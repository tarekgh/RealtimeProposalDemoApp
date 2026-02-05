using Microsoft.Extensions.AI;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace RealtimePlayGround
{
    public class OpenAIRealtimeClient : IRealtimeClient
    {
        private readonly string _apiKey;
        private readonly string _model;

        // Events for server responses
        public event EventHandler<ServerEventArgs>? ServerEventReceived;
        public event EventHandler<string>? ErrorOccurred;

        public OpenAIRealtimeClient(string apiKey, string model = "gpt-realtime")
        {
            _apiKey = apiKey;
            _model = model;
        }

        // Create a new session
        public async Task<IRealtimeSession?> CreateSessionAsync()
        {
            try
            {
                var session = new OpenAIRealtimeSession(_apiKey, _model);

                // Forward events from session to client
                session.ServerEventReceived += (s, e) => ServerEventReceived?.Invoke(this, e);
                session.ErrorOccurred += (s, e) => ErrorOccurred?.Invoke(this, e);

                var connected = await session.ConnectAsync();
                if (connected)
                {
                    return session;
                }
                else
                {
                    session.Dispose();
                    return null;
                }
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Failed to create session: {ex.Message}");
                return null;
            }
        }

        public void Dispose()
        {
            // Client itself has no resources to dispose
            // Sessions are disposed independently
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<IRealtimeSession?> CreateSessionAsync(RealtimeSessionOptions? options = null, CancellationToken cancellationToken = default)
            => CreateSessionAsync();
    }

    // Session implementation
    public class OpenAIRealtimeSession : IRealtimeSession
    {
        private ClientWebSocket? _webSocket;
        private CancellationTokenSource? _cancellationTokenSource;
        private readonly string _apiKey;
        private readonly string _model;
        private bool _isConnected;
        private readonly StringBuilder _partialMessageBuilder = new();
        private Channel<RealtimeServerMessage>? _eventChannel;
        private RealtimeSessionOptions? _options;

        // Events for server responses
        public event EventHandler<ServerEventArgs>? ServerEventReceived;
        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler? SessionCreated;
        public event EventHandler? SessionEnded;

        public bool IsConnected => _isConnected;

        public OpenAIRealtimeSession(string apiKey, string model)
        {
            _apiKey = apiKey;
            _model = model;
            _eventChannel = Channel.CreateUnbounded<RealtimeServerMessage>();
        }

        public RealtimeSessionOptions? Options => _options;

        internal async Task<bool> ConnectAsync()
        {
            try
            {
                _webSocket = new ClientWebSocket();
                _webSocket.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");

                _cancellationTokenSource = new CancellationTokenSource();

                var uri = new Uri($"wss://api.openai.com/v1/realtime?model={_model}");
                await _webSocket.ConnectAsync(uri, _cancellationTokenSource.Token);

                _isConnected = true;
                SessionCreated?.Invoke(this, EventArgs.Empty);

                // Start receiving messages
                _ = Task.Run(() => ReceiveMessagesAsync(_cancellationTokenSource.Token));

                return true;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Failed to connect session: {ex.Message}");
                return false;
            }
        }

        public async Task UpdateAsync(RealtimeSessionOptions options, CancellationToken cancellationToken = default)
        {
            var sessionElement = new JsonObject
            {
                ["type"] = "session.update"
            };
            var sessionObject = new JsonObject();
            sessionElement["session"] = sessionObject;

            var audioElement = new JsonObject();
            var audioInputElement = new JsonObject();
            var audioOutputElement = new JsonObject();
            sessionObject["audio"] = audioElement;
            audioElement["input"] = audioInputElement;
            audioElement["output"] = audioOutputElement;

            if (options.InputAudioFormat is not null)
            {
                var audioInputFormatElement = new JsonObject
                {
                    ["type"] = options.InputAudioFormat.Type
                };
                if (options.InputAudioFormat.SampleRate.HasValue)
                {
                    audioInputFormatElement["rate"] = options.InputAudioFormat.SampleRate.Value;
                }
                audioInputElement["format"] = audioInputFormatElement;
            }

            if (options.NoiseReductionOptions.HasValue)
            {
                var noiseReductionObj = new JsonObject
                {
                    ["type"] = options.NoiseReductionOptions.Value == NoiseReductionOptions.NearField ? "near_field" : "far_field"
                };
                audioInputElement["noise_reduction"] = noiseReductionObj;
            }

            if (options.TranscriptionOptions is not null)
            {
                var transcriptionOptionsObj = new JsonObject
                {
                    ["language"] = options.TranscriptionOptions.Language,
                    ["model"] = options.TranscriptionOptions.Model
                };
                if (options.TranscriptionOptions.Prompt is not null)
                {
                    transcriptionOptionsObj["prompt"] = options.TranscriptionOptions.Prompt;
                }
                audioInputElement["transcription"] = transcriptionOptionsObj;
            }

            if (options.VoiceActivityDetection is ServerVoiceActivityDetection serverVad)
            {
                audioInputElement["turn_detection"] = new JsonObject
                {
                    ["type"] = "server_vad",
                    ["create_response"] = serverVad.CreateResponse,
                    ["idle_timeout_ms"] = serverVad.IdleTimeoutInMilliseconds,
                    ["interrupt_response"] = serverVad.InterruptResponse,
                    ["prefix_padding_ms"] = serverVad.PrefixPaddingInMilliseconds,
                    ["silence_duration_ms"] = serverVad.SilenceDurationInMilliseconds,
                    ["threshold"] = serverVad.Threshold
                };
            }
            else if (options.VoiceActivityDetection is SemanticVoiceActivityDetection semanticVad)
            {
                audioInputElement["turn_detection"] = new JsonObject
                {
                    ["type"] = "semantic_vad",
                    ["create_response"] = semanticVad.CreateResponse,
                    ["interrupt_response"] = semanticVad.InterruptResponse,
                    ["eagerness"] = semanticVad.Eagerness
                };
            }

            if (options.SessionKind == RealtimeSessionKind.Realtime)
            {
                sessionObject["type"] = "realtime";

                if (options.OutputAudioFormat is not null)
                {
                    var audioOutputFormatElement = new JsonObject
                    {
                        ["type"] = options.OutputAudioFormat.Type
                    };
                    if (options.OutputAudioFormat.SampleRate.HasValue)
                    {
                        audioOutputFormatElement["rate"] = options.OutputAudioFormat.SampleRate.Value;
                    }
                    audioOutputElement["format"] = audioOutputFormatElement;
                }

                audioOutputElement["speed"] = options.VoiceSpeed;

                if (options.Voice is not null)
                {
                    audioOutputElement["voice"] = options.Voice;
                }

                if (options.Instructions is not null)
                {
                    sessionObject["instructions"] = options.Instructions;
                }

                if (options.MaxOutputTokens.HasValue)
                {
                    sessionObject["max_output_tokens"] = options.MaxOutputTokens.Value;
                }

                if (options.Model is not null)
                {
                    sessionObject["model"] = options.Model;
                }

                if (options.OutputModalities is not null && options.OutputModalities.Any())
                {
                    sessionObject["output_modalities"] = CreateModalitiesArray(options.OutputModalities);
                }

                if (options.Tools is not null)
                {
                    var toolsArray = new JsonArray();
                    foreach (var tool in options.Tools)
                    {
                        var toolObj = SerializeAIFunctionToJson(tool as AIFunction);
                        if (toolObj is not null)
                        {
                            toolsArray.Add(toolObj);
                        }
                    }
                    sessionObject["tools"] = toolsArray;
                }
            }
            else if (options.SessionKind == RealtimeSessionKind.Transcription)
            {
                sessionObject["type"] = "transcription";
            }

            _options = options;

            await SendEventAsync(sessionElement);
        }

        public async Task EndSessionAsync()
        {
            try
            {
                if (_webSocket?.State == WebSocketState.Open)
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session ended", CancellationToken.None);
                }

                _isConnected = false;
                SessionEnded?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Error ending session: {ex.Message}");
            }
        }

        public Task SendAudioAsync(string base64Audio)
            => SendEventAsync(new JsonObject
            {
                ["type"] = "input_audio_buffer.append",
                ["audio"] = base64Audio
            });

        public Task CommitAudioAsync()
            => SendEventAsync(new JsonObject { ["type"] = "input_audio_buffer.commit" });

        public Task CreateResponseAsync()
            => SendEventAsync(new JsonObject
            {
                ["type"] = "response.create",
                ["response"] = new JsonObject
                {
                    ["instructions"] = "Respond to the the audio input.",
                }
            });

        public Task ClearAudioBufferAsync()
            => SendEventAsync(new JsonObject { ["type"] = "input_audio_buffer.clear" });

        public async Task InjectClientMessageAsync(RealtimeClientMessage message, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(message);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            JsonObject? jsonMessage = new JsonObject();

            if (message.EventId is not null)
            {
                jsonMessage["event_id"] = message.EventId;
            }

            // Handle different RealtimeClientMessage types
            switch (message)
            {
                case RealtimeClientResponseCreateMessage responseCreate:
                    jsonMessage["type"] = "response.create";

                    var responseObj = new JsonObject();

                    // Handle OutputAudioOptions (audio.output.format)
                    if (responseCreate.OutputAudioOptions is not null)
                    {
                        var audioObj = new JsonObject();
                        var outputObj = new JsonObject();
                        var formatObj = new JsonObject();

                        switch (responseCreate.OutputAudioOptions.Type)
                        {
                            case "audio/pcm":
                                if (responseCreate.OutputAudioOptions.SampleRate == 24000)
                                {
                                    formatObj["type"] = responseCreate.OutputAudioOptions.Type;
                                    formatObj["rate"] = 24000;
                                }
                                break;

                            case "audio/pcmu":
                            case "audio/pcma":
                                formatObj["type"] = responseCreate.OutputAudioOptions.Type;
                                break;
                        }

                        outputObj["format"] = formatObj;

                        if (!string.IsNullOrEmpty(responseCreate.OutputVoice))
                        {
                            outputObj["voice"] = responseCreate.OutputVoice;
                        }

                        audioObj["output"] = outputObj;
                        responseObj["audio"] = audioObj;
                    }
                    else if (!string.IsNullOrEmpty(responseCreate.OutputVoice))
                    {
                        responseObj["audio"] = new JsonObject
                        {
                            ["output"] = new JsonObject
                            {
                                ["voice"] = responseCreate.OutputVoice
                            }
                        };
                    }

                    responseObj["conversation"] = responseCreate.ExcludeFromConversation ? "none" : "auto";

                    if (responseCreate.Items is { } items && items.Any())
                    {
                        var inputArray = new JsonArray();
                        foreach (var item in items)
                        {
                            if (item is RealtimeContentItem contentItem && contentItem.Contents is not null)
                            {
                                var itemObj = new JsonObject
                                {
                                    ["type"] = "message",
                                    ["object"] = "realtime.item"
                                };

                                if (contentItem.Role.HasValue)
                                {
                                    itemObj["role"] = contentItem.Role.Value.Value;
                                }

                                if (contentItem.Id is not null)
                                {
                                    itemObj["id"] = contentItem.Id;
                                }

                                itemObj["content"] = SerializeContentsToJsonArray(contentItem.Contents);
                                inputArray.Add(itemObj);
                            }
                        }
                        responseObj["input"] = inputArray;
                    }

                    if (!string.IsNullOrEmpty(responseCreate.Instructions))
                    {
                        responseObj["instructions"] = responseCreate.Instructions;
                    }

                    if (responseCreate.MaxOutputTokens.HasValue)
                    {
                        responseObj["max_output_tokens"] = responseCreate.MaxOutputTokens.Value;
                    }

                    if (responseCreate.Metadata is { Count: > 0 })
                    {
                        var metadataObj = new JsonObject();
                        foreach (var kvp in responseCreate.Metadata)
                        {
                            metadataObj[kvp.Key] = JsonValue.Create(kvp.Value);
                        }
                        responseObj["metadata"] = metadataObj;
                    }

                    if (responseCreate.OutputModalities is not null && responseCreate.OutputModalities.Any())
                    {
                        responseObj["output_modalities"] = CreateModalitiesArray(responseCreate.OutputModalities);
                    }

                    if (responseCreate.AIFunction is not null)
                    {
                        responseObj["tool_choice"] = new JsonObject
                        {
                            ["type"] = "function",
                            ["name"] = responseCreate.AIFunction.Name
                        };
                    }
                    else if (responseCreate.HostedMcpServerTool is not null)
                    {
                        responseObj["tool_choice"] = new JsonObject
                        {
                            ["type"] = "mcp",
                            ["server_label"] = responseCreate.HostedMcpServerTool.ServerName,
                            ["name"] = responseCreate.HostedMcpServerTool.Name
                        };
                    }
                    else if (responseCreate.ToolChoiceMode.HasValue)
                    {
                        responseObj["tool_choice"] = responseCreate.ToolChoiceMode.Value switch
                        {
                            ToolChoiceMode.None => "none",
                            ToolChoiceMode.Auto => "auto",
                            ToolChoiceMode.Required => "required",
                            _ => "auto"
                        };
                    }

                    if (responseCreate.Tools is not null && responseCreate.Tools.Any())
                    {
                        var toolsArray = new JsonArray();

                        foreach (var tool in responseCreate.Tools)
                        {
                            JsonObject? toolObj = null;

                            if (tool is AIFunction aiFunction && !string.IsNullOrEmpty(aiFunction.Name))
                            {
                                toolObj = SerializeAIFunctionToJson(aiFunction);
                            }
                            else
                            {
                                var toolJson = JsonSerializer.SerializeToNode(tool);

                                if (toolJson is not null &&
                                    (toolJson["server_label"] is not null || toolJson["server_url"] is not null || toolJson["connector_id"] is not null))
                                {
                                    toolObj = new JsonObject { ["type"] = "mcp" };

                                    CopyJsonPropertyIfExists(toolJson, toolObj, "server_label");
                                    CopyJsonPropertyIfExists(toolJson, toolObj, "server_url");
                                    CopyJsonPropertyIfExists(toolJson, toolObj, "connector_id");
                                    CopyJsonPropertyIfExists(toolJson, toolObj, "authorization");
                                    CopyJsonPropertyIfExists(toolJson, toolObj, "headers");
                                    CopyJsonPropertyIfExists(toolJson, toolObj, "require_approval");
                                    CopyJsonPropertyIfExists(toolJson, toolObj, "server_description");
                                    CopyJsonPropertyIfExists(toolJson, toolObj, "allowed_tools");
                                }
                            }

                            if (toolObj is not null)
                            {
                                toolsArray.Add(toolObj);
                            }
                        }

                        responseObj["tools"] = toolsArray;
                    }

                    jsonMessage["response"] = responseObj;
                    break;

                case RealtimeClientConversationItemCreateMessage itemCreate:
                    if (itemCreate.Item is not null)
                    {
                        jsonMessage["type"] = "conversation.item.create";

                        if (itemCreate.PreviousId is not null)
                        {
                            jsonMessage["previous_item_id"] = itemCreate.PreviousId;
                        }

                        if (itemCreate.Item is RealtimeContentItem contentItem && contentItem.Contents is not null)
                        {
                            var itemObj = new JsonObject();

                            if (contentItem.Id is not null)
                            {
                                itemObj["id"] = contentItem.Id;
                            }

                            if (contentItem.Contents.Count > 0 && contentItem.Contents[0] is FunctionResultContent functionResult)
                            {
                                itemObj["type"] = "function_call_output";
                                itemObj["call_id"] = functionResult.CallId;
                                itemObj["output"] = functionResult?.Result?.ToString();
                            }
                            else if (contentItem.Contents.Count > 0 && contentItem.Contents[0] is FunctionCallContent functionCall)
                            {
                                itemObj["type"] = "function_call";
                                itemObj["call_id"] = functionCall.CallId;
                                itemObj["name"] = functionCall.Name;

                                if (functionCall.Arguments is not null)
                                {
                                    itemObj["arguments"] = JsonSerializer.Serialize(functionCall.Arguments);
                                }
                            }
                            else
                            {
                                itemObj["type"] = "message";

                                if (contentItem.Role.HasValue)
                                {
                                    itemObj["role"] = contentItem.Role.Value.Value;
                                }

                                itemObj["content"] = SerializeContentsToJsonArray(contentItem.Contents);
                            }

                            jsonMessage["item"] = itemObj;
                        }
                    }
                    break;

                case RealtimeClientInputAudioBufferAppendMessage audioAppend:
                    if (audioAppend.Content is not null && audioAppend.Content.MediaType.StartsWith("audio/"))
                    {
                        jsonMessage["type"] = "input_audio_buffer.append";

                        string dataUri = audioAppend.Content.Uri?.ToString() ?? string.Empty;
                        int commaIndex = dataUri.LastIndexOf(',');

                        jsonMessage["audio"] = commaIndex >= 0 && commaIndex < dataUri.Length - 1
                            ? dataUri[(commaIndex + 1)..]
                            : Convert.ToBase64String(audioAppend.Content.Data.ToArray());
                    }
                    break;

                case RealtimeClientInputAudioBufferCommitMessage:
                    jsonMessage["type"] = "input_audio_buffer.commit";
                    break;

                default:
                    if (message.RawRepresentation is string rawString)
                    {
                        jsonMessage = JsonSerializer.Deserialize<JsonObject>(rawString);
                    }
                    else if (message.RawRepresentation is JsonObject rawJsonObject)
                    {
                        jsonMessage = rawJsonObject;
                    }
                    break;
            }

            if (jsonMessage?.TryGetPropertyValue("type", out _) is true)
            {
                await SendEventAsync(jsonMessage);
            }
        }

        public async IAsyncEnumerable<RealtimeServerMessage> GetStreamingResponseAsync(
            IAsyncEnumerable<RealtimeClientMessage> updates,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (_eventChannel == null)
                yield break;

            var processUpdatesTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var message in updates.WithCancellation(cancellationToken))
                    {
                        await InjectClientMessageAsync(message, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke(this, $"Error processing updates: {ex.Message}");
                }
            }, cancellationToken);

            await foreach (var serverEvent in _eventChannel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return serverEvent;
            }

            await processUpdatesTask;
        }

        private async Task SendEventAsync(JsonObject eventData)
        {
            if (_webSocket?.State != WebSocketState.Open)
            {
                ErrorOccurred?.Invoke(this, "WebSocket is not connected");
                return;
            }

            try
            {
                var json = eventData.ToJsonString();
                var bytes = Encoding.UTF8.GetBytes(json);
                await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Error sending event: {ex.Message}");
            }
        }

        private async Task ReceiveMessagesAsync(CancellationToken cancellationToken)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 16);
            try
            {
                while (_webSocket?.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await EndSessionAsync();
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        ProcessServerEvent(message);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Error receiving messages: {ex}");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private void ProcessServerEvent(string message)
        {
            try
            {
                if (_partialMessageBuilder.Length > 0)
                {
                    _partialMessageBuilder.Append(message);
                    message = _partialMessageBuilder.ToString();
                    _partialMessageBuilder.Clear();
                }

                if (message.EndsWith('}'))
                {
                    var jsonDoc = JsonDocument.Parse(message);
                    var root = jsonDoc.RootElement;
                    var eventType = root.GetProperty("type").GetString();

                    var eventArgs = new ServerEventArgs
                    {
                        EventType = eventType ?? "unknown",
                        Data = jsonDoc
                    };

                    ServerEventReceived?.Invoke(this, eventArgs);

                    try
                    {
                        var serverMessage = eventType switch
                        {
                            "error" => CreateErrorMessage(root),
                            "conversation.item.input_audio_transcription.delta" or
                            "conversation.item.input_audio_transcription.completed" or
                            "conversation.item.input_audio_transcription.failed" =>
                                CreateInputAudioTranscriptionMessage(root, eventType),
                            "response.output_audio_transcript.delta" or
                            "response.output_audio_transcript.done" or
                            "response.output_audio.delta" or
                            "response.output_audio.done" =>
                                CreateOutputTextAudioMessage(root, eventType),
                            "response.created" or
                            "response.done" =>
                                CreateResponseCreatedMessage(root, eventType),
                            "response.output_item.added" or
                            "response.output_item.done" =>
                                CreateResponseOutItemMessage(root, eventType),
                            _ => new RealtimeServerMessage
                            {
                                Type = RealtimeServerMessageType.RawContentOnly,
                                RawRepresentation = root.Clone()
                            }
                        };

                        if (serverMessage is not null)
                        {
                            _eventChannel?.Writer.TryWrite(serverMessage);
                        }
                    }
                    catch { }
                }
                else
                {
                    _partialMessageBuilder.Append(message);
                }
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Error processing server event: {ex}\n{message}");
            }
        }

        public void Dispose()
        {
            _eventChannel?.Writer.Complete();
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
            _webSocket?.Dispose();
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            if (serviceType == typeof(OpenAIRealtimeClient))
                return this;

            if (serviceType == typeof(ChatClientMetadata))
                return new ChatClientMetadata("OpenAI", null, "realtime");

            return null;
        }

        public TService? GetService<TService>(object? key = null) where TService : class
            => GetService(typeof(TService), key) as TService;

        #region Helper Methods

        private static void CopyJsonPropertyIfExists(JsonNode? source, JsonObject target, string propertyName)
        {
            if (source?[propertyName] is JsonNode value)
            {
                target[propertyName] = value.DeepClone();
            }
        }

        private static JsonArray CreateModalitiesArray(IEnumerable<string> modalities)
            => new([.. modalities.Select(m => JsonValue.Create(m))]);

        private static JsonObject? SerializeAIFunctionToJson(AIFunction? aiFunction)
        {
            if (aiFunction is null || string.IsNullOrEmpty(aiFunction.Name))
                return null;

            var toolObj = new JsonObject
            {
                ["type"] = "function",
                ["name"] = aiFunction.Name
            };

            if (!string.IsNullOrEmpty(aiFunction.Description))
            {
                toolObj["description"] = aiFunction.Description;
            }

            toolObj["parameters"] = JsonNode.Parse(aiFunction.JsonSchema.GetRawText());
            return toolObj;
        }

        private static JsonArray SerializeContentsToJsonArray(IEnumerable<AIContent> contents)
        {
            var contentsArray = new JsonArray();

            foreach (var content in contents)
            {
                if (content is TextContent textContent)
                {
                    contentsArray.Add(new JsonObject
                    {
                        ["type"] = "input_text",
                        ["text"] = textContent.Text
                    });
                }
                else if (content is DataContent dataContent)
                {
                    if (dataContent.MediaType.StartsWith("audio/"))
                    {
                        contentsArray.Add(new JsonObject
                        {
                            ["type"] = "input_audio",
                            ["audio"] = dataContent.Base64Data.ToString()
                        });
                    }
                    else if (dataContent.MediaType.StartsWith("image/"))
                    {
                        contentsArray.Add(new JsonObject
                        {
                            ["type"] = "input_image",
                            ["image_url"] = dataContent.Uri
                        });
                    }
                }
            }

            return contentsArray;
        }

        private static ChatRole? ParseChatRole(string? roleString) => roleString switch
        {
            "assistant" => ChatRole.Assistant,
            "user" => ChatRole.User,
            "system" => ChatRole.System,
            _ => null
        };

        private static UsageDetails? ParseUsageDetails(JsonElement usageElement, bool requireTypeCheck = false)
        {
            if (usageElement.ValueKind != JsonValueKind.Object)
                return null;

            if (requireTypeCheck &&
                (!usageElement.TryGetProperty("type", out var usageTypeElement) ||
                 usageTypeElement.GetString() != "tokens"))
            {
                return null;
            }

            var usageData = new UsageDetails();

            if (usageElement.TryGetProperty("input_tokens", out var inputTokensElement))
                usageData.InputTokenCount = inputTokensElement.GetInt32();

            if (usageElement.TryGetProperty("output_tokens", out var outputTokensElement))
                usageData.OutputTokenCount = outputTokensElement.GetInt32();

            if (usageElement.TryGetProperty("total_tokens", out var totalTokensElement))
                usageData.TotalTokenCount = totalTokensElement.GetInt32();

            if (usageElement.TryGetProperty("input_token_details", out var inputTokenDetailsElement) &&
                inputTokenDetailsElement.ValueKind == JsonValueKind.Object)
            {
                if (inputTokenDetailsElement.TryGetProperty("audio_tokens", out var audioTokensElement))
                    usageData.InputAudioTokenCount = audioTokensElement.GetInt32();

                if (inputTokenDetailsElement.TryGetProperty("text_tokens", out var textTokensElement))
                    usageData.InputTextTokenCount = textTokensElement.GetInt32();
            }

            if (usageElement.TryGetProperty("output_token_details", out var outputTokenDetailsElement) &&
                outputTokenDetailsElement.ValueKind == JsonValueKind.Object)
            {
                if (outputTokenDetailsElement.TryGetProperty("audio_tokens", out var audioTokensElement))
                    usageData.OutputAudioTokenCount = audioTokensElement.GetInt32();

                if (outputTokenDetailsElement.TryGetProperty("text_tokens", out var textTokensElement))
                    usageData.OutputTextTokenCount = textTokensElement.GetInt32();
            }

            return usageData;
        }

        private static List<AIContent> ParseContentItems(JsonElement contentElements)
        {
            if (contentElements.ValueKind != JsonValueKind.Array)
                return [];

            var contentList = new List<AIContent>();

            foreach (var contentElement in contentElements.EnumerateArray())
            {
                if (!contentElement.TryGetProperty("type", out var contentTypeElement))
                    continue;

                string? contentType = contentTypeElement.GetString();

                if (contentType == "input_text")
                {
                    if (contentElement.TryGetProperty("text", out var textElement))
                        contentList.Add(new TextContent(textElement.GetString()));
                    else if (contentElement.TryGetProperty("transcript", out var transcriptElement))
                        contentList.Add(new TextContent(transcriptElement.GetString()));
                }
                else if (contentType == "input_audio" && contentElement.TryGetProperty("audio", out var audioDataElement))
                {
                    contentList.Add(new DataContent($"data:audio/pcm;base64,{audioDataElement.GetString()}"));
                }
                else if (contentType == "input_image" && contentElement.TryGetProperty("image_url", out var imageUrlElement))
                {
                    contentList.Add(new DataContent(imageUrlElement.GetString()!));
                }
            }

            return contentList;
        }

        private static RealtimeContentItem? ParseRealtimeContentItem(JsonElement itemElement)
        {
            if (!itemElement.TryGetProperty("type", out var itemTypeElement))
                return null;

            string? id = itemElement.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            string? itemType = itemTypeElement.GetString();

            if (itemType == "message")
            {
                if (!itemElement.TryGetProperty("content", out var contentElements) ||
                    contentElements.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                ChatRole? chatRole = itemElement.TryGetProperty("role", out var roleElement)
                    ? ParseChatRole(roleElement.GetString())
                    : null;

                return new RealtimeContentItem(ParseContentItems(contentElements), id, chatRole);
            }
            else if (itemType == "function_call" &&
                itemElement.TryGetProperty("name", out var nameElement) &&
                itemElement.TryGetProperty("call_id", out var callerIdElement))
            {
                IDictionary<string, object?>? arguments = null;

                if (itemElement.TryGetProperty("arguments", out var argumentsElement))
                {
                    if (argumentsElement.ValueKind == JsonValueKind.String)
                    {
                        string argumentsJson = argumentsElement.GetString()!;
                        arguments = string.IsNullOrEmpty(argumentsJson) ? null : JsonSerializer.Deserialize<IDictionary<string, object?>>(argumentsJson);
                    }
                    else if (argumentsElement.ValueKind == JsonValueKind.Object)
                    {
                        arguments = new AdditionalPropertiesDictionary();
                        foreach (var argProperty in argumentsElement.EnumerateObject())
                        {
                            arguments[argProperty.Name] = argProperty.Value.GetString();
                        }
                    }
                }

                return new RealtimeContentItem(
                    [new FunctionCallContent(callerIdElement.GetString()!, nameElement.GetString()!, arguments)],
                    id,
                    null);
            }

            return null;
        }

        #endregion

        #region Message Creation Methods

        private static RealtimeServerErrorMessage? CreateErrorMessage(JsonElement root)
        {
            if (!root.TryGetProperty("error", out var errorElement) ||
                !errorElement.TryGetProperty("message", out var messageElement))
            {
                return null;
            }

            var msg = new RealtimeServerErrorMessage
            {
                Error = new ErrorContent(messageElement.GetString())
            };

            if (errorElement.TryGetProperty("code", out var codeElement))
                msg.Error.ErrorCode = codeElement.GetString();

            if (root.TryGetProperty("event_id", out var eventIdElement))
                msg.EventId = eventIdElement.GetString();

            if (errorElement.TryGetProperty("param", out var paramElement))
                msg.Parameter = paramElement.GetString();

            return msg;
        }

        private static RealtimeServerInputAudioTranscriptionMessage? CreateInputAudioTranscriptionMessage(JsonElement root, string messageType)
        {
            RealtimeServerMessageType serverMessageType = messageType switch
            {
                "conversation.item.input_audio_transcription.delta" => RealtimeServerMessageType.InputAudioTranscriptionDelta,
                "conversation.item.input_audio_transcription.completed" => RealtimeServerMessageType.InputAudioTranscriptionCompleted,
                "conversation.item.input_audio_transcription.failed" => RealtimeServerMessageType.InputAudioTranscriptionFailed,
                _ => throw new InvalidOperationException($"Unknown message type: {messageType}")
            };

            var msg = new RealtimeServerInputAudioTranscriptionMessage(serverMessageType);

            if (root.TryGetProperty("event_id", out var eventIdElement))
                msg.EventId = eventIdElement.GetString();

            if (root.TryGetProperty("content_index", out var contentIndexElement))
                msg.ContentIndex = contentIndexElement.GetInt32();

            if (root.TryGetProperty("item_id", out var itemIdElement))
                msg.ItemId = itemIdElement.GetString();

            if (root.TryGetProperty("delta", out var deltaElement))
                msg.Transcription = deltaElement.GetString();

            if (msg.Transcription is null && root.TryGetProperty("transcript", out deltaElement))
                msg.Transcription = deltaElement.GetString();

            if (root.TryGetProperty("error", out var errorElement) &&
                errorElement.TryGetProperty("message", out var errorMsgElement))
            {
                var errorContent = new ErrorContent(errorMsgElement.GetString());

                if (errorElement.TryGetProperty("code", out var errorCodeElement))
                    errorContent.ErrorCode = errorCodeElement.GetString();

                if (errorElement.TryGetProperty("param", out var errorParamElement))
                    errorContent.Details = errorParamElement.GetString();

                msg.Error = errorContent;
            }

            if (root.TryGetProperty("usage", out var usageElement))
                msg.Usage = ParseUsageDetails(usageElement, requireTypeCheck: true);

            return msg;
        }

        private static RealtimeServerOutputTextAudioMessage? CreateOutputTextAudioMessage(JsonElement root, string messageType)
        {
            RealtimeServerMessageType serverMessageType = messageType switch
            {
                "response.output_audio.delta" => RealtimeServerMessageType.OutputAudioDelta,
                "response.output_audio.done" => RealtimeServerMessageType.OutputAudioDone,
                "response.output_audio_transcript.delta" => RealtimeServerMessageType.OutputAudioTranscriptionDelta,
                "response.output_audio_transcript.done" => RealtimeServerMessageType.OutputAudioTranscriptionDone,
                _ => throw new InvalidOperationException($"Unknown message type: {messageType}")
            };

            var msg = new RealtimeServerOutputTextAudioMessage(serverMessageType);

            if (root.TryGetProperty("event_id", out var eventIdElement))
                msg.EventId = eventIdElement.GetString();

            if (root.TryGetProperty("response_id", out var responseIdElement))
                msg.ResponseId = responseIdElement.GetString();

            if (root.TryGetProperty("item_id", out var itemIdElement))
                msg.ItemId = itemIdElement.GetString();

            if (root.TryGetProperty("output_index", out var outputIndexElement))
                msg.OutputIndex = outputIndexElement.GetInt32();

            if (root.TryGetProperty("content_index", out var contentIndexElement))
                msg.ContentIndex = contentIndexElement.GetInt32();

            if (root.TryGetProperty("delta", out var deltaElement))
                msg.Text = deltaElement.GetString();

            if (msg.Text is null && root.TryGetProperty("transcript", out deltaElement))
                msg.Text = deltaElement.GetString();

            return msg;
        }

        private static RealtimeServerResponseOutputItemMessage? CreateResponseOutItemMessage(JsonElement root, string messageType)
        {
            RealtimeServerMessageType serverMessageType = messageType switch
            {
                "response.output_item.added" => RealtimeServerMessageType.ResponseCreated,
                "response.output_item.done" => RealtimeServerMessageType.ResponseDone,
                _ => throw new InvalidOperationException($"Unknown message type: {messageType}")
            };

            var msg = new RealtimeServerResponseOutputItemMessage(serverMessageType);

            if (root.TryGetProperty("event_id", out var eventIdElement))
                msg.EventId = eventIdElement.GetString();

            if (root.TryGetProperty("response_id", out var responseIdElement))
                msg.ResponseId = responseIdElement.GetString();

            if (root.TryGetProperty("output_index", out var outputIndexElement))
                msg.OutputIndex = outputIndexElement.GetInt32();

            if (root.TryGetProperty("item", out var itemElement))
                msg.Item = ParseRealtimeContentItem(itemElement);

            return msg;
        }

        private static RealtimeServerResponseCreatedMessage? CreateResponseCreatedMessage(JsonElement root, string messageType)
        {
            RealtimeServerMessageType serverMessageType = messageType switch
            {
                "response.created" => RealtimeServerMessageType.ResponseCreated,
                "response.done" => RealtimeServerMessageType.ResponseDone,
                _ => throw new InvalidOperationException($"Unknown message type: {messageType}")
            };

            if (!root.TryGetProperty("response", out var responseElement))
                return null;

            var msg = new RealtimeServerResponseCreatedMessage(serverMessageType);

            if (root.TryGetProperty("event_id", out var eventIdElement))
                msg.EventId = eventIdElement.GetString();

            if (responseElement.TryGetProperty("audio", out var responseAudioElement) &&
                responseAudioElement.ValueKind == JsonValueKind.Object &&
                responseAudioElement.TryGetProperty("output", out var outputElement) &&
                outputElement.ValueKind == JsonValueKind.Object)
            {
                if (outputElement.TryGetProperty("format", out var formatElement) &&
                    formatElement.TryGetProperty("type", out var formatTypeElement))
                {
                    string? formatType = formatTypeElement.GetString();
                    msg.OutputAudioOptions = formatType switch
                    {
                        "audio/pcma" or "audio/pcmu" => new RealtimeAudioFormat(formatType, 0),
                        "audio/pcm" => new RealtimeAudioFormat("audio/pcm", 24000),
                        _ => null
                    };
                }

                if (outputElement.TryGetProperty("voice", out var voiceElement))
                    msg.OutputVoice = voiceElement.GetString();
            }

            if (responseElement.TryGetProperty("conversation_id", out var conversationIdElement))
                msg.ConversationId = conversationIdElement.GetString();

            if (responseElement.TryGetProperty("id", out var idElement))
                msg.ResponseId = idElement.GetString();

            if (responseElement.TryGetProperty("max_output_tokens", out var maxOutputTokensElement))
            {
                msg.MaxOutputTokens = maxOutputTokensElement.ValueKind == JsonValueKind.Number
                    ? maxOutputTokensElement.GetInt32()
                    : maxOutputTokensElement.ValueKind == JsonValueKind.String && maxOutputTokensElement.GetString() == "inf"
                        ? int.MaxValue
                        : null;
            }

            if (responseElement.TryGetProperty("metadata", out var metadataElement) &&
                metadataElement.ValueKind == JsonValueKind.Object)
            {
                var metadataDict = new AdditionalPropertiesDictionary();
                foreach (var property in metadataElement.EnumerateObject())
                {
                    metadataDict[property.Name] = property.Value.GetString();
                }
                msg.Metadata = metadataDict;
            }

            if (responseElement.TryGetProperty("output_modalities", out var outputModalitiesElement) &&
                outputModalitiesElement.ValueKind == JsonValueKind.Array)
            {
                var modalities = new List<string>();
                foreach (var modalityItem in outputModalitiesElement.EnumerateArray())
                {
                    if (modalityItem.GetString() is string m && !string.IsNullOrEmpty(m))
                        modalities.Add(m);
                }
                if (modalities.Count > 0)
                    msg.OutputModalities = modalities;
            }

            if (responseElement.TryGetProperty("status", out var statusElement))
                msg.Status = statusElement.GetString();

            if (responseElement.TryGetProperty("status_details", out var statusDetailsElement) &&
                statusDetailsElement.ValueKind == JsonValueKind.Object &&
                statusDetailsElement.TryGetProperty("error", out var errorElement) &&
                errorElement.ValueKind == JsonValueKind.Object &&
                errorElement.TryGetProperty("type", out var errorTypeElement) &&
                errorElement.TryGetProperty("code", out var errorCodeElement))
            {
                msg.Error = new ErrorContent(errorTypeElement.GetString())
                {
                    ErrorCode = errorCodeElement.GetString()
                };
            }

            if (responseElement.TryGetProperty("usage", out var usageElement))
                msg.Usage = ParseUsageDetails(usageElement);

            if (responseElement.TryGetProperty("output", out outputElement) &&
                outputElement.ValueKind == JsonValueKind.Array)
            {
                var outputItems = new List<RealtimeContentItem>();
                foreach (var outputItemElement in outputElement.EnumerateArray())
                {
                    if (ParseRealtimeContentItem(outputItemElement) is RealtimeContentItem item)
                        outputItems.Add(item);
                }
                msg.Items = outputItems;
            }

            return msg;
        }

        #endregion
    }

    public class ServerEventArgs : EventArgs
    {
        public string EventType { get; set; } = string.Empty;
        public JsonDocument? Data { get; set; }
    }
}