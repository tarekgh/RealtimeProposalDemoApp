using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RealtimePlayGround
{
    public partial class MainForm : Form
    {
        private WaveInEvent? _waveIn;
        private WaveOutEvent? _waveOut;
        private AudioFileReader? _audioFileReader;
        private bool _isRecording = false;
        private string _audioFilePath = Path.Combine(Path.GetTempPath(), "recorded_audio.wav");
        private WaveFileWriter? _waveFileWriter;
        private WaveFormat? _recordingFormat;
        private System.Drawing.Image? _microphoneIcon;
        private System.Drawing.Image? _muteIcon;
        private System.Drawing.Image? _startPlayIcon;
        private System.Drawing.Image? _stopPlayIcon;
        private System.Drawing.Image? _startCallIcon;
        private System.Drawing.Image? _hangUpIcon;
        private IRealtimeClient? _realtimeClient;
        private IRealtimeSession? _realtimeSession;
        private bool _isCallActive = false;
        private BufferedWaveProvider? _audioProvider;
        private Channel<RealtimeClientMessage>? _clientMessageChannel;
        private CancellationTokenSource? _streamingCancellationTokenSource;

        public MainForm()
        {
            InitializeComponent();
            LoadIcons();
        }

        private void LoadIcons()
        {
            try
            {
                string basePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources");
                string micPath = Path.Combine(basePath, "microphone.png");
                string mutePath = Path.Combine(basePath, "mute.png");
                string startPlayPath = Path.Combine(basePath, "start-play.png");
                string stopPlayPath = Path.Combine(basePath, "stop-play.png");
                string startCallPath = Path.Combine(basePath, "start-call.png");
                string hangUpPath = Path.Combine(basePath, "hang-up.png");

                if (File.Exists(micPath))
                {
                    _microphoneIcon = System.Drawing.Image.FromFile(micPath);
                    // Resize icon to fit button nicely
                    _microphoneIcon = new System.Drawing.Bitmap(_microphoneIcon, new System.Drawing.Size(32, 32));
                }

                if (File.Exists(mutePath))
                {
                    _muteIcon = System.Drawing.Image.FromFile(mutePath);
                    // Resize icon to fit button nicely
                    _muteIcon = new System.Drawing.Bitmap(_muteIcon, new System.Drawing.Size(32, 32));
                }

                if (File.Exists(startPlayPath))
                {
                    _startPlayIcon = System.Drawing.Image.FromFile(startPlayPath);
                    // Resize icon to fit button nicely
                    _startPlayIcon = new System.Drawing.Bitmap(_startPlayIcon, new System.Drawing.Size(32, 32));
                }

                if (File.Exists(stopPlayPath))
                {
                    _stopPlayIcon = System.Drawing.Image.FromFile(stopPlayPath);
                    // Resize icon to fit button nicely
                    _stopPlayIcon = new System.Drawing.Bitmap(_stopPlayIcon, new System.Drawing.Size(32, 32));
                }

                if (File.Exists(startCallPath))
                {
                    _startCallIcon = System.Drawing.Image.FromFile(startCallPath);
                    // Resize icon to fit button nicely
                    _startCallIcon = new System.Drawing.Bitmap(_startCallIcon, new System.Drawing.Size(32, 32));
                }

                if (File.Exists(hangUpPath))
                {
                    _hangUpIcon = System.Drawing.Image.FromFile(hangUpPath);
                    // Resize icon to fit button nicely
                    _hangUpIcon = new System.Drawing.Bitmap(_hangUpIcon, new System.Drawing.Size(32, 32));
                }

                // Set initial icon
                if (_microphoneIcon != null)
                {
                    btnRecord.Image = _microphoneIcon;
                    btnRecord.ImageAlign = System.Drawing.ContentAlignment.MiddleCenter;
                    btnRecord.Text = string.Empty;
                }

                if (_startPlayIcon != null)
                {
                    btnPlay.Image = _startPlayIcon;
                    btnPlay.ImageAlign = System.Drawing.ContentAlignment.MiddleCenter;
                }

                if (_startCallIcon != null)
                {
                    btnCall.Image = _startCallIcon;
                    btnCall.ImageAlign = System.Drawing.ContentAlignment.MiddleCenter;
                }
            }
            catch (Exception)
            {
                // Silently fail if icons can't be loaded
            }
        }

        private void btnRecord_Click(object sender, EventArgs e)
        {
            if (!_isRecording)
            {
                btnRecord.Enabled = false; // Disable to prevent double-clicks
                _ = StartRecordingAsync(); // Fire and forget but properly named
            }
            else
            {
                _ = StopRecordingAsync();
            }
        }

        private async Task StartRecordingAsync()
        {
            try
            {
                btnPlay.Enabled = false;

                // Stop playback immediately and completely
                try
                {
                    // Clean up any existing recording
                    StopAndDisposeWaveIn();
                    CleanupRecordingResources();

                    if (_audioFileReader != null)
                    {
                        _audioFileReader.Dispose();
                        _audioFileReader = null;
                    }

                    if (_startPlayIcon != null)
                        btnPlay.Image = _startPlayIcon;
                }
                catch
                {
                }

                statusLabel.Text = "Initializing recording...";

                // Prepare file path
                _audioFilePath = Path.Combine(Path.GetTempPath(), $"recorded_audio_{DateTime.Now:yyyyMMdd_HHmmss}.wav");

                // Check available devices
                int deviceCount = WaveInEvent.DeviceCount;
                if (deviceCount == 0)
                {
                    throw new InvalidOperationException("No recording devices found. Please connect a microphone.");
                }

                // Log device info for debugging
                var deviceInfo = WaveInEvent.GetCapabilities(0);
                System.Diagnostics.Debug.WriteLine($"Recording device: {deviceInfo.ProductName}, Channels: {deviceInfo.Channels}");

                // Try different sample rates until one works
                int[] sampleRates = { 24000, 44100, 48000, 16000, 22050, 8000 };
                List<string> attemptedRates = new List<string>();
                Exception? lastException = null;

                foreach (var sampleRate in sampleRates)
                {
                    try
                    {
                        // Configure recording format
                        _recordingFormat = new WaveFormat(sampleRate, 16, 1);

                        // Initialize WaveIn
                        _waveIn = new WaveInEvent
                        {
                            DeviceNumber = 0,
                            WaveFormat = _recordingFormat,
                            BufferMilliseconds = 100
                        };

                        // Create WAV file writer
                        _waveFileWriter = new WaveFileWriter(_audioFilePath, _recordingFormat);

                        // Mark as recording BEFORE hooking events to avoid race condition
                        _isRecording = true;

                        // Hook up events
                        _waveIn.DataAvailable += OnDataAvailable;
                        _waveIn.RecordingStopped += OnRecordingStopped;

                        // Start recording immediately - no delay needed before
                        _waveIn.StartRecording();

                        // Small delay to verify it started
                        await Task.Delay(50);

                        // Update UI on success
                        if (_muteIcon != null)
                            btnRecord.Image = _muteIcon;

                        trackSpeed.Enabled = false;
                        btnRecord.Enabled = true;

                        statusLabel.Text = $"Recording ({sampleRate}Hz)...";
                        System.Diagnostics.Debug.WriteLine($"Recording started successfully at {sampleRate}Hz");
                        return; // Success!
                    }
                    catch (Exception ex)
                    {
                        _isRecording = false;
                        attemptedRates.Add($"{sampleRate}Hz: {ex.Message}");
                        lastException = ex;
                        StopAndDisposeWaveIn();
                        CleanupRecordingResources();
                    }
                }

                // If we get here, none of the sample rates worked
                string attemptDetails = string.Join("\n", attemptedRates);
                throw new InvalidOperationException($"Failed to initialize recording with any sample rate.\n\nAttempts:\n{attemptDetails}", lastException);
            }
            catch (Exception ex)
            {
                _isRecording = false;
                StopAndDisposeWaveIn();
                CleanupRecordingResources();
                MessageBox.Show($"Error starting recording: {ex.Message}\n\nPlease check:\n1. Microphone is connected\n2. Windows microphone permissions are enabled\n3. Microphone is not in use by another app", "Recording Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                statusLabel.Text = "Ready to record.";

                if (_microphoneIcon != null)
                    btnRecord.Image = _microphoneIcon;
                btnRecord.Enabled = true;
            }
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception != null)
            {
                System.Diagnostics.Debug.WriteLine($"Recording stopped with error: {e.Exception.Message}");
                BeginInvoke(new Action(() =>
                {
                    statusLabel.Text = $"Recording error: {e.Exception.Message}";
                }));
            }
        }

        private void StopAndDisposeWaveIn()
        {
            if (_waveIn != null)
            {
                try
                {
                    // Unhook events first to prevent any callbacks during cleanup
                    _waveIn.DataAvailable -= OnDataAvailable;
                    _waveIn.RecordingStopped -= OnRecordingStopped;

                    // Stop recording if active
                    _waveIn.StopRecording();

                    // Give it a moment to finish
                    System.Threading.Thread.Sleep(100);
                }
                catch { }
                finally
                {
                    try
                    {
                        _waveIn.Dispose();
                    }
                    catch { }
                    _waveIn = null;
                }
            }
        }

        private void CleanupRecordingResources()
        {
            if (_waveFileWriter != null)
            {
                try
                {
                    _waveFileWriter.Flush();
                    _waveFileWriter.Close();
                    _waveFileWriter.Dispose();
                }
                catch { }
                finally
                {
                    _waveFileWriter = null;
                }
            }
            _recordingFormat = null;
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            try
            {
                if (_waveFileWriter != null && e.BytesRecorded > 0)
                {
                    _waveFileWriter.Write(e.Buffer, 0, e.BytesRecorded);

                    // Update UI safely
                    if (InvokeRequired)
                    {
                        BeginInvoke(new Action(() =>
                        {
                            statusLabel.Text = $"Recording... {_waveFileWriter.Length} bytes";
                        }));
                    }
                    else
                    {
                        statusLabel.Text = $"Recording... {_waveFileWriter.Length} bytes";
                    }
                }
            }
            catch (Exception ex)
            {
                // Log error but don't crash the recording thread
                System.Diagnostics.Debug.WriteLine($"Error writing audio data: {ex.Message}");
                BeginInvoke(new Action(() =>
                {
                    statusLabel.Text = $"Recording error: {ex.Message}";
                }));
            }
        }

        private async Task StopRecordingAsync()
        {
            try
            {
                _isRecording = false;

                if (_waveIn == null)
                {
                    // Reset UI even if waveIn is null
                    if (_microphoneIcon != null)
                        btnRecord.Image = _microphoneIcon;
                    statusLabel.Text = "Ready to record.";
                    return;
                }

                // Stop recording
                StopAndDisposeWaveIn();
                await Task.Delay(200);

                // Ensure file is closed
                CleanupRecordingResources();
                await Task.Delay(100);

                // Always reset button icon
                if (_microphoneIcon != null)
                    btnRecord.Image = _microphoneIcon;

                // Re-enable speed control if session is active
                if (_isCallActive)
                    trackSpeed.Enabled = true;

                // Check if we have audio
                if (!File.Exists(_audioFilePath))
                {
                    statusLabel.Text = "Audio file not found. Ready to record.";
                    btnPlay.Enabled = false;
                    return;
                }

                long fileSize = new FileInfo(_audioFilePath).Length;
                statusLabel.Text = $"Recorded file: {fileSize} bytes";

                // Enable playback if we have any data
                btnPlay.Enabled = fileSize > 44;

                // Send to API if connected (regardless of size for testing)
                if (_realtimeSession != null && _isCallActive && fileSize > 44)
                {
                    await SendAudioToAPIAsync();
                }
                else if (fileSize <= 44)
                {
                    await Task.Delay(1000);
                    statusLabel.Text = "No audio data captured. Check microphone permissions.";
                }
                else
                {
                    statusLabel.Text = "Recording saved. Not connected to API.";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error stopping recording: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                statusLabel.Text = "Ready to record.";
            }
            finally
            {
                // Ensure we're always in a valid state to record again
                _isRecording = false;
                if (_microphoneIcon != null)
                    btnRecord.Image = _microphoneIcon;
            }
        }

        private async Task SendAudioToAPIAsync()
        {
            if (_realtimeSession == null || !_isCallActive)
            {
                statusLabel.Text = "Not connected.";
                return;
            }

            if (!File.Exists(_audioFilePath))
            {
                statusLabel.Text = "No audio file found.";
                return;
            }

            try
            {
                // Ensure file is closed and flushed
                CleanupRecordingResources();
                await Task.Delay(100);

                // Use NAudio to properly read the PCM data (this handles the WAV header correctly)
                byte[] audioData;
                WaveFormat format;

                using (var reader = new WaveFileReader(_audioFilePath))
                {
                    format = reader.WaveFormat;

                    // Read all PCM data from the WAV file
                    using var ms = new MemoryStream();
                    byte[] buffer = new byte[reader.WaveFormat.AverageBytesPerSecond];
                    int bytesRead;
                    while ((bytesRead = reader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ms.Write(buffer, 0, bytesRead);
                    }
                    audioData = ms.ToArray();
                }

                if (audioData.Length == 0)
                {
                    statusLabel.Text = "No audio captured (no PCM data).";
                    return;
                }

                // Convert to 24kHz mono PCM16 (required by OpenAI Realtime API)
                byte[] monoAudio = ConvertToMono(audioData, format.Channels);
                byte[] resampledAudio = ResampleAudio(monoAudio, format.SampleRate, 24000);

                // Check for minimum 100ms of audio (24kHz mono PCM16 = 4800 bytes for 100ms)
                if (resampledAudio.Length < 4800)
                {
                    double durationMs = (resampledAudio.Length / 2.0) / 24000.0 * 1000.0;
                    statusLabel.Text = $"Audio too short: {resampledAudio.Length} bytes ({durationMs:F1}ms). Need 100ms minimum.";
                    return;
                }

                // Calculate and display duration
                double audioDurationMs = (resampledAudio.Length / 2.0) / 24000.0 * 1000.0;

                if (_clientMessageChannel != null)
                {
                    // Send audio append message with raw PCM bytes
                    await _clientMessageChannel.Writer.WriteAsync(new RealtimeClientInputAudioBufferAppendMessage(
                        audioContent: new DataContent($"data:audio/pcm;base64,{Convert.ToBase64String(resampledAudio)}")
                    ));

                    // Commit audio buffer
                    await _clientMessageChannel.Writer.WriteAsync(new RealtimeClientInputAudioBufferCommitMessage());

                    // Request response
                    await _clientMessageChannel.Writer.WriteAsync(new RealtimeClientResponseCreateMessage());

                    statusLabel.Text = $"Sent {resampledAudio.Length} bytes ({audioDurationMs:F0}ms).";
                }
                else
                {
                    statusLabel.Text = "Channel not initialized.";
                }
            }
            catch (Exception ex)
            {
                WriteErrorToRichTextBox($"[ERROR] {ex.Message}");
                statusLabel.Text = $"Error: {ex.Message}";
            }
        }

        private byte[] ConvertToMono(byte[] audioData, int channels)
        {
            if (channels <= 1)
            {
                return audioData;
            }

            // Currently we only expect mono or stereo input
            if (channels != 2)
            {
                throw new NotSupportedException($"Unsupported channel count: {channels}");
            }

            // 16-bit stereo: 4 bytes per sample (2 channels × 2 bytes)
            // 16-bit mono: 2 bytes per sample
            byte[] monoData = new byte[audioData.Length / 2];

            for (int i = 0; i < audioData.Length; i += 4)
            {
                short left = BitConverter.ToInt16(audioData, i);
                short right = BitConverter.ToInt16(audioData, i + 2);

                short mono = (short)((left + right) / 2);

                byte[] monoBytes = BitConverter.GetBytes(mono);
                monoData[i / 2] = monoBytes[0];
                monoData[i / 2 + 1] = monoBytes[1];
            }

            return monoData;
        }

        private byte[] ResampleAudio(byte[] input, int inputRate, int outputRate)
        {
            if (inputRate == outputRate)
            {
                return input;
            }

            // Simple linear interpolation resampling
            int inputSamples = input.Length / 2; // 16-bit = 2 bytes per sample
            int outputSamples = (int)((long)inputSamples * outputRate / inputRate);
            byte[] output = new byte[outputSamples * 2];

            for (int i = 0; i < outputSamples; i++)
            {
                double sourceIndex = (double)i * inputSamples / outputSamples;
                int index1 = (int)sourceIndex;
                int index2 = Math.Min(index1 + 1, inputSamples - 1);
                double fraction = sourceIndex - index1;

                short sample1 = BitConverter.ToInt16(input, index1 * 2);
                short sample2 = BitConverter.ToInt16(input, index2 * 2);

                short interpolated = (short)(sample1 + (sample2 - sample1) * fraction);

                byte[] bytes = BitConverter.GetBytes(interpolated);
                output[i * 2] = bytes[0];
                output[i * 2 + 1] = bytes[1];
            }

            return output;
        }

        private void btnPlay_Click(object sender, EventArgs e)
        {
            if (!File.Exists(_audioFilePath))
            {
                MessageBox.Show("No audio file to play. Please record first.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                if (_waveOut?.PlaybackState == PlaybackState.Playing)
                {
                    _waveOut.Stop();
                    _waveOut.Dispose();
                    _waveOut = null;
                    _audioFileReader?.Dispose();
                    _audioFileReader = null;
                    if (_startPlayIcon != null)
                        btnPlay.Image = _startPlayIcon;
                    statusLabel.Text = "Playback stopped.";
                }
                else
                {
                    _waveOut?.Dispose();
                    _audioFileReader?.Dispose();

                    _waveOut = new WaveOutEvent();
                    _audioFileReader = new AudioFileReader(_audioFilePath);

                    _waveOut.Init(_audioFileReader);
                    _waveOut.PlaybackStopped += (s, args) =>
                    {
                        _audioFileReader?.Dispose();
                        _audioFileReader = null;
                        Invoke(new Action(() =>
                        {
                            if (_startPlayIcon != null)
                                btnPlay.Image = _startPlayIcon;
                            statusLabel.Text = "Playback finished.";
                        }));
                    };

                    _waveOut.Play();
                    if (_stopPlayIcon != null)
                        btnPlay.Image = _stopPlayIcon;
                    statusLabel.Text = "Playing...";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error playing audio: {ex}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async void btnCall_Click(object sender, EventArgs e)
        {
            if (!_isCallActive)
            {
                // Disable button during connection attempt
                btnCall.Enabled = false;
                await StartCallAsync();
                // Re-enable button after attempt
                btnCall.Enabled = true;
            }
            else
            {
                await EndCallAsync();
            }
        }

        private async Task StartCallAsync()
        {
            try
            {
                var config = new ConfigurationBuilder().AddUserSecrets<MainForm>().Build();
                string? apiKey = config["OpenAIKey"];

                if (string.IsNullOrEmpty(apiKey))
                {
                    WriteErrorToRichTextBox("API key is not set.");
                    return;
                }

                // Create and configure realtime client
                _realtimeClient = new OpenAIRealtimeClient(apiKey);

                statusLabel.Text = "Connecting to OpenAI...";
                var session = await _realtimeClient.CreateSessionAsync();
                if (session == null)
                {
                    WriteErrorToRichTextBox("Failed to connect to OpenAI.");
                    statusLabel.Text = "Connection failed.";
                    return;
                }

                // Define a function that can be called by the AI
                AIFunction getWeatherFunction = AIFunctionFactory.Create(
                    (string location) =>
                        location switch
                        {
                            "Seattle" => $"The weather in {location} is rainy, 55°F",
                            "New York" => $"The weather in {location} is cloudy, 70°F",
                            "San Francisco" => $"The weather in {location} is foggy, 60°F",
                            _ => $"Sorry, I don't have weather data for {location}."
                        },
                    "GetWeather",
                    "Gets the current weather for a given location");

                //// Set up services (optional)
                var services = new ServiceCollection()
                    .AddLogging()
                    .BuildServiceProvider();

                var builder = new RealtimeSessionBuilder(session!)
                    .UseFunctionInvocation(configure: functionSession =>
                    {
                        // Add tools that can be invoked
                        functionSession.AdditionalTools = [getWeatherFunction];

                        // Optional configuration
                        functionSession.MaximumIterationsPerRequest = 10;
                        functionSession.AllowConcurrentInvocation = true;
                        functionSession.IncludeDetailedErrors = false;
                    });

                //// Build the session with function invocation enabled
                _realtimeSession = builder.Build(services);

                //// Update session options to include tools
                //await _realtimeSession.UpdateAsync(new RealtimeSessionOptions
                //{
                //    Tools = [getWeatherFunction]
                //});

                // Session is created, update UI
                _isCallActive = true;
                if (_hangUpIcon != null)
                    btnCall.Image = _hangUpIcon;
                btnRecord.Enabled = true;
                btnSend.Enabled = true;
                richTextBox2.Enabled = true;
                // cmbVoice.Enabled = false;
                trackSpeed.Enabled = true;
                statusLabel.Text = "Connected to OpenAI Realtime.";

                // Get selected voice
                string selectedVoice = "alloy"; // default
                if (cmbVoice.InvokeRequired)
                {
                    selectedVoice = (string)cmbVoice.Invoke(new Func<string>(() => cmbVoice.SelectedItem?.ToString() ?? "alloy"));
                }
                else
                {
                    selectedVoice = cmbVoice.SelectedItem?.ToString() ?? "alloy";
                }

                // Get speed value (0=0.25, 1=1.0, 2=1.5)
                double speedValue = GetSpeedValue();

                // Start streaming communication first
                await StartStreamingAsync();

                await _realtimeSession.UpdateAsync(new RealtimeSessionOptions
                {
                    OutputModalities = [ "audio" ],
                    Instructions = "You are a funny chat bot.",
                    Voice = selectedVoice,
                    VoiceSpeed = speedValue,
                    TranscriptionOptions = new TranscriptionOptions("en", "whisper-1"),
                    VoiceActivityDetection = new VoiceActivityDetection
                    {
                        CreateResponse = true,
                    },
                    Tools = [getWeatherFunction]
                });
            }
            catch (Exception ex)
            {
                WriteErrorToRichTextBox($"Error starting call: {ex}");
            }
        }

        private async Task StartStreamingAsync()
        {
            if (_realtimeSession == null)
                return;

            try
            {
                // Create channel for client messages
                _clientMessageChannel = Channel.CreateUnbounded<RealtimeClientMessage>();
                _streamingCancellationTokenSource = new CancellationTokenSource();

                // Start background task to process server messages
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (var serverMessage in _realtimeSession.GetStreamingResponseAsync(
                            _clientMessageChannel.Reader.ReadAllAsync(_streamingCancellationTokenSource.Token),
                            _streamingCancellationTokenSource.Token))
                        {
                            // Process server messages
                            ProcessServerMessage(serverMessage);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when cancelling
                    }
                    catch (Exception ex)
                    {
                        Invoke(new Action(() =>
                        {
                            WriteErrorToRichTextBox($"Streaming error: {ex.Message}");
                        }));
                    }
                }, _streamingCancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                WriteErrorToRichTextBox($"Error starting streaming: {ex.Message}");
            }
        }

        private void ProcessServerMessage(RealtimeServerMessage serverMessage)
        {
            Invoke(new Action(() =>
            {
                try
                {
                    string eventType = serverMessage.Type.ToString();
                    richTextBoxEvents?.AppendText($"{eventType} .... {serverMessage.EventId}\n");
                    richTextBoxEvents?.ScrollToCaret();
                    statusLabel.Text = $"[{eventType}]";

                    switch (serverMessage)
                    {
                        case RealtimeServerOutputTextAudioMessage audioMessage:
                            if (audioMessage.Type == RealtimeServerMessageType.OutputAudioDelta && audioMessage.Text != null)
                            {
                                PlayAudioChunk(audioMessage.Text);
                            }
                            else if (audioMessage.Type == RealtimeServerMessageType.OutputAudioTranscriptionDelta && audioMessage.Text != null)
                            {
                                WriteTranscriptToRichTextBox(audioMessage.Text);
                            }
                            else if (audioMessage.Type == RealtimeServerMessageType.OutputAudioTranscriptionDone)
                            {
                                richTextBox1.AppendText("\n");
                            }
                            break;

                        case RealtimeServerInputAudioTranscriptionMessage transcriptionMessage:
                            if (transcriptionMessage.Type == RealtimeServerMessageType.InputAudioTranscriptionCompleted &&
                                transcriptionMessage.Transcription != null)
                            {
                                WriteUserInputToRichTextBox($"You: {transcriptionMessage.Transcription}\n");
                            }
                            break;

                        case RealtimeServerErrorMessage errorMessage:
                            WriteErrorToRichTextBox($"Error: {errorMessage.Error?.Message}");
                            break;

                        case RealtimeServerResponseCreatedMessage responseMessage:
                            if (responseMessage.Usage != null)
                            {
                                richTextBoxEvents?.AppendText($"Usage - Input: {responseMessage.Usage.InputTokenCount}, Output: {responseMessage.Usage.OutputTokenCount}\n");
                            }
                            break;

                        case RealtimeServerResponseOutputItemMessage responseMessage:
                            if (responseMessage.Item is RealtimeContentItem contentItem)
                            {
                                foreach (var content in contentItem.Contents)
                                {
                                    if (content is FunctionCallContent functionCall)
                                    {
                                        if (functionCall.Arguments != null)
                                        {
                                            var argsList = new List<string>();
                                            foreach (var arg in functionCall.Arguments)
                                            {
                                                argsList.Add($"({arg.Key}, {arg.Value?.ToString()})");
                                            }
                                            richTextBoxEvents?.AppendText($"Function Call: {functionCall.Name} with arguments {string.Join(", ", argsList)}\n");
                                        }
                                        else
                                        {
                                            richTextBoxEvents?.AppendText($"Function Call: {functionCall.Name} with no arguments\n");
                                        }
                                    }
                                }
                            }
                            break;

                        default:
                            if (serverMessage.RawRepresentation is not null)
                            {
                                if (serverMessage.RawRepresentation is JsonElement rawElement && rawElement.TryGetProperty("type", out var typeProperty))
                                {
                                    richTextBoxEvents?.AppendText($"{serverMessage.Type} ... {typeProperty} \n");
                                }
                            }
                            // Handle other message types as needed
                            break;
                    }
                }
                catch (Exception ex)
                {
                    richTextBox1.AppendText($"Error processing message: {ex.Message}\n");
                }
            }));
        }

        private async Task EndCallAsync()
        {
            try
            {
                // Cancel streaming
                _streamingCancellationTokenSource?.Cancel();
                _streamingCancellationTokenSource?.Dispose();
                _streamingCancellationTokenSource = null;

                // Complete the client message channel
                _clientMessageChannel?.Writer.Complete();
                _clientMessageChannel = null;

                if (_realtimeSession != null)
                {
                    _realtimeSession.Dispose();
                    _realtimeSession = null;
                }

                if (_realtimeClient != null)
                {
                    _realtimeClient.Dispose();
                    _realtimeClient = null;
                }

                _isCallActive = false;
                if (_startCallIcon != null)
                    btnCall.Image = _startCallIcon;
                statusLabel.Text = "Call ended.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error ending call: {ex}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void PlayAudioChunk(string base64Audio)
        {
            try
            {
                byte[] audioData = Convert.FromBase64String(base64Audio);

                // Initialize provider if needed
                if (_audioProvider is null)
                {
                    var waveFormat = new WaveFormat(24000, 16, 1);
                    _audioProvider = new BufferedWaveProvider(waveFormat)
                    {
                        BufferDuration = TimeSpan.FromSeconds(60),
                        DiscardOnBufferOverflow = true
                    };
                }

                // Add samples to the buffer
                _audioProvider.AddSamples(audioData, 0, audioData.Length);

                // Start playback only after buffering at least 500ms of audio
                if (_waveOut == null || _waveOut.PlaybackState != PlaybackState.Playing)
                {
                    var bufferedDuration = TimeSpan.FromSeconds((double)_audioProvider.BufferedBytes / (_audioProvider.WaveFormat.AverageBytesPerSecond));

                    // Wait until we have at least 500ms buffered before starting playback
                    if (bufferedDuration.TotalMilliseconds >= 500)
                    {
                        if (_waveOut == null)
                        {
                            _waveOut = new WaveOutEvent();
                            _waveOut.Init(_audioProvider);
                        }
                        _waveOut.Play();
                    }
                }
            }
            catch (Exception ex)
            {
                WriteErrorToRichTextBox($"Error playing audio: {ex}");
            }
        }

        private void WriteErrorToRichTextBox(string errorMessage)
        {
            int startIndex = richTextBoxEvents.TextLength;
            richTextBoxEvents.AppendText($"{errorMessage}\n");
            richTextBoxEvents.Select(startIndex, errorMessage.Length);
            richTextBoxEvents.SelectionColor = System.Drawing.Color.Red;
            richTextBoxEvents.Select(richTextBoxEvents.TextLength, 0);
            richTextBoxEvents.SelectionColor = richTextBoxEvents.ForeColor;
            richTextBoxEvents.ScrollToCaret();
        }

        private void WriteTranscriptToRichTextBox(string transcript)
        {
            int startIndex = richTextBox1.TextLength;
            richTextBox1.AppendText($"{transcript}");
            richTextBox1.Select(startIndex, transcript.Length);
            richTextBox1.SelectionColor = System.Drawing.Color.Blue;
            richTextBox1.Select(richTextBox1.TextLength, 0);
            richTextBox1.SelectionColor = richTextBox1.ForeColor;
            richTextBox1.ScrollToCaret();
        }

        private void WriteUserInputToRichTextBox(string text)
        {
            int startIndex = richTextBox1.TextLength;
            richTextBox1.AppendText($"{text}");
            richTextBox1.Select(startIndex, text.Length);
            richTextBox1.SelectionColor = System.Drawing.Color.Green;
            richTextBox1.Select(richTextBox1.TextLength, 0);
            richTextBox1.SelectionColor = richTextBox1.ForeColor;
            richTextBox1.ScrollToCaret();
        }

        private void ResetCallButton()
        {
            _isCallActive = false;
            if (_startCallIcon != null)
                btnCall.Image = _startCallIcon;
            statusLabel.Text = "Ready to record.";
        }

        private double GetSpeedValue()
        {
            int trackValue = 1;
            if (trackSpeed.InvokeRequired)
            {
                trackValue = (int)trackSpeed.Invoke(new Func<int>(() => trackSpeed.Value));
            }
            else
            {
                trackValue = trackSpeed.Value;
            }

            // Map track positions to speed values: 0=0.25, 1=1.0, 2=1.5
            return trackValue switch
            {
                0 => 0.25,
                1 => 1.0,
                2 => 1.5,
                _ => 1.0
            };
        }

        private async void trackSpeed_ValueChanged(object? sender, EventArgs e)
        {
            // Only update if session is active and not recording
            if (_realtimeSession != null && _isCallActive && !_isRecording)
            {
                try
                {
                    double speedValue = GetSpeedValue();
                    string selectedVoice = cmbVoice.SelectedItem?.ToString() ?? "alloy";

                    await _realtimeSession.UpdateAsync(new RealtimeSessionOptions
                    {
                        OutputModalities = ["audio"],
                        Instructions = "You are a funny chat bot.",
                        Voice = selectedVoice,
                        VoiceSpeed = speedValue,
                        TranscriptionOptions = new TranscriptionOptions("en", "whisper-1"),
                        VoiceActivityDetection = new VoiceActivityDetection
                        {
                            CreateResponse = true,
                        },
                    });

                    statusLabel.Text = $"Speed updated to {speedValue}x";
                }
                catch (Exception ex)
                {
                    statusLabel.Text = $"Error updating speed: {ex.Message}";
                }
            }
        }

        private async void btnSend_Click(object sender, EventArgs e)
        {
            if (_realtimeSession == null || !_isCallActive)
            {
                WriteErrorToRichTextBox("Not connected to OpenAI.");
                return;
            }

            string text = richTextBox2.Text.Trim();
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            try
            {
                statusLabel.Text = "Sending text...";

                // Check if text is an image URL
                if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleImageUrlAsync(text);
                }
                else
                {
                    // Display sent text in dark green in richTextBox1
                    WriteUserTextToRichTextBox($"You: {text}\n");

                    if (_clientMessageChannel != null)
                    {
                        // Create conversation item with text
                        var contentItem = new RealtimeContentItem(
                            new[] { new TextContent(text) },
                            id: null,
                            role: ChatRole.User
                        );
                        await _clientMessageChannel.Writer.WriteAsync(new RealtimeClientConversationItemCreateMessage(
                            item: contentItem
                        ));

                        // Request response
                        await _clientMessageChannel.Writer.WriteAsync(new RealtimeClientResponseCreateMessage());

                        statusLabel.Text = "Text sent. Waiting for response...";
                    }
                }

                richTextBox2.Clear();
            }
            catch (Exception ex)
            {
                WriteErrorToRichTextBox($"Error sending text: {ex.Message}");
                statusLabel.Text = "Error sending text.";
            }
        }

        private void richTextBox2_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                // Prevent the newline from being added
                e.SuppressKeyPress = true;

                // Send the text
                btnSend_Click(sender, e);
            }
        }

        private void WriteUserTextToRichTextBox(string text)
        {
            int startIndex = richTextBox1.TextLength;
            richTextBox1.AppendText(text);
            richTextBox1.Select(startIndex, text.Length);
            richTextBox1.SelectionColor = System.Drawing.Color.DarkGreen;
            richTextBox1.Select(richTextBox1.TextLength, 0);
            richTextBox1.SelectionColor = richTextBox1.ForeColor;
            richTextBox1.ScrollToCaret();
        }

        private async Task HandleImageUrlAsync(string imageUrl)
        {
            try
            {
                statusLabel.Text = "Loading image...";

                // Download the image
                using (var httpClient = new HttpClient())
                {
                    // Add user agent to avoid 403 errors on some sites
                    httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                    httpClient.Timeout = TimeSpan.FromSeconds(10);

                    var response = await httpClient.GetAsync(imageUrl);

                    if (!response.IsSuccessStatusCode)
                    {
                        WriteErrorToRichTextBox($"Failed to download image: {response.StatusCode} - {response.ReasonPhrase}\n" +
                                              $"URL: {imageUrl}\n" +
                                              $"Note: The server may require authentication or block direct downloads.");
                        statusLabel.Text = "Error loading image.";
                        return;
                    }

                    byte[] imageBytes = await response.Content.ReadAsByteArrayAsync();

                    // Convert to base64
                    string base64Image = Convert.ToBase64String(imageBytes);

                    // Determine MIME type from content type header or URL
                    string mimeType = response.Content.Headers.ContentType?.MediaType ?? "image/png";

                    // Fallback to URL extension if content type is not available
                    if (mimeType == "application/octet-stream" || !mimeType.StartsWith("image/"))
                    {
                        if (imageUrl.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                            imageUrl.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                        {
                            mimeType = "image/jpeg";
                        }
                        else if (imageUrl.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                        {
                            mimeType = "image/gif";
                        }
                        else if (imageUrl.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                        {
                            mimeType = "image/png";
                        }
                    }

                    // Send image to OpenAI through channel
                    if (_clientMessageChannel != null)
                    {
                        // Create conversation item with image
                        var contentItem = new RealtimeContentItem(
                            new [] { new DataContent($"data:{mimeType};base64,{base64Image}") },
                            id: null,
                            role: ChatRole.User
                        );
                        await _clientMessageChannel.Writer.WriteAsync(new RealtimeClientConversationItemCreateMessage(
                            item: contentItem
                        ));

                        // Request response
                        await _clientMessageChannel.Writer.WriteAsync(new RealtimeClientResponseCreateMessage());
                    }

                    // Insert image into richTextBox1
                    using (var ms = new MemoryStream(imageBytes))
                    {
                        var image = System.Drawing.Image.FromStream(ms);
                        InsertImageToRichTextBox(image, imageUrl);
                    }

                    statusLabel.Text = "Image sent. Waiting for response...";
                }
            }
            catch (HttpRequestException ex)
            {
                WriteErrorToRichTextBox($"Network error downloading image: {ex.Message}\nURL: {imageUrl}");
                statusLabel.Text = "Error loading image.";
            }
            catch (TaskCanceledException)
            {
                WriteErrorToRichTextBox($"Download timeout: The image took too long to download.\nURL: {imageUrl}");
                statusLabel.Text = "Download timeout.";
            }
            catch (Exception ex)
            {
                WriteErrorToRichTextBox($"Error handling image: {ex.Message}\nURL: {imageUrl}");
                statusLabel.Text = "Error loading image.";
            }
        }

        private void InsertImageToRichTextBox(System.Drawing.Image image, string url)
        {
            try
            {
                // Add label before image
                WriteUserTextToRichTextBox($"You (Image): {url}\n");

                // Convert image to RTF format
                string rtfImage = GetRtfImage(image);

                // Insert the RTF image
                richTextBox1.Select(richTextBox1.TextLength, 0);
                richTextBox1.SelectedRtf = rtfImage;
                richTextBox1.AppendText("\n");
                richTextBox1.ScrollToCaret();
            }
            catch (Exception ex)
            {
                WriteErrorToRichTextBox($"Error inserting image: {ex.Message}");
            }
        }

        private string GetRtfImage(System.Drawing.Image image)
        {
            // Resize image if too large
            int maxWidth = 300;
            int maxHeight = 300;

            if (image.Width > maxWidth || image.Height > maxHeight)
            {
                double ratioW = (double)maxWidth / image.Width;
                double ratioH = (double)maxHeight / image.Height;
                double ratio = Math.Min(ratioW, ratioH);

                int newWidth = (int)(image.Width * ratio);
                int newHeight = (int)(image.Height * ratio);

                var resized = new System.Drawing.Bitmap(newWidth, newHeight);
                using (var g = System.Drawing.Graphics.FromImage(resized))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(image, 0, 0, newWidth, newHeight);
                }
                image = resized;
            }

            using (var ms = new MemoryStream())
            {
                image.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                byte[] bytes = ms.ToArray();
                string hexString = BitConverter.ToString(bytes).Replace("-", "");

                return @"{\rtf1{\pict\pngblip\picw" +
                       image.Width + @"\pich" + image.Height +
                       @"\picwgoal" + (image.Width * 15) +
                       @"\pichgoal" + (image.Height * 15) +
                       @" " + hexString + @"}}";
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);

            _waveOut?.Stop();
            _waveOut?.Dispose();
            StopAndDisposeWaveIn();
            CleanupRecordingResources();
            _microphoneIcon?.Dispose();
            _muteIcon?.Dispose();
            _startPlayIcon?.Dispose();
            _stopPlayIcon?.Dispose();
            _startCallIcon?.Dispose();
            _hangUpIcon?.Dispose();
            _realtimeClient?.Dispose();
        }
    }
}
