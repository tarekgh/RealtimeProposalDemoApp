// Gemini Realtime session adapted from https://github.com/jeffhandley/googleapis-dotnet-genai/pull/1

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.GenAI;
using Google.GenAI.Types;

#pragma warning disable MEAI001 // Experimental AI API

namespace Microsoft.Extensions.AI;

/// <summary>
/// Provides an <see cref="IRealtimeClientSession"/> implementation for Google GenAI's Live API,
/// wrapping an <see cref="AsyncSession"/> WebSocket connection.
/// </summary>
public sealed class GoogleGenAIRealtimeSession : IRealtimeClientSession
{
    private readonly AsyncSession _asyncSession;
    private readonly ChatClientMetadata _metadata;
    private int _disposed;

    // Buffer for audio chunks between Append and Commit.
    private readonly List<byte[]> _audioBuffer = new();
    private readonly object _audioBufferLock = new();
    private int _audioBufferSize;

    /// <summary>Maximum buffered audio size (10 MB).</summary>
    private const int MaxAudioBufferBytes = 10 * 1024 * 1024;

    // Track whether a response is in progress to emit ResponseCreated only once per response.
    private bool _responseInProgress;

    // Track whether audio was sent via SendRealtimeInputAsync to avoid mixing with SendClientContentAsync.
    private bool _lastInputWasRealtime;

    /// <inheritdoc />
    public RealtimeSessionOptions? Options { get; private set; }

    /// <summary>Initializes a new instance wrapping a connected <see cref="AsyncSession"/>.</summary>
    internal GoogleGenAIRealtimeSession(
        AsyncSession asyncSession,
        string model,
        RealtimeSessionOptions? initialOptions)
    {
        _asyncSession = asyncSession ?? throw new ArgumentNullException(nameof(asyncSession));
        _metadata = new ChatClientMetadata("google-genai", defaultModelId: model);
        Options = initialOptions;
    }

    /// <inheritdoc />
    public async Task SendAsync(
        RealtimeClientMessage message,
        CancellationToken cancellationToken = default)
    {
        if (message is null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        cancellationToken.ThrowIfCancellationRequested();

        switch (message)
        {
            case InputAudioBufferAppendRealtimeClientMessage audioAppend:
                await HandleAudioAppendAsync(audioAppend, cancellationToken).ConfigureAwait(false);
                break;

            case InputAudioBufferCommitRealtimeClientMessage:
                await HandleAudioCommitAsync(cancellationToken).ConfigureAwait(false);
                break;

            case CreateConversationItemRealtimeClientMessage itemCreate:
                await HandleConversationItemCreateAsync(itemCreate, cancellationToken).ConfigureAwait(false);
                break;

            case SessionUpdateRealtimeClientMessage sessionUpdate:
                Options = sessionUpdate.Options ?? throw new ArgumentNullException(nameof(sessionUpdate.Options));
                break;

            case CreateResponseRealtimeClientMessage:
                if (!_lastInputWasRealtime)
                {
                    await _asyncSession.SendClientContentAsync(
                        new LiveSendClientContentParameters { TurnComplete = true },
                        cancellationToken).ConfigureAwait(false);
                }
                break;

            default:
                break;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RealtimeServerMessage> GetStreamingResponseAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            LiveServerMessage? serverMessage;
            serverMessage = await _asyncSession.ReceiveAsync(cancellationToken).ConfigureAwait(false);

            if (serverMessage is null)
            {
                yield break;
            }

            foreach (var mapped in MapServerMessage(serverMessage))
            {
                yield return mapped;
            }
        }
    }

    /// <inheritdoc />
    public object? GetService(System.Type serviceType, object? serviceKey = null)
    {
        if (serviceType is null)
        {
            throw new ArgumentNullException(nameof(serviceType));
        }

        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(ChatClientMetadata))
        {
            return _metadata;
        }

        if (serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        if (serviceType.IsInstanceOfType(_asyncSession))
        {
            return _asyncSession;
        }

        return null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _asyncSession.DisposeAsync().ConfigureAwait(false);
    }

    #region Send Helpers (MEAI → Google GenAI)

    private Task HandleAudioAppendAsync(
        InputAudioBufferAppendRealtimeClientMessage audioAppend,
        CancellationToken cancellationToken)
    {
        if (audioAppend.Content is null || !audioAppend.Content.HasTopLevelMediaType("audio"))
        {
            return Task.CompletedTask;
        }

        byte[] audioBytes = ExtractDataBytes(audioAppend.Content);

        lock (_audioBufferLock)
        {
            if (_audioBufferSize + audioBytes.Length > MaxAudioBufferBytes)
            {
                throw new InvalidOperationException(
                    $"Audio buffer would exceed {MaxAudioBufferBytes} bytes. " +
                    "Call AudioBufferCommit before appending more audio.");
            }

            _audioBuffer.Add(audioBytes);
            _audioBufferSize += audioBytes.Length;
        }

        return Task.CompletedTask;
    }

    private async Task HandleAudioCommitAsync(CancellationToken cancellationToken)
    {
        List<byte[]> bufferedChunks;
        lock (_audioBufferLock)
        {
            if (_audioBuffer.Count == 0)
            {
                return;
            }

            bufferedChunks = new List<byte[]>(_audioBuffer);
            _audioBuffer.Clear();
            _audioBufferSize = 0;
        }

        _lastInputWasRealtime = true;

        // Explicit ActivityStart/ActivityEnd framing required with automatic VAD disabled.
        await _asyncSession.SendRealtimeInputAsync(
            new LiveSendRealtimeInputParameters
            {
                ActivityStart = new ActivityStart()
            },
            cancellationToken).ConfigureAwait(false);

        const int maxFrameBytes = 32_000;
        foreach (var buffered in bufferedChunks)
        {
            if (buffered.Length <= maxFrameBytes)
            {
                await SendAudioFrameAsync(buffered, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                for (int i = 0; i < buffered.Length; i += maxFrameBytes)
                {
                    int len = Math.Min(maxFrameBytes, buffered.Length - i);
                    byte[] frame = new byte[len];
                    Buffer.BlockCopy(buffered, i, frame, 0, len);
                    await SendAudioFrameAsync(frame, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        await _asyncSession.SendRealtimeInputAsync(
            new LiveSendRealtimeInputParameters
            {
                ActivityEnd = new ActivityEnd()
            },
            cancellationToken).ConfigureAwait(false);
    }

    private Task SendAudioFrameAsync(byte[] data, CancellationToken cancellationToken)
    {
        return _asyncSession.SendRealtimeInputAsync(
            new LiveSendRealtimeInputParameters
            {
                Audio = new Blob
                {
                    Data = data,
                    MimeType = "audio/pcm",
                }
            },
            cancellationToken);
    }

    private async Task HandleConversationItemCreateAsync(
        CreateConversationItemRealtimeClientMessage itemCreate,
        CancellationToken cancellationToken)
    {
        if (itemCreate.Item?.Contents is null or { Count: 0 })
        {
            return;
        }

        var firstContent = itemCreate.Item.Contents[0];

        // Function result
        if (firstContent is FunctionResultContent functionResult)
        {
            var response = new FunctionResponse
            {
                Id = functionResult.CallId,
                Name = string.Empty,
                Response = new Dictionary<string, object>
                {
                    ["result"] = functionResult.Result?.ToString() ?? string.Empty
                }
            };

            await _asyncSession.SendToolResponseAsync(
                new LiveSendToolResponseParameters
                {
                    FunctionResponses = new List<FunctionResponse> { response }
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        // Text/content conversation input
        var parts = new List<Part>();
        foreach (var content in itemCreate.Item.Contents)
        {
            if (content is TextContent textContent && !string.IsNullOrEmpty(textContent.Text))
            {
                parts.Add(new Part { Text = textContent.Text });
            }
            else if (content is DataContent dataContent)
            {
                if (dataContent.HasTopLevelMediaType("audio"))
                {
                    parts.Add(new Part
                    {
                        InlineData = new Blob
                        {
                            Data = ExtractDataBytes(dataContent),
                            MimeType = dataContent.MediaType ?? "audio/pcm",
                        }
                    });
                }
                else if (dataContent.HasTopLevelMediaType("image"))
                {
                    parts.Add(new Part
                    {
                        InlineData = new Blob
                        {
                            Data = ExtractDataBytes(dataContent),
                            MimeType = dataContent.MediaType ?? "image/png",
                        }
                    });
                }
            }
        }

        if (parts.Count == 0)
        {
            return;
        }

        string role = itemCreate.Item.Role?.Value switch
        {
            "assistant" => "model",
            _ => "user",
        };

        _lastInputWasRealtime = false;
        await _asyncSession.SendClientContentAsync(
            new LiveSendClientContentParameters
            {
                Turns = new List<Content>
                {
                    new Content
                    {
                        Parts = parts,
                        Role = role,
                    }
                },
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static byte[] ExtractDataBytes(DataContent content)
    {
        string? dataUri = content.Uri?.ToString();

        if (dataUri is not null)
        {
            int commaIndex = dataUri.LastIndexOf(',');
            if (commaIndex >= 0 && commaIndex < dataUri.Length - 1)
            {
                string base64 = dataUri.Substring(commaIndex + 1);
                return Convert.FromBase64String(base64);
            }
        }

        return content.Data.ToArray();
    }

    #endregion

    #region Receive Helpers (Google GenAI → MEAI)

    private IEnumerable<RealtimeServerMessage> MapServerMessage(LiveServerMessage serverMessage)
    {
        // SetupComplete — internal protocol message, skip
        if (serverMessage.SetupComplete is not null)
        {
            yield break;
        }

        // Server content (model responses — audio, text, transcription)
        if (serverMessage.ServerContent is { } serverContent)
        {
            foreach (var msg in MapServerContent(serverContent, serverMessage))
            {
                yield return msg;
            }
        }

        // Tool calls
        if (serverMessage.ToolCall is { FunctionCalls: { Count: > 0 } functionCalls })
        {
            if (!_responseInProgress)
            {
                _responseInProgress = true;
                yield return new ResponseCreatedRealtimeServerMessage(RealtimeServerMessageType.ResponseCreated)
                {
                    RawRepresentation = serverMessage,
                };
            }

            foreach (var fc in functionCalls)
            {
                var contents = new List<AIContent>
                {
                    new FunctionCallContent(
                        fc.Id ?? string.Empty,
                        fc.Name ?? string.Empty,
                        fc.Args?.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value))
                };

                var item = new RealtimeConversationItem(contents, id: fc.Id, role: ChatRole.Assistant);

                yield return new ResponseOutputItemRealtimeServerMessage(RealtimeServerMessageType.ResponseOutputItemAdded)
                {
                    Item = item,
                    RawRepresentation = serverMessage,
                };

                yield return new ResponseOutputItemRealtimeServerMessage(RealtimeServerMessageType.ResponseOutputItemDone)
                {
                    Item = item,
                    RawRepresentation = serverMessage,
                };
            }
        }

        // Tool call cancellation
        if (serverMessage.ToolCallCancellation is { Ids: { Count: > 0 } })
        {
            yield return new RealtimeServerMessage
            {
                Type = RealtimeServerMessageType.RawContentOnly,
                RawRepresentation = serverMessage,
            };
        }

        // Usage metadata
        if (serverMessage.UsageMetadata is { } usage)
        {
            yield return new ResponseCreatedRealtimeServerMessage(RealtimeServerMessageType.ResponseDone)
            {
                Usage = new UsageDetails
                {
                    InputTokenCount = usage.PromptTokenCount ?? 0,
                    OutputTokenCount = usage.ResponseTokenCount ?? 0,
                    TotalTokenCount = usage.TotalTokenCount ?? 0,
                },
                RawRepresentation = serverMessage,
            };
        }

        // GoAway (server disconnect)
        if (serverMessage.GoAway is not null)
        {
            yield return new ErrorRealtimeServerMessage
            {
                Error = new ErrorContent("Server is disconnecting (GoAway)"),
                RawRepresentation = serverMessage,
            };
        }
    }

    private IEnumerable<RealtimeServerMessage> MapServerContent(
        LiveServerContent serverContent,
        LiveServerMessage rawMessage)
    {
        if (serverContent.ModelTurn?.Parts is { Count: > 0 } parts)
        {
            if (!_responseInProgress)
            {
                _responseInProgress = true;
                yield return new ResponseCreatedRealtimeServerMessage(RealtimeServerMessageType.ResponseCreated)
                {
                    RawRepresentation = rawMessage,
                };
            }

            foreach (var part in parts)
            {
                // Audio data
                if (part.InlineData is { Data: not null } blob &&
                    blob.MimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true)
                {
                    yield return new OutputTextAudioRealtimeServerMessage(RealtimeServerMessageType.OutputAudioDelta)
                    {
                        Audio = Convert.ToBase64String(blob.Data),
                        RawRepresentation = rawMessage,
                    };
                }

                // Text response
                if (!string.IsNullOrEmpty(part.Text))
                {
                    yield return new OutputTextAudioRealtimeServerMessage(RealtimeServerMessageType.OutputTextDelta)
                    {
                        Text = part.Text,
                        RawRepresentation = rawMessage,
                    };
                }
            }
        }

        // Input transcription
        if (serverContent.InputTranscription is { Text: not null } inputTranscription)
        {
            yield return new InputAudioTranscriptionRealtimeServerMessage(RealtimeServerMessageType.InputAudioTranscriptionCompleted)
            {
                Transcription = inputTranscription.Text,
                RawRepresentation = rawMessage,
            };
        }

        // Output transcription
        if (serverContent.OutputTranscription is { Text: not null } outputTranscription)
        {
            yield return new OutputTextAudioRealtimeServerMessage(RealtimeServerMessageType.OutputAudioTranscriptionDelta)
            {
                Text = outputTranscription.Text,
                RawRepresentation = rawMessage,
            };
        }

        // Turn complete or generation complete — reset response tracking and emit ResponseDone
        if (serverContent.TurnComplete == true || serverContent.GenerationComplete == true)
        {
            _responseInProgress = false;
            yield return new ResponseCreatedRealtimeServerMessage(RealtimeServerMessageType.ResponseDone)
            {
                RawRepresentation = rawMessage,
            };
        }
    }

    #endregion

    #region Tool Mapping Helpers

    /// <summary>
    /// Converts an <see cref="AIFunction"/> to a Google GenAI <see cref="FunctionDeclaration"/>.
    /// </summary>
    internal static FunctionDeclaration ToGoogleFunctionDeclaration(AIFunction aiFunction)
    {
        var declaration = new FunctionDeclaration
        {
            Name = aiFunction.Name,
            Description = aiFunction.Description,
        };

        if (aiFunction.JsonSchema is JsonElement schemaElement &&
            schemaElement.ValueKind != JsonValueKind.Undefined)
        {
            declaration.ParametersJsonSchema = schemaElement;
        }

        return declaration;
    }

    #endregion
}

#pragma warning restore MEAI001
