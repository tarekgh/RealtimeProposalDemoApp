using System;
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
using Microsoft.Extensions.AI;

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

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }

        public Task<IRealtimeSession?> CreateSessionAsync(RealtimeSessionOptions? options = null, CancellationToken cancellationToken = default)
        {
            return CreateSessionAsync();
        }
    }

    // Session implementation
    public class OpenAIRealtimeSession : IRealtimeSession
    {
        private ClientWebSocket? _webSocket;
        private CancellationTokenSource? _cancellationTokenSource;
        private readonly string _apiKey;
        private readonly string _model;
        private bool _isConnected;
        private string _partialMessage = string.Empty;
        private Channel<RealtimeServerMessage>? _eventChannel;

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
            // Convert RealtimeSessionOptions to JsonObject and call UpdateSessionAsync
            var sessionElement = new JsonObject();
            sessionElement["type"] = "session.update";
            var sessionObject = new JsonObject();
            sessionElement["session"] = sessionObject;

            JsonObject audioElement = new JsonObject();
            JsonObject audioInputElement = new JsonObject();
            JsonObject audioOutputElement = new JsonObject();
            JsonObject audioInputFormatElement = new JsonObject();
            JsonObject audioOutputFormatElement = new JsonObject();
            sessionObject["audio"] = audioElement;
            audioElement["input"] = audioInputElement;
            audioElement["output"] = audioOutputElement;

            if (options.InputAudioFormat is not null)
            {
                audioInputElement["format"] = audioInputFormatElement;
                audioInputFormatElement["type"] = options.InputAudioFormat.Type;
                if (options.InputAudioFormat.SampleRate.HasValue)
                {
                    audioInputFormatElement["rate"] = options.InputAudioFormat.SampleRate.Value;
                }
            }

            if (options.NoiseReductionOptions.HasValue)
            {
                var noiseReductionObj = new JsonObject();
                if (options.NoiseReductionOptions.Value == NoiseReductionOptions.NearField)
                {
                    noiseReductionObj["type"] = "near_field";
                    audioInputElement["noise_reduction"] = noiseReductionObj;
                }
                else if (options.NoiseReductionOptions.Value == NoiseReductionOptions.FarField)
                {
                    noiseReductionObj["type"] = "far_field";
                    audioInputElement["noise_reduction"] = noiseReductionObj;
                }
            }

            if (options.TranscriptionOptions is not null)
            {
                var transcriptionOptionsObj = new JsonObject();
                transcriptionOptionsObj["language"] = options.TranscriptionOptions.Language;
                transcriptionOptionsObj["model"] = options.TranscriptionOptions.Model;
                if (options.TranscriptionOptions.Prompt is not null)
                {
                    transcriptionOptionsObj["prompt"] = options.TranscriptionOptions.Prompt;
                }
                // Add transcription options properties here as needed
                audioInputElement["transcription"] = transcriptionOptionsObj;
            }

            if (options.VoiceActivityDetection is ServerVoiceActivityDetection serverVad)
            {
                var turnDetection = new JsonObject();
                turnDetection["type"] = "server_vad";
                turnDetection["create_response"] = serverVad.CreateResponse;
                turnDetection["idle_timeout_ms"] = serverVad.IdleTimeoutInMilliseconds;
                turnDetection["interrupt_response"] = serverVad.InterruptResponse;
                turnDetection["prefix_padding_ms"] = serverVad.PrefixPaddingInMilliseconds;
                turnDetection["silence_duration_ms"] = serverVad.SilenceDurationInMilliseconds;
                turnDetection["threshold"] = serverVad.Threshold;
                audioInputElement["turn_detection"] = turnDetection;
            }
            else if (options.VoiceActivityDetection is SemanticVoiceActivityDetection semanticVad)
            {
                var turnDetection = new JsonObject();
                turnDetection["type"] = "semantic_vad";
                turnDetection["create_response"] = semanticVad.CreateResponse;
                turnDetection["interrupt_response"] = semanticVad.InterruptResponse;
                turnDetection["eagerness"] = semanticVad.Eagerness;
                audioInputElement["turn_detection"] = turnDetection;
            }

            if (options.SessionKind == RealtimeSessionKind.Realtime)
            {
                sessionObject["type"] = "realtime";

                if (options.OutputAudioFormat is not null)
                {
                    audioOutputElement["format"] = audioOutputFormatElement;
                    audioOutputFormatElement["type"] = options.OutputAudioFormat.Type;
                    if (options.OutputAudioFormat.SampleRate.HasValue)
                    {
                        audioOutputFormatElement["rate"] = options.OutputAudioFormat.SampleRate.Value;
                    }
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
                    var modalitiesArray = new JsonArray();
                    foreach (var modality in options.OutputModalities)
                    {
                        modalitiesArray.Add(modality);
                    }
                    sessionObject["output_modalities"] = modalitiesArray;
                }

                //if (options.PromptTemplate is not null && !string.IsNullOrEmpty(options.PromptTemplate.Id))
                //{
                //    var promptObj = new JsonObject
                //    {
                //        ["id"] = options.PromptTemplate.Id
                //    };

                //    if (options.PromptTemplate.Variables is not null && options.PromptTemplate.Variables.Count > 0)
                //    {
                //        var variablesObj = new JsonObject();
                //        foreach (var kvp in options.PromptTemplate.Variables)
                //        {
                //            variablesObj[kvp.Key] = JsonValue.Create(kvp.Value);
                //        }
                //        promptObj["variables"] = variablesObj;
                //    }

                //    if (!string.IsNullOrEmpty(options.PromptTemplate.Version))
                //    {
                //        promptObj["version"] = options.PromptTemplate.Version;
                //    }

                //    sessionObject["prompt"] = promptObj;
                //}
                // to do item.input_audio_transcription.logprobs and tools properties
            }
            else if (options.SessionKind == RealtimeSessionKind.Transcription)
            {
                sessionObject["type"] = "transcription";
            }

            await SendEventAsync(sessionElement);
        }

        // End the current session
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

        // Send audio input (base64 encoded PCM16 audio)
        public async Task SendAudioAsync(string base64Audio)
        {
            var audioEvent = new JsonObject
            {
                ["type"] = "input_audio_buffer.append",
                ["audio"] = base64Audio
            };

            await SendEventAsync(audioEvent);
        }

        // Commit audio buffer
        public async Task CommitAudioAsync()
        {
            var commitEvent = new JsonObject
            {
                ["type"] = "input_audio_buffer.commit"
            };

            await SendEventAsync(commitEvent);
        }

        // Create response (triggers model to generate response)
        public async Task CreateResponseAsync()
        {
            var responseEvent = new JsonObject
            {
                ["type"] = "response.create",
                ["response"] = new JsonObject
                {
                    // ["output_modalities"] = new JsonArray("text", "audio"),
                    // ["instructions"] = "Respond to the input audio naturally in English."
                    ["instructions"] = "Respond to the the audio input.",
                }
            };

            await SendEventAsync(responseEvent);
        }

        // Clear audio buffer
        public async Task ClearAudioBufferAsync()
        {
            var clearEvent = new JsonObject
            {
                ["type"] = "input_audio_buffer.clear"
            };

            await SendEventAsync(clearEvent);
        }

        // Get streaming response as IAsyncEnumerable
        public async IAsyncEnumerable<RealtimeServerMessage> GetStreamingResponseAsync(
                                    IAsyncEnumerable<RealtimeClientMessage> updates,
                                    [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (_eventChannel == null)
                yield break;

            // Start a task to process incoming client messages
            var processUpdatesTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var message in updates.WithCancellation(cancellationToken))
                    {
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

                                        case "audio/pcmu": // The G.711 μ-law format
                                            formatObj["type"] = responseCreate.OutputAudioOptions.Type;
                                            break;

                                        case "audio/pcma": // The G.711 A-law format.
                                            formatObj["type"] = responseCreate.OutputAudioOptions.Type;
                                            break;
                                    }

                                    outputObj["format"] = formatObj;

                                    if (!string.IsNullOrEmpty(responseCreate.OutputVoice))
                                    {
                                        outputObj["voice"] = responseCreate.OutputVoice;
                                    }

                                    // Handle OutputVoice (audio.output.voice)

                                    audioObj["output"] = outputObj;
                                    responseObj["audio"] = audioObj;
                                }
                                else if (!string.IsNullOrEmpty(responseCreate.OutputVoice))
                                {
                                    // OutputVoice without OutputAudioOptions
                                    responseObj["audio"] = new JsonObject
                                    {
                                        ["output"] = new JsonObject
                                        {
                                            ["voice"] = responseCreate.OutputVoice
                                        }
                                    };
                                }

                                // Handle conversation
                                if (responseCreate.ExcludeFromConversation)
                                {
                                    responseObj["conversation"] = "none";
                                }
                                else
                                {
                                    responseObj["conversation"] = "auto";
                                }

                                // Handle Items (input)
                                if (responseCreate.Items != null && responseCreate.Items.Any())
                                {
                                    var inputArray = new JsonArray();
                                    foreach (var item in responseCreate.Items)
                                    {
                                        if (item is RealtimeContentItem contentItem && contentItem.Content is not null)
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

                                            if (contentItem.Content is TextContent textContent)
                                            {
                                                itemObj["content"] = new JsonArray
                                                {
                                                    new JsonObject
                                                    {
                                                        ["type"] = "input_text",
                                                        ["text"] = textContent.Text
                                                    }
                                                };

                                                inputArray.Add(itemObj);
                                                continue;
                                            }
                                            else if (contentItem.Content is DataContent audioContent && audioContent.MediaType.StartsWith("audio/"))
                                            {
                                                itemObj["content"] = new JsonArray
                                                {
                                                    new JsonObject
                                                    {
                                                        ["type"] = "input_audio",
                                                        ["audio"] = audioContent.Base64Data.ToString()
                                                    }
                                                };

                                                inputArray.Add(itemObj);
                                                continue;
                                            }
                                            else if (contentItem.Content is DataContent imageContent && imageContent.MediaType.StartsWith("image/"))
                                            {
                                                itemObj["content"] = new JsonArray
                                                {
                                                    new JsonObject
                                                    {
                                                        ["type"] = "input_image",
                                                        ["image"] = imageContent.Base64Data.ToString()
                                                    }
                                                };

                                                inputArray.Add(itemObj);
                                                continue;
                                            }
                                        }
                                    }

                                    // To do: Support other content types as needed

                                    responseObj["input"] = inputArray;
                                }

                                // Handle Instructions
                                if (!string.IsNullOrEmpty(responseCreate.Instructions))
                                {
                                    responseObj["instructions"] = responseCreate.Instructions;
                                }

                                // Handle MaxOutputTokens
                                if (responseCreate.MaxOutputTokens.HasValue)
                                {
                                    responseObj["max_output_tokens"] = responseCreate.MaxOutputTokens.Value;
                                }

                                // Handle Metadata
                                if (responseCreate.Metadata is not null && responseCreate.Metadata.Count > 0)
                                {
                                    var metadataObj = new JsonObject();
                                    foreach (var kvp in responseCreate.Metadata)
                                    {
                                        metadataObj[kvp.Key] = JsonValue.Create(kvp.Value);
                                    }
                                    responseObj["metadata"] = metadataObj;
                                }

                                // Handle OutputModalities
                                if (responseCreate.OutputModalities is not null && responseCreate.OutputModalities.Any())
                                {
                                    var modalitiesArray = new JsonArray();
                                    foreach (var modality in responseCreate.OutputModalities)
                                    {
                                        modalitiesArray.Add(modality);
                                    }
                                    responseObj["output_modalities"] = modalitiesArray;
                                }

                                // Handle PromptTemplate
                                //if (responseCreate.PromptTemplate is not null && !string.IsNullOrEmpty(responseCreate.PromptTemplate.Id))
                                //{
                                //    var promptObj = new JsonObject
                                //    {
                                //        ["id"] = responseCreate.PromptTemplate.Id
                                //    };

                                //    if (responseCreate.PromptTemplate.Variables is not null && responseCreate.PromptTemplate.Variables.Count > 0)
                                //    {
                                //        var variablesObj = new JsonObject();
                                //        foreach (var kvp in responseCreate.PromptTemplate.Variables)
                                //        {
                                //            variablesObj[kvp.Key] = JsonValue.Create(kvp.Value);
                                //        }
                                //        promptObj["variables"] = variablesObj;
                                //    }

                                //    if (!string.IsNullOrEmpty(responseCreate.PromptTemplate.Version))
                                //    {
                                //        promptObj["version"] = responseCreate.PromptTemplate.Version;
                                //    }

                                //    responseObj["prompt"] = promptObj;
                                //}

                                // Handle Function Tool Name
                                if (!string.IsNullOrEmpty(responseCreate.FunctionToolName))
                                {
                                    responseObj["tool_choice"] = new JsonObject
                                    {
                                        ["type"] = "function",
                                        ["name"] = responseCreate.FunctionToolName
                                    };
                                }
                                else if (!string.IsNullOrEmpty(responseCreate.McpToolName) && !string.IsNullOrEmpty(responseCreate.McpToolServerLabel))
                                {
                                    // Handle MCP Tool

                                    var mcpToolChoice = new JsonObject
                                    {
                                        ["type"] = "mcp"
                                    };

                                    mcpToolChoice["server_label"] = responseCreate.McpToolServerLabel;
                                    mcpToolChoice["name"] = responseCreate.McpToolName;
                                    responseObj["tool_choice"] = mcpToolChoice;
                                }
                                else if (responseCreate.ToolChoiceMode.HasValue)
                                {
                                    switch (responseCreate.ToolChoiceMode.Value)
                                    {
                                        case ToolChoiceMode.None:
                                            responseObj["tool_choice"] = "none";
                                            break;
                                        case ToolChoiceMode.Auto:
                                            responseObj["tool_choice"] = "auto";
                                            break;
                                        case ToolChoiceMode.Required:
                                            responseObj["tool_choice"] = "required";
                                            break;
                                    }
                                }

                                // Handle Tools
                                if (responseCreate.Tools is not null && responseCreate.Tools.Any())
                                {
                                    var toolsArray = new JsonArray();

                                    foreach (var tool in responseCreate.Tools)
                                    {
                                        var toolObj = new JsonObject();

                                        // Check if it's an AIFunction
                                        if (tool is AIFunction aiFunction)
                                        {
                                            toolObj["type"] = "function";

                                            // Get function name
                                            if (!string.IsNullOrEmpty(aiFunction.Name))
                                            {
                                                toolObj["name"] = aiFunction.Name;
                                            }

                                            // Get description
                                            if (!string.IsNullOrEmpty(aiFunction.Description))
                                            {
                                                toolObj["description"] = aiFunction.Description;
                                            }

                                            // Get parameters schema - AIFunction doesn't expose this directly
                                            // We need to serialize the AIFunction and extract the schema
                                            var functionJson = JsonSerializer.SerializeToNode(aiFunction);
                                            if (functionJson?["parameters"] is not null)
                                            {
                                                toolObj["parameters"] = functionJson["parameters"];
                                            }
                                        }
                                        // Check if it's an MCP tool - serialize the tool and extract MCP properties
                                        else
                                        {
                                            // Serialize the entire tool to get all properties
                                            var toolJson = JsonSerializer.SerializeToNode(tool);

                                            // Check if the serialized JSON has MCP properties
                                            if (toolJson is not null)
                                            {
                                                // Check if it has MCP-specific properties
                                                if (toolJson["server_label"] is not null || toolJson["server_url"] is not null || toolJson["connector_id"] is not null)
                                                {
                                                    toolObj["type"] = "mcp";

                                                    // Copy all MCP-related properties from the serialized JSON
                                                    if (toolJson["server_label"] is not null)
                                                        toolObj["server_label"] = toolJson["server_label"];

                                                    if (toolJson["server_url"] is not null)
                                                        toolObj["server_url"] = toolJson["server_url"];

                                                    if (toolJson["connector_id"] is not null)
                                                        toolObj["connector_id"] = toolJson["connector_id"];

                                                    if (toolJson["authorization"] is not null)
                                                        toolObj["authorization"] = toolJson["authorization"];

                                                    if (toolJson["headers"] is not null)
                                                        toolObj["headers"] = toolJson["headers"];

                                                    if (toolJson["require_approval"] is not null)
                                                        toolObj["require_approval"] = toolJson["require_approval"];

                                                    if (toolJson["server_description"] is not null)
                                                        toolObj["server_description"] = toolJson["server_description"];

                                                    if (toolJson["allowed_tools"] is not null)
                                                        toolObj["allowed_tools"] = toolJson["allowed_tools"];
                                                }
                                            }
                                        }

                                        toolsArray.Add(toolObj);
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

                                    if (itemCreate.Item is RealtimeContentItem contentItem && contentItem.Content is not null)
                                    {
                                        var itemObj = new JsonObject
                                        {
                                            ["type"] = "message",
                                            // ["object"] = "realtime.item"
                                        };

                                        if (contentItem.Role.HasValue)
                                        {
                                            itemObj["role"] = contentItem.Role.Value.Value;
                                        }

                                        if (contentItem.Id is not null)
                                        {
                                            itemObj["id"] = contentItem.Id;
                                        }

                                        if (contentItem.Content is TextContent textContent)
                                        {
                                            itemObj["content"] = new JsonArray
                                            {
                                                new JsonObject
                                                {
                                                    ["type"] = "input_text",
                                                    ["text"] = textContent.Text
                                                }
                                            };
                                        }
                                        else if (contentItem.Content is DataContent audioContent && audioContent.MediaType.StartsWith("audio/"))
                                        {
                                            itemObj["content"] = new JsonArray
                                            {
                                                new JsonObject
                                                {
                                                    ["type"] = "input_audio",
                                                    ["audio"] = audioContent.Base64Data.ToString()
                                                }
                                            };
                                        }
                                        else if (contentItem.Content is DataContent imageContent && imageContent.MediaType.StartsWith("image/"))
                                        {
                                            itemObj["content"] = new JsonArray
                                            {
                                                new JsonObject
                                                {
                                                    ["type"] = "input_image",
                                                    ["image_url"] = imageContent.Uri
                                                }
                                            };
                                        }

                                        jsonMessage["item"] = itemObj;
                                    }

                                    // To do: Support other content types as needed
                                }
                                break;

                            case RealtimeClientInputAudioBufferAppendMessage audioAppend:
                                if (audioAppend.Content is not null && audioAppend.Content.MediaType.StartsWith("audio/"))
                                {
                                    jsonMessage["type"] = "input_audio_buffer.append";

                                    // DataContent is created with "data:audio/pcm;base64,<data>" format
                                    // Extract the Uri property and get the base64 part after the comma
                                    string dataUri = audioAppend.Content.Uri?.ToString() ?? string.Empty;
                                    string base64Data;

                                    int commaIndex = dataUri.LastIndexOf(',');
                                    if (commaIndex >= 0 && commaIndex < dataUri.Length - 1)
                                    {
                                        // Extract everything after the last comma
                                        base64Data = dataUri.Substring(commaIndex + 1);
                                    }
                                    else
                                    {
                                        // Fallback: try to get raw data directly
                                        base64Data = Convert.ToBase64String(audioAppend.Content.Data.ToArray());
                                    }

                                    jsonMessage["audio"] = base64Data;
                                }
                                break;

                            case RealtimeClientInputAudioBufferCommitMessage audioCommit:
                                jsonMessage["type"] = "input_audio_buffer.commit";
                                break;

                            default:
                                if (message.Type is RealtimeClientMessageType.RawContentOnly && message.RawRepresentation is not null)
                                {
                                    if (message.RawRepresentation is string rawString)
                                    {
                                        // For raw string content, parse it directly
                                        jsonMessage = JsonSerializer.Deserialize<JsonObject>(rawString);
                                    }
                                    else if (message.RawRepresentation is JsonObject rawJsonObject)
                                    {
                                        // For raw JsonObject content, use it directly
                                        jsonMessage = rawJsonObject;
                                    }
                                }
                                break;
                        }

                        if (jsonMessage is not null && jsonMessage.TryGetPropertyValue("type", out var _))
                        {
                            await SendEventAsync(jsonMessage);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancelled
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke(this, $"Error processing updates: {ex.Message}");
                }
            }, cancellationToken);

            // Stream server events
            await foreach (var serverEvent in _eventChannel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return serverEvent;
            }

            // Wait for update processing to complete
            await processUpdatesTask;
        }

        // Send generic event
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

        // Receive messages from server
        private async Task ReceiveMessagesAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[1024 * 16]; // 16KB buffer

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
            catch (OperationCanceledException)
            {
                // Expected when cancelling
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Error receiving messages: {ex}");
            }
        }

        // Process server events
        private void ProcessServerEvent(string message)
        {
            try
            {
                if (_partialMessage.Length > 0)
                {
                    message = _partialMessage + message;
                    _partialMessage = string.Empty;
                }

                if (message.EndsWith('}'))
                {
                    var jsonDoc = JsonDocument.Parse(message);
                    var eventType = jsonDoc.RootElement.GetProperty("type").GetString();

                    // Keep ServerEventArgs for backward compatibility with existing event handlers
                    var eventArgs = new ServerEventArgs
                    {
                        EventType = eventType ?? "unknown",
                        Data = jsonDoc
                    };

                    ServerEventReceived?.Invoke(this, eventArgs);

                    // Convert to RealtimeServerMessage for the channel based on event type
                    RealtimeServerMessage? serverMessage = null;

                    try
                    {
                        var root = jsonDoc.RootElement;

                        // Map event type to specific RealtimeServerMessage type
                        serverMessage = eventType switch
                        {
                            "error" => CreateErrorMessage(message),

                            "conversation.item.input_audio_transcription.delta" or
                            "conversation.item.input_audio_transcription.completed" or
                            "conversation.item.input_audio_transcription.failed" =>
                                CreateInputAudioTranscriptionMessage(message),

                            "response.output_audio_transcript.delta" or
                            "response.output_audio_transcript.done" or
                            "response.output_audio.delta" or
                            "response.output_audio.done" =>
                                CreateOutputTextAudioMessage(message),

                            "response.created" or
                            "response.done" =>
                                CreateResponseCreatedMessage(message),

                            // For other event types, skip
                            _ => null
                        };

                        if (serverMessage is not null)
                        {
                            _eventChannel?.Writer.TryWrite(serverMessage);
                        }
                    }
                    catch
                    {
                        // If deserialization fails, skip this message as we can't create a valid RealtimeServerMessage
                        // The ServerEventReceived event has already been raised for backward compatibility
                    }
                }
                else
                {
                    _partialMessage = message;
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

        // Helper methods to create RealtimeServerMessage objects
        private RealtimeServerErrorMessage? CreateErrorMessage(string jsonMessage)
        {
            try
            {
                var jsonDoc = JsonDocument.Parse(jsonMessage);
                var root = jsonDoc.RootElement;

                // Parse the error object
                if (!root.TryGetProperty("error", out var errorElement) || !errorElement.TryGetProperty("message", out var messageElement))
                {
                    return null;
                }

                RealtimeServerErrorMessage realtimeServerErrorMessage = new RealtimeServerErrorMessage();
                realtimeServerErrorMessage.Error = new ErrorContent(messageElement.GetString());

                if (errorElement.TryGetProperty("code", out var codeElement))
                {
                    realtimeServerErrorMessage.Error.ErrorCode = codeElement.GetString();
                }

                if (errorElement.TryGetProperty("event_id", out var errorEventIdElement))
                {
                    realtimeServerErrorMessage.EventId = errorEventIdElement.GetString();
                }

                if (errorElement.TryGetProperty("param", out var paramElement))
                {
                    realtimeServerErrorMessage.Parameter = paramElement.GetString();
                }

                return realtimeServerErrorMessage;
            }
            catch
            {
                return null;
            }
        }

        private RealtimeServerInputAudioTranscriptionMessage? CreateInputAudioTranscriptionMessage(string jsonMessage)
        {
            try
            {
                var jsonDoc = JsonDocument.Parse(jsonMessage);
                var root = jsonDoc.RootElement;

                // Get type from root
                string? messageType = null;
                if (root.TryGetProperty("type", out var typeElement))
                {
                    messageType = typeElement.GetString();
                }

                if (messageType is null)
                {
                    return null;
                }

                RealtimeServerMessageType serverMessageType;

                switch (messageType)
                {
                    case "conversation.item.input_audio_transcription.delta":
                        serverMessageType = RealtimeServerMessageType.InputAudioTranscriptionDelta;
                        break;
                    case "conversation.item.input_audio_transcription.completed":
                        serverMessageType = RealtimeServerMessageType.InputAudioTranscriptionCompleted;
                        break;
                    case "conversation.item.input_audio_transcription.failed":
                        serverMessageType = RealtimeServerMessageType.InputAudioTranscriptionFailed;
                        break;
                    default:
                        return null;
                }

                RealtimeServerInputAudioTranscriptionMessage realtimeServerInputAudioTranscriptionMessage = new RealtimeServerInputAudioTranscriptionMessage(serverMessageType);

                // Get event_id from root
                if (root.TryGetProperty("event_id", out var eventIdElement))
                {
                    realtimeServerInputAudioTranscriptionMessage.EventId = eventIdElement.GetString();
                }

                if (root.TryGetProperty("content_index", out var contentIndexElement))
                {
                    realtimeServerInputAudioTranscriptionMessage.ContentIndex = contentIndexElement.GetInt32();
                }

                if (root.TryGetProperty("item_id", out var itemIdElement))
                {
                    realtimeServerInputAudioTranscriptionMessage.ItemId = itemIdElement.GetString();
                }

                // For delta messages
                if (root.TryGetProperty("delta", out var deltaElement))
                {
                    realtimeServerInputAudioTranscriptionMessage.Transcription = deltaElement.GetString();
                }

                // For completed messages
                if (realtimeServerInputAudioTranscriptionMessage.Transcription is null && root.TryGetProperty("transcript", out deltaElement))
                {
                    realtimeServerInputAudioTranscriptionMessage.Transcription = deltaElement.GetString();
                }

                // For failed messages
                if (root.TryGetProperty("error", out var errorElement) && errorElement.TryGetProperty("message", out var errorMsgElement))
                {
                    var errorContent = new ErrorContent(errorMsgElement.GetString());

                    if (errorElement.TryGetProperty("code", out var errorCodeElement))
                    {
                        errorContent.ErrorCode = errorCodeElement.GetString();
                    }

                    if (errorElement.TryGetProperty("param", out var errorParamElement))
                    {
                        errorContent.Details = errorParamElement.GetString();
                    }

                    realtimeServerInputAudioTranscriptionMessage.Error = errorContent;
                }

                //if (root.TryGetProperty("logprobs", out var logProbElement) && logProbElement.ValueKind == JsonValueKind.Array)
                //{
                //    List<LogProbability> logProbabilities = new List<LogProbability>();

                //    foreach (var logProbItem in logProbElement.EnumerateArray())
                //    {
                //        LogProbability logProbability = new LogProbability();

                //        if (logProbItem.TryGetProperty("logprob", out var logProbValue))
                //        {
                //            logProbability.Value = logProbValue.GetDouble();
                //        }

                //        if (logProbItem.TryGetProperty("token", out var tokenValue))
                //        {
                //            logProbability.Token = tokenValue.GetString();
                //        }

                //        if (logProbItem.TryGetProperty("bytes", out var bytesValue) && bytesValue.ValueKind == JsonValueKind.Array)
                //        {
                //            List<byte> bytesList = new List<byte>();
                //            foreach (var byteItem in bytesValue.EnumerateArray())
                //            {
                //                if (byteItem.TryGetByte(out var byteVal))
                //                {
                //                    bytesList.Add(byteVal);
                //                }
                //            }
                //            logProbability.Bytes = bytesList;
                //        }
                //    }

                //    realtimeServerInputAudioTranscriptionMessage.LogProbabilities = logProbabilities;
                //}

                if (root.TryGetProperty("usage", out var usageElement) &&
                    usageElement.TryGetProperty("type", out var usageTypeElement) &&
                    usageTypeElement.GetString() == "tokens")
                {
                    UsageDetails usageData = new UsageDetails();

                    if (usageElement.TryGetProperty("input_tokens", out var inputTokensElement))
                    {
                        usageData.InputTokenCount = inputTokensElement.GetInt32();
                    }

                    if (usageElement.TryGetProperty("output_tokens", out var outputTokensElement))
                    {
                        usageData.OutputTokenCount = outputTokensElement.GetInt32();
                    }

                    if (usageElement.TryGetProperty("total_tokens", out var totalTokensElement))
                    {
                        usageData.TotalTokenCount = totalTokensElement.GetInt32();
                    }

                    if (usageElement.TryGetProperty("input_token_details", out var inputTokenDetailsElement) &&
                        inputTokenDetailsElement.ValueKind == JsonValueKind.Object)
                    {
                        if (inputTokenDetailsElement.TryGetProperty("audio_tokens", out var audioTokensElement))
                        {
                            usageData.InputAudioTokenCount = audioTokensElement.GetInt32();
                        }

                        if (inputTokenDetailsElement.TryGetProperty("text_tokens", out var textTokensElement))
                        {
                            usageData.InputTextTokenCount = textTokensElement.GetInt32();
                        }
                    }

                    realtimeServerInputAudioTranscriptionMessage.Usage = usageData;
                }

                // Deserialize the reconstructed JSON
                return realtimeServerInputAudioTranscriptionMessage;
            }
            catch
            {
                return null;
            }
        }

        private RealtimeServerOutputTextAudioMessage? CreateOutputTextAudioMessage(string jsonMessage)
        {
            try
            {
                var jsonDoc = JsonDocument.Parse(jsonMessage);
                var root = jsonDoc.RootElement;

                // Get type from root
                string? messageType = null;
                if (root.TryGetProperty("type", out var typeElement))
                {
                    messageType = typeElement.GetString();
                }

                if (messageType is null)
                {
                    return null;
                }

                RealtimeServerMessageType serverMessageType;

                switch (messageType)
                {
                    case "response.output_audio.delta":
                        serverMessageType = RealtimeServerMessageType.OutputAudioDelta;
                        break;
                    case "response.output_audio.done":
                        serverMessageType = RealtimeServerMessageType.OutputAudioDone;
                        break;
                    case "response.output_audio_transcript.delta":
                        serverMessageType = RealtimeServerMessageType.OutputAudioTranscriptionDelta;
                        break;
                    case "response.output_audio_transcript.done":
                        serverMessageType = RealtimeServerMessageType.OutputAudioTranscriptionDone;
                        break;
                    default:
                        return null;
                }

                RealtimeServerOutputTextAudioMessage realtimeServerOutputTextAudioMessage = new RealtimeServerOutputTextAudioMessage(serverMessageType);

                // Get event_id from root
                if (root.TryGetProperty("event_id", out var eventIdElement))
                {
                    realtimeServerOutputTextAudioMessage.EventId = eventIdElement.GetString();
                }

                // Extract properties based on message type
                if (root.TryGetProperty("response_id", out var responseIdElement))
                {
                    realtimeServerOutputTextAudioMessage.ResponseId = responseIdElement.GetString();
                }

                if (root.TryGetProperty("item_id", out var itemIdElement))
                {
                    realtimeServerOutputTextAudioMessage.ItemId = itemIdElement.GetString();
                }

                if (root.TryGetProperty("output_index", out var outputIndexElement))
                {
                    realtimeServerOutputTextAudioMessage.OutputIndex = outputIndexElement.GetInt32();
                }

                if (root.TryGetProperty("content_index", out var contentIndexElement))
                {
                    realtimeServerOutputTextAudioMessage.ContentIndex = contentIndexElement.GetInt32();
                }

                if (root.TryGetProperty("delta", out var deltaElement))
                {
                    realtimeServerOutputTextAudioMessage.Text = deltaElement.GetString();
                }

                if (realtimeServerOutputTextAudioMessage.Text is null && root.TryGetProperty("transcript", out deltaElement))
                {
                    realtimeServerOutputTextAudioMessage.Text = deltaElement.GetString();
                }

                // Deserialize the reconstructed JSON
                return realtimeServerOutputTextAudioMessage;
            }
            catch
            {
                return null;
            }
        }

        private RealtimeServerResponseCreatedMessage? CreateResponseCreatedMessage(string jsonMessage)
        {
            try
            {
                var jsonDoc = JsonDocument.Parse(jsonMessage);
                var root = jsonDoc.RootElement;

                // Get type from root
                string? messageType = null;
                if (root.TryGetProperty("type", out var typeElement))
                {
                    messageType = typeElement.GetString();
                }

                if (messageType is null)
                {
                    return null;
                }

                RealtimeServerMessageType serverMessageType;
                JsonElement responseElement = default;
                if (messageType == "response.created")
                {
                    serverMessageType = RealtimeServerMessageType.ResponseCreated;
                }
                else if (messageType == "response.done" && root.TryGetProperty("response", out responseElement))
                {
                    serverMessageType = RealtimeServerMessageType.ResponseDone;
                }
                else
                {
                    return null;
                }

                RealtimeServerResponseCreatedMessage realtimeServerResponseCreatedMessage = new RealtimeServerResponseCreatedMessage(serverMessageType);

                // Get event_id from root
                if (root.TryGetProperty("event_id", out var eventIdElement))
                {
                    realtimeServerResponseCreatedMessage.EventId = eventIdElement.GetString();
                }

                // Parse response object
                if (responseElement.TryGetProperty("audio", out var responseAudioElement) &&
                    responseAudioElement.TryGetProperty("output", out var outputElement))
                {
                    if (outputElement.TryGetProperty("format", out var formatElement) &&
                        formatElement.TryGetProperty("type", out var formatTypeElement))
                    {
                        string? formatType = formatTypeElement.GetString();
                        if (formatType == "audio/pcma" || formatType == "audio/pcmu")
                        {
                            realtimeServerResponseCreatedMessage.OutputAudioOptions = new RealtimeAudioFormat(formatType, 0);
                        }
                        else if (formatType == "audio/pcm")
                        {
                            realtimeServerResponseCreatedMessage.OutputAudioOptions = new RealtimeAudioFormat("audio/pcm", 24000);
                        }
                    }

                    if (outputElement.TryGetProperty("voice", out var voiceElement))
                    {
                        realtimeServerResponseCreatedMessage.OutputVoice = voiceElement.GetString();
                    }
                }

                if (responseElement.TryGetProperty("conversation_id", out var conversationIdElement))
                {
                    realtimeServerResponseCreatedMessage.ConversationId = conversationIdElement.GetString();
                }

                if (responseElement.TryGetProperty("id", out var idElement))
                {
                    realtimeServerResponseCreatedMessage.ResponseId = idElement.GetString();
                }

                if (responseElement.TryGetProperty("max_output_tokens", out var maxOutputTokensElement))
                {
                    realtimeServerResponseCreatedMessage.MaxOutputTokens = maxOutputTokensElement.GetInt32();
                }

                if (responseElement.TryGetProperty("metadata", out var metadataElement) && metadataElement.ValueKind == JsonValueKind.Object)
                {
                    var metadataDict = new AdditionalPropertiesDictionary();

                    foreach (var property in metadataElement.EnumerateObject())
                    {
                        metadataDict[property.Name] = property.Value.GetString();
                    }

                    realtimeServerResponseCreatedMessage.Metadata = metadataDict;
                }

                if (responseElement.TryGetProperty("output_modalities", out var outputModalitiesElement) && outputModalitiesElement.ValueKind == JsonValueKind.Array)
                {
                    List<string> modalities = new List<string>();
                    foreach (var modalityItem in outputModalitiesElement.EnumerateArray())
                    {
                        string? modalitiesItem = modalityItem.GetString();
                        if (!string.IsNullOrEmpty(modalitiesItem))
                        {
                            modalities.Add(modalitiesItem);
                        }
                    }

                    if (modalities.Count > 0)
                    {
                        realtimeServerResponseCreatedMessage.OutputModalities = modalities;
                    }
                }

                if (responseElement.TryGetProperty("status", out var statusElement))
                {
                    realtimeServerResponseCreatedMessage.Status = statusElement.GetString();
                }

                if (responseElement.TryGetProperty("status_details", out var statusDetailsElement) &&
                    statusDetailsElement.TryGetProperty("error", out var errorElement) &&
                    errorElement.TryGetProperty("type", out var errorTypeElement) &&
                    errorElement.TryGetProperty("code", out var errorCodeElement))
                {
                    realtimeServerResponseCreatedMessage.Error = new ErrorContent(errorTypeElement.GetString())
                    {
                        ErrorCode = errorCodeElement.GetString()
                    };
                }

                // Get usage information
                if (responseElement.TryGetProperty("usage", out var usageElement))
                {
                    var usageData = new UsageDetails();

                    if (usageElement.TryGetProperty("input_tokens", out var inputTokensElement))
                    {
                        usageData.InputTokenCount = inputTokensElement.GetInt32();
                    }

                    if (usageElement.TryGetProperty("output_tokens", out var outputTokensElement))
                    {
                        usageData.OutputTokenCount = outputTokensElement.GetInt32();
                    }

                    if (usageElement.TryGetProperty("total_tokens", out var totalTokensElement))
                    {
                        usageData.TotalTokenCount = totalTokensElement.GetInt32();
                    }

                    if (usageElement.TryGetProperty("input_token_details", out var inputTokenDetailsElement))
                    {
                        if (inputTokenDetailsElement.TryGetProperty("audio_tokens", out var audioTokensElement))
                        {
                            usageData.InputAudioTokenCount = audioTokensElement.GetInt32();
                        }

                        if (inputTokenDetailsElement.TryGetProperty("text_tokens", out var textTokensElement))
                        {
                            usageData.InputTextTokenCount = textTokensElement.GetInt32();
                        }
                    }

                    realtimeServerResponseCreatedMessage.Usage = usageData;
                }

                if (responseElement.TryGetProperty("output", out outputElement) && outputElement.ValueKind == JsonValueKind.Array)
                {
                    List<RealtimeContentItem> outputItems = new List<RealtimeContentItem>();

                    foreach (var outputItemElement in outputElement.EnumerateArray())
                    {
                        if (!outputItemElement.TryGetProperty("type", out var outputTypeElement) || outputTypeElement.GetString() != "message")
                        {
                            // To do: Support other output item types as needed
                            continue;
                        }

                        ChatRole? chatRole = null;
                        if (outputItemElement.TryGetProperty("role", out var outputRoleElement))
                        {
                            string? roleString = outputRoleElement.GetString();
                            if (roleString == "assistant")
                            {
                                chatRole = ChatRole.Assistant;
                            }
                            else if (roleString == "user")
                            {
                                chatRole = ChatRole.User;
                            }
                            else if (roleString == "system")
                            {
                                chatRole = ChatRole.System;
                            }
                        }

                        string? id = null;
                        if (outputItemElement.TryGetProperty("id", out var outputIdElement))
                        {
                            id = outputIdElement.GetString();
                        }

                        if (!outputItemElement.TryGetProperty("content", out var contentElements) || contentElements.ValueKind != JsonValueKind.Array )
                        {
                            continue;
                        }

                        foreach (var contentElement in contentElements.EnumerateArray())
                        {
                            if (!contentElement.TryGetProperty("type", out var contentTypeElement))
                            {
                                // To do: Support other content types as needed
                                continue;
                            }

                            string? contentType = contentTypeElement.GetString();
                            if (contentType == "input_text" && contentTypeElement.TryGetProperty("text", out var textElement))
                            {
                                outputItems.Add(new RealtimeContentItem(new TextContent(textElement.GetString()), id, chatRole));
                            }
                            else if (contentType == "input_text" && contentTypeElement.TryGetProperty("transcript", out var transcriptElement))
                            {
                                outputItems.Add(new RealtimeContentItem(new TextContent(transcriptElement.GetString()), id, chatRole));
                            }
                            else if (contentType == "input_audio" &&  contentTypeElement.TryGetProperty("audio", out var audioDataElement) )
                            {
                                outputItems.Add(new RealtimeContentItem(new DataContent($"data:audio/pcm;base64,{audioDataElement.GetString()}"), id, chatRole));
                            }
                            else if (contentType == "input_image" &&  contentTypeElement.TryGetProperty("image_url", out var imageUrlElement) )
                            {
                                outputItems.Add(new RealtimeContentItem(new DataContent(imageUrlElement.GetString()!), id, chatRole));
                            }
                        }
                    }

                    realtimeServerResponseCreatedMessage.Items = outputItems;
                }

                return realtimeServerResponseCreatedMessage;
            }
            catch
            {
                return null;
            }
        }
    }

    // Event args for server events
    public class ServerEventArgs : EventArgs
    {
        public string EventType { get; set; } = string.Empty;
        public JsonDocument? Data { get; set; }
    }
}
