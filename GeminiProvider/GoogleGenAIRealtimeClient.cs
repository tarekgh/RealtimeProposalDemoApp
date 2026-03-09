// Gemini Realtime provider adapted from https://github.com/jeffhandley/googleapis-dotnet-genai/pull/1

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.GenAI;
using Google.GenAI.Types;

#pragma warning disable MEAI001 // Experimental AI API

namespace Microsoft.Extensions.AI;

/// <summary>
/// Provides an <see cref="IRealtimeClient"/> implementation for Google GenAI's Live API.
/// </summary>
public sealed class GoogleGenAIRealtimeClient : IRealtimeClient
{
    private readonly Client _client;
    private readonly string? _defaultModelId;
    private ChatClientMetadata? _metadata;

    /// <summary>Initializes a new instance wrapping an existing <see cref="Client"/>.</summary>
    /// <param name="client">The Google GenAI client.</param>
    /// <param name="defaultModelId">The default model to use for realtime sessions.</param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <see langword="null"/>.</exception>
    public GoogleGenAIRealtimeClient(Client client, string? defaultModelId = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _defaultModelId = defaultModelId;
    }

    /// <inheritdoc />
    public async Task<IRealtimeClientSession> CreateSessionAsync(
        RealtimeSessionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string model = options?.Model ?? _defaultModelId
            ?? throw new InvalidOperationException(
                "No model specified. Provide a model via RealtimeSessionOptions.Model or the defaultModelId constructor parameter.");

        var config = BuildLiveConnectConfig(options);

        var asyncSession = await _client.Live.ConnectAsync(model, config, cancellationToken).ConfigureAwait(false);

        return new GoogleGenAIRealtimeSession(asyncSession, model, options);
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
            return _metadata ??= new ChatClientMetadata("google-genai", defaultModelId: _defaultModelId);
        }

        if (serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        if (serviceType.IsInstanceOfType(_client))
        {
            return _client;
        }

        return null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Client lifecycle is not owned by this wrapper.
    }

    /// <summary>Converts MEAI session options to a Google GenAI <see cref="LiveConnectConfig"/>.</summary>
    private static LiveConnectConfig BuildLiveConnectConfig(RealtimeSessionOptions? options)
    {
        var config = new LiveConnectConfig();

        if (options is null)
        {
            config.ResponseModalities = new List<Modality> { Modality.Audio };
            return config;
        }

        // System instructions
        if (!string.IsNullOrEmpty(options.Instructions))
        {
            config.SystemInstruction = new Content
            {
                Parts = new List<Part> { new Part { Text = options.Instructions } },
                Role = "user"
            };
        }

        // Output modalities
        if (options.OutputModalities is { Count: > 0 })
        {
            config.ResponseModalities = new List<Modality>();
            foreach (var modality in options.OutputModalities)
            {
                config.ResponseModalities.Add(modality.ToLowerInvariant() switch
                {
                    "audio" => Modality.Audio,
                    "text" => Modality.Text,
                    _ => Modality.Text,
                });
            }
        }
        else
        {
            config.ResponseModalities = new List<Modality> { Modality.Audio };
        }

        // Voice / speech config
        if (!string.IsNullOrEmpty(options.Voice))
        {
            config.SpeechConfig = new SpeechConfig
            {
                VoiceConfig = new VoiceConfig
                {
                    PrebuiltVoiceConfig = new PrebuiltVoiceConfig
                    {
                        VoiceName = options.Voice,
                    }
                }
            };
        }

        // Generation config
        if (options.MaxOutputTokens.HasValue)
        {
            config.GenerationConfig ??= new GenerationConfig();
            config.GenerationConfig.MaxOutputTokens = options.MaxOutputTokens.Value;
        }

        // Tools (AIFunction → Google FunctionDeclaration)
        if (options.Tools is { Count: > 0 })
        {
            var functionDeclarations = new List<FunctionDeclaration>();
            foreach (var tool in options.Tools)
            {
                if (tool is AIFunction aiFunction)
                {
                    functionDeclarations.Add(GoogleGenAIRealtimeSession.ToGoogleFunctionDeclaration(aiFunction));
                }
            }

            if (functionDeclarations.Count > 0)
            {
                config.Tools = new List<Tool>
                {
                    new Tool { FunctionDeclarations = functionDeclarations }
                };
            }
        }

        // Transcription
        if (options.TranscriptionOptions is not null)
        {
            config.InputAudioTranscription = new AudioTranscriptionConfig();
            config.OutputAudioTranscription = new AudioTranscriptionConfig();
        }

        // Disable automatic VAD — the demo app controls activity boundaries
        // via explicit AudioBufferAppend → AudioBufferCommit → ResponseCreate.
        config.RealtimeInputConfig = new RealtimeInputConfig
        {
            AutomaticActivityDetection = new AutomaticActivityDetection { Disabled = true }
        };

        return config;
    }
}

#pragma warning restore MEAI001
