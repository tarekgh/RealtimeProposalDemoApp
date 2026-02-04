using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
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
        private WaveIn? _waveIn;
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
        private ActivityListener? _activityListener;
        private MeterListener? _meterListener;
        private readonly object _recordingLock = new();
        private int _selectedDeviceNumber = 0;
        private int _workingSampleRate = 44100;

        public MainForm()
        {
            InitializeComponent();
            LoadIcons();
            SetupTelemetryListeners();
            InitializeAudioDevice();
        }

        private void InitializeAudioDevice()
        {
            // Find and validate available recording devices at startup
            try
            {
                int deviceCount = WaveIn.DeviceCount;
                System.Diagnostics.Debug.WriteLine($"Found {deviceCount} recording devices");
                
                if (deviceCount > 0)
                {
                    // List all devices
                    for (int i = 0; i < deviceCount; i++)
                    {
                        var capabilities = WaveIn.GetCapabilities(i);
                        System.Diagnostics.Debug.WriteLine($"Audio device {i}: {capabilities.ProductName}, Channels: {capabilities.Channels}");
                    }

                    // Use device 0 (default)
                    _selectedDeviceNumber = 0;
                    var selectedDevice = WaveIn.GetCapabilities(_selectedDeviceNumber);
                    statusLabel.Text = $"Ready. Device: {selectedDevice.ProductName}";
                    
                    // Test which sample rate works
                    TestSampleRates();
                }
                else
                {
                    statusLabel.Text = "No microphone detected.";
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error initializing audio device: {ex.Message}");
                statusLabel.Text = "Error detecting microphone.";
            }
        }

        private void TestSampleRates()
        {
            // Test sample rates at startup to find one that works
            int[] sampleRates = [44100, 48000, 16000, 24000, 22050, 8000];
            
            foreach (var rate in sampleRates)
            {
                try
                {
                    using var testWaveIn = new WaveIn
                    {
                        DeviceNumber = _selectedDeviceNumber,
                        WaveFormat = new WaveFormat(rate, 16, 1)
                    };
                    // If we get here without exception, this rate works
                    _workingSampleRate = rate;
                    System.Diagnostics.Debug.WriteLine($"Sample rate {rate}Hz works for this device");
                    return;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Sample rate {rate}Hz failed: {ex.Message}");
                }
            }
        }

        private void SetupTelemetryListeners()
        {
            // Set up ActivityListener to capture Activity stop events
            _activityListener = new ActivityListener
            {
                // Listen to activities from Microsoft.Extensions.AI sources
                ShouldListenTo = source => source.Name.StartsWith("Microsoft.Extensions.AI") ||
                                           source.Name.StartsWith("OpenAI") ||
                                           source.Name.StartsWith("Experimental.Microsoft.Extensions.AI"),
                Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    WriteActivityToLog(activity);
                }
            };
            ActivitySource.AddActivityListener(_activityListener);

            // Set up MeterListener to capture metrics
            _meterListener = new MeterListener();
            _meterListener.InstrumentPublished = (instrument, listener) =>
            {
                // Listen to metrics from Microsoft.Extensions.AI sources
                if (instrument.Meter.Name.StartsWith("Microsoft.Extensions.AI") ||
                    instrument.Meter.Name.StartsWith("OpenAI") ||
                    instrument.Meter.Name.StartsWith("Experimental.Microsoft.Extensions.AI"))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };

            // Handle different measurement types
            _meterListener.SetMeasurementEventCallback<long>(OnMeasurementRecorded);
            _meterListener.SetMeasurementEventCallback<int>(OnMeasurementRecorded);
            _meterListener.SetMeasurementEventCallback<double>(OnMeasurementRecorded);
            _meterListener.SetMeasurementEventCallback<float>(OnMeasurementRecorded);

            _meterListener.Start();
        }

        private void WriteActivityToLog(Activity activity)
        {
            if (richTextBoxLogs == null || richTextBoxLogs.IsDisposed)
                return;

            var sb = new StringBuilder();
            sb.AppendLine($"[Activity] {activity.OperationName}");
            sb.AppendLine($"  Duration: {activity.Duration.TotalMilliseconds:F2}ms");
            sb.AppendLine($"  Status: {activity.Status}");

            if (activity.Tags.Any())
            {
                sb.AppendLine("  Tags:");
                foreach (var tag in activity.Tags)
                {
                    // Truncate long values for readability
                    var value = tag.Value?.Length > 100 ? tag.Value[..100] + "..." : tag.Value;
                    sb.AppendLine($"    {tag.Key}: {value}");
                }
            }

            if (activity.Events.Any())
            {
                sb.AppendLine("  Events:");
                foreach (var evt in activity.Events)
                {
                    sb.AppendLine($"    {evt.Name} @ {evt.Timestamp:HH:mm:ss.fff}");
                    foreach (var evtTag in evt.Tags)
                    {
                        var value = evtTag.Value?.ToString();
                        if (value?.Length > 100)
                            value = value[..100] + "...";
                        sb.AppendLine($"      {evtTag.Key}: {value}");
                    }
                }
            }

            WriteToLogRichTextBox(sb.ToString(), System.Drawing.Color.Purple);
        }

        private void OnMeasurementRecorded<T>(Instrument instrument, T measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        {
            var sb = new StringBuilder();
            sb.Append($"[Metric] {instrument.Meter.Name}/{instrument.Name}: {measurement} {instrument.Unit}");

            if (tags.Length > 0)
            {
                sb.Append(" | Tags: ");
                var tagStrings = new List<string>();
                foreach (var tag in tags)
                {
                    tagStrings.Add($"{tag.Key}={tag.Value}");
                }
                sb.Append(string.Join(", ", tagStrings));
            }

            WriteToLogRichTextBox(sb.ToString(), System.Drawing.Color.Teal);
        }

        private void WriteToLogRichTextBox(string message, System.Drawing.Color color)
        {
            if (richTextBoxLogs == null || richTextBoxLogs.IsDisposed)
                return;

            if (richTextBoxLogs.InvokeRequired)
            {
                richTextBoxLogs.BeginInvoke(() => AppendLogText(message, color));
            }
            else
            {
                AppendLogText(message, color);
            }
        }

        private void AppendLogText(string message, System.Drawing.Color color)
        {
            try
            {
                if (richTextBoxLogs.IsDisposed)
                    return;

                int startIndex = richTextBoxLogs.TextLength;
                richTextBoxLogs.AppendText(message + Environment.NewLine);
                richTextBoxLogs.Select(startIndex, message.Length);
                richTextBoxLogs.SelectionColor = color;
                richTextBoxLogs.Select(richTextBoxLogs.TextLength, 0);
                richTextBoxLogs.SelectionColor = richTextBoxLogs.ForeColor;
                richTextBoxLogs.ScrollToCaret();
            }
            catch (ObjectDisposedException)
            {
                // Control was disposed, ignore
            }
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
                StartRecording();
            }
            else
            {
                StopRecording();
            }
        }

        private void StartRecording()
        {
            try
            {
                btnRecord.Enabled = false;
                btnPlay.Enabled = false;

                // Clean up any existing resources
                StopAndDisposeWaveIn();
                CleanupRecordingResources();

                // Stop playback if playing
                if (_waveOut?.PlaybackState == PlaybackState.Playing)
                {
                    _waveOut.Stop();
                }

                statusLabel.Text = "Starting recording...";

                // Prepare file path
                _audioFilePath = Path.Combine(Path.GetTempPath(), $"recorded_audio_{DateTime.Now:yyyyMMdd_HHmmss}.wav");

                // Check available devices
                int deviceCount = WaveIn.DeviceCount;
                if (deviceCount == 0)
                {
                    throw new InvalidOperationException("No recording devices found.");
                }

                // Configure recording format using the working sample rate
                _recordingFormat = new WaveFormat(_workingSampleRate, 16, 1);

                // Use WaveIn (callback-based, works better with Windows Forms)
                _waveIn = new WaveIn
                {
                    DeviceNumber = _selectedDeviceNumber,
                    WaveFormat = _recordingFormat,
                    BufferMilliseconds = 100,
                    NumberOfBuffers = 3
                };

                // Create WAV file writer
                _waveFileWriter = new WaveFileWriter(_audioFilePath, _recordingFormat);

                // Hook up the data available event
                _waveIn.DataAvailable += WaveIn_DataAvailable;
                _waveIn.RecordingStopped += WaveIn_RecordingStopped;

                // Start recording
                _waveIn.StartRecording();
                _isRecording = true;

                // Update UI
                if (_muteIcon != null)
                    btnRecord.Image = _muteIcon;
                
                trackSpeed.Enabled = false;
                btnRecord.Enabled = true;

                statusLabel.Text = $"Recording at {_workingSampleRate}Hz... Speak now!";
                System.Diagnostics.Debug.WriteLine($"Recording started at {_workingSampleRate}Hz");
            }
            catch (Exception ex)
            {
                _isRecording = false;
                StopAndDisposeWaveIn();
                CleanupRecordingResources();

                MessageBox.Show(
                    $"Error starting recording: {ex.Message}\n\nPlease check:\n" +
                    "1. Microphone is connected\n" +
                    "2. Microphone permissions are enabled\n" +
                    "3. No other app is using the microphone",
                    "Recording Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);

                statusLabel.Text = "Recording failed.";
                if (_microphoneIcon != null)
                    btnRecord.Image = _microphoneIcon;
                btnRecord.Enabled = true;
            }
        }

        private void WaveIn_DataAvailable(object? sender, WaveInEventArgs e)
        {
            try
            {
                if (_waveFileWriter != null && e.BytesRecorded > 0)
                {
                    _waveFileWriter.Write(e.Buffer, 0, e.BytesRecorded);

                    // Calculate peak level for visual feedback
                    float maxLevel = 0;
                    for (int i = 0; i < e.BytesRecorded - 1; i += 2)
                    {
                        short sample = (short)(e.Buffer[i] | (e.Buffer[i + 1] << 8));
                        float level = Math.Abs(sample / 32768f);
                        if (level > maxLevel) maxLevel = level;
                    }

                    // Update UI (throttled)
                    long length = _waveFileWriter.Length;
                    if (length % 8000 < e.BytesRecorded)
                    {
                        BeginInvoke(() =>
                        {
                            string indicator = maxLevel > 0.05f ? "🔊 Sound detected" : "🎤 Listening...";
                            statusLabel.Text = $"Recording... {length / 1024}KB | {indicator}";
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in DataAvailable: {ex.Message}");
            }
        }

        private void WaveIn_RecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception != null)
            {
                System.Diagnostics.Debug.WriteLine($"Recording stopped with error: {e.Exception.Message}");
                BeginInvoke(() =>
                {
                    statusLabel.Text = $"Recording error: {e.Exception.Message}";
                });
            }
        }

        private void StopRecording()
        {
            try
            {
                btnRecord.Enabled = false;
                statusLabel.Text = "Stopping recording...";

                _isRecording = false;

                // Stop and dispose WaveIn
                StopAndDisposeWaveIn();

                // Close the file writer
                CleanupRecordingResources();

                // Reset button
                if (_microphoneIcon != null)
                    btnRecord.Image = _microphoneIcon;

                // Re-enable speed control if session is active
                if (_isCallActive)
                    trackSpeed.Enabled = true;

                btnRecord.Enabled = true;

                // Check file
                if (File.Exists(_audioFilePath))
                {
                    var fileInfo = new FileInfo(_audioFilePath);
                    long audioBytes = fileInfo.Length - 44; // Subtract WAV header

                    if (audioBytes > 0)
                    {
                        btnPlay.Enabled = true;
                        statusLabel.Text = $"Recorded {audioBytes / 1024}KB of audio.";

                        // Send to API if connected
                        if (_realtimeSession != null && _isCallActive)
                        {
                            _ = SendAudioToAPIAsync();
                        }
                    }
                    else
                    {
                        statusLabel.Text = "No audio captured. Check microphone.";
                        btnPlay.Enabled = false;
                    }
                }
                else
                {
                    statusLabel.Text = "Recording failed - no file created.";
                    btnPlay.Enabled = false;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error stopping recording: {ex.Message}");
                statusLabel.Text = "Error stopping recording.";
                btnRecord.Enabled = true;
                if (_microphoneIcon != null)
                    btnRecord.Image = _microphoneIcon;
            }
        }

        private void StopAndDisposeWaveIn()
        {
            if (_waveIn != null)
            {
                try
                {
                    _waveIn.DataAvailable -= WaveIn_DataAvailable;
                    _waveIn.RecordingStopped -= WaveIn_RecordingStopped;
                    _waveIn.StopRecording();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error stopping WaveIn: {ex.Message}");
                }
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
                    _waveFileWriter.Dispose();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error closing file writer: {ex.Message}");
                }
                finally
                {
                    _waveFileWriter = null;
                }
            }
            _recordingFormat = null;
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
                statusLabel.Text = "Sending audio...";

                // Read the audio file
                byte[] audioData;
                WaveFormat format;

                using (var reader = new WaveFileReader(_audioFilePath))
                {
                    format = reader.WaveFormat;

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
                    statusLabel.Text = "No audio data captured.";
                    return;
                }

                // Convert to 24kHz mono PCM16 (required by OpenAI)
                byte[] monoAudio = ConvertToMono(audioData, format.Channels);
                byte[] resampledAudio = ResampleAudio(monoAudio, format.SampleRate, 24000);

                // Check minimum length (100ms = 4800 bytes at 24kHz 16-bit mono)
                if (resampledAudio.Length < 4800)
                {
                    double durationMs = (resampledAudio.Length / 2.0) / 24000.0 * 1000.0;
                    statusLabel.Text = $"Audio too short ({durationMs:F0}ms). Need 100ms+.";
                    return;
                }

                double audioDurationMs = (resampledAudio.Length / 2.0) / 24000.0 * 1000.0;

                if (_clientMessageChannel != null)
                {
                    await _clientMessageChannel.Writer.WriteAsync(new RealtimeClientInputAudioBufferAppendMessage(
                        audioContent: new DataContent($"data:audio/pcm;base64,{Convert.ToBase64String(resampledAudio)}")
                    ));

                    await _clientMessageChannel.Writer.WriteAsync(new RealtimeClientInputAudioBufferCommitMessage());
                    await _clientMessageChannel.Writer.WriteAsync(new RealtimeClientResponseCreateMessage());

                    statusLabel.Text = $"Sent {audioDurationMs:F0}ms of audio.";
                }
            }
            catch (Exception ex)
            {
                WriteErrorToRichTextBox($"Error sending audio: {ex.Message}");
                statusLabel.Text = "Error sending audio.";
            }
        }

        private byte[] ConvertToMono(byte[] audioData, int channels)
        {
            if (channels <= 1)
                return audioData;

            if (channels != 2)
                throw new NotSupportedException($"Unsupported channel count: {channels}");

            byte[] monoData = new byte[audioData.Length / 2];

            for (int i = 0; i < audioData.Length - 3; i += 4)
            {
                short left = BitConverter.ToInt16(audioData, i);
                short right = BitConverter.ToInt16(audioData, i + 2);
                short mono = (short)((left + right) / 2);

                byte[] monoBytes = BitConverter.GetBytes(mono);
                int destIndex = i / 2;
                monoData[destIndex] = monoBytes[0];
                monoData[destIndex + 1] = monoBytes[1];
            }

            return monoData;
        }

        private byte[] ResampleAudio(byte[] input, int inputRate, int outputRate)
        {
            if (inputRate == outputRate)
                return input;

            int inputSamples = input.Length / 2;
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
                MessageBox.Show("No audio file to play.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                        Invoke(() =>
                        {
                            if (_startPlayIcon != null)
                                btnPlay.Image = _startPlayIcon;
                            statusLabel.Text = "Playback finished.";
                        });
                    };

                    _waveOut.Play();
                    if (_stopPlayIcon != null)
                        btnPlay.Image = _stopPlayIcon;
                    statusLabel.Text = "Playing...";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error playing audio: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async void btnCall_Click(object sender, EventArgs e)
        {
            if (!_isCallActive)
            {
                btnCall.Enabled = false;
                await StartCallAsync();
                btnCall.Enabled = true;
            }
            else
            {
                await EndCallAsync();
            }
        }

        private LogLevel GetSelectedLogLevel()
        {
            int selectedIndex = 6;
            if (cmbLogLevel.InvokeRequired)
            {
                selectedIndex = (int)cmbLogLevel.Invoke(new Func<int>(() => cmbLogLevel.SelectedIndex));
            }
            else
            {
                selectedIndex = cmbLogLevel.SelectedIndex;
            }

            return selectedIndex switch
            {
                0 => LogLevel.Trace,
                1 => LogLevel.Debug,
                2 => LogLevel.Information,
                3 => LogLevel.Warning,
                4 => LogLevel.Error,
                5 => LogLevel.Critical,
                6 => LogLevel.None,
                _ => LogLevel.None
            };
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

                _realtimeClient = new OpenAIRealtimeClient(apiKey);

                statusLabel.Text = "Connecting to OpenAI...";
                var session = await _realtimeClient.CreateSessionAsync();
                if (session == null)
                {
                    WriteErrorToRichTextBox("Failed to connect to OpenAI.");
                    statusLabel.Text = "Connection failed.";
                    return;
                }

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

                var services = new ServiceCollection()
                    .AddLogging(builder =>
                    {
                        builder.SetMinimumLevel(GetSelectedLogLevel());
                        builder.AddRichTextBox(richTextBoxLogs, GetSelectedLogLevel);
                    })
                    .BuildServiceProvider();

                var builder = new RealtimeSessionBuilder(session!)
                    .UseFunctionInvocation(configure: functionSession =>
                    {
                        functionSession.AdditionalTools = [getWeatherFunction];
                        functionSession.MaximumIterationsPerRequest = 10;
                        functionSession.AllowConcurrentInvocation = true;
                        functionSession.IncludeDetailedErrors = false;
                    })
                    .UseOpenTelemetry(configure: otel =>
                    {
                        otel.EnableSensitiveData = true;
                    })
                    .UseLogging();

                _realtimeSession = builder.Build(services);

                _isCallActive = true;
                if (_hangUpIcon != null)
                    btnCall.Image = _hangUpIcon;
                btnRecord.Enabled = true;
                btnSend.Enabled = true;
                richTextBox2.Enabled = true;
                trackSpeed.Enabled = true;
                cmbLogLevel.Enabled = false;
                cmbVoice.Enabled = false;
                statusLabel.Text = "Connected to OpenAI Realtime.";

                string selectedVoice = cmbVoice.SelectedItem?.ToString() ?? "alloy";
                double speedValue = GetSpeedValue();

                await StartStreamingAsync();

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
                _clientMessageChannel = Channel.CreateUnbounded<RealtimeClientMessage>();
                _streamingCancellationTokenSource = new CancellationTokenSource();

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (var serverMessage in _realtimeSession.GetStreamingResponseAsync(
                            _clientMessageChannel.Reader.ReadAllAsync(_streamingCancellationTokenSource.Token),
                            _streamingCancellationTokenSource.Token))
                        {
                            ProcessServerMessage(serverMessage);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        Invoke(() => WriteErrorToRichTextBox($"Streaming error: {ex.Message}"));
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
            Invoke(() =>
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
                            break;
                    }
                }
                catch (Exception ex)
                {
                    richTextBox1.AppendText($"Error processing message: {ex.Message}\n");
                }
            });
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
                cmbLogLevel.Enabled = true;
                cmbVoice.Enabled = true;
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

                if (_audioProvider is null)
                {
                    var waveFormat = new WaveFormat(24000, 16, 1);
                    _audioProvider = new BufferedWaveProvider(waveFormat)
                    {
                        BufferDuration = TimeSpan.FromSeconds(60),
                        DiscardOnBufferOverflow = true
                    };
                }

                _audioProvider.AddSamples(audioData, 0, audioData.Length);

                if (_waveOut == null || _waveOut.PlaybackState != PlaybackState.Playing)
                {
                    var bufferedDuration = TimeSpan.FromSeconds((double)_audioProvider.BufferedBytes / _audioProvider.WaveFormat.AverageBytesPerSecond);

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
            int trackValue = trackSpeed.Value;
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
                return;

            try
            {
                statusLabel.Text = "Sending text...";

                if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleImageUrlAsync(text);
                }
                else
                {
                    WriteUserTextToRichTextBox($"You: {text}\n");

                    if (_clientMessageChannel != null)
                    {
                        var contentItem = new RealtimeContentItem(
                            [new TextContent(text)],
                            id: null,
                            role: ChatRole.User
                        );
                        await _clientMessageChannel.Writer.WriteAsync(new RealtimeClientConversationItemCreateMessage(item: contentItem));
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
                e.SuppressKeyPress = true;
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

                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
                httpClient.Timeout = TimeSpan.FromSeconds(10);

                var response = await httpClient.GetAsync(imageUrl);

                if (!response.IsSuccessStatusCode)
                {
                    WriteErrorToRichTextBox($"Failed to download image: {response.StatusCode}");
                    statusLabel.Text = "Error loading image.";
                    return;
                }

                byte[] imageBytes = await response.Content.ReadAsByteArrayAsync();
                string base64Image = Convert.ToBase64String(imageBytes);
                string mimeType = response.Content.Headers.ContentType?.MediaType ?? "image/png";

                if (mimeType == "application/octet-stream" || !mimeType.StartsWith("image/"))
                {
                    if (imageUrl.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || imageUrl.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                        mimeType = "image/jpeg";
                    else if (imageUrl.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                        mimeType = "image/gif";
                    else
                        mimeType = "image/png";
                }

                if (_clientMessageChannel != null)
                {
                    var contentItem = new RealtimeContentItem(
                        [new DataContent($"data:{mimeType};base64,{base64Image}")],
                        id: null,
                        role: ChatRole.User
                    );
                    await _clientMessageChannel.Writer.WriteAsync(new RealtimeClientConversationItemCreateMessage(item: contentItem));
                    await _clientMessageChannel.Writer.WriteAsync(new RealtimeClientResponseCreateMessage());
                }

                using var ms = new MemoryStream(imageBytes);
                var image = System.Drawing.Image.FromStream(ms);
                InsertImageToRichTextBox(image, imageUrl);

                statusLabel.Text = "Image sent. Waiting for response...";
            }
            catch (Exception ex)
            {
                WriteErrorToRichTextBox($"Error handling image: {ex.Message}");
                statusLabel.Text = "Error loading image.";
            }
        }

        private void InsertImageToRichTextBox(System.Drawing.Image image, string url)
        {
            try
            {
                WriteUserTextToRichTextBox($"You (Image): {url}\n");
                string rtfImage = GetRtfImage(image);
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
            int maxWidth = 300;
            int maxHeight = 300;

            if (image.Width > maxWidth || image.Height > maxHeight)
            {
                double ratio = Math.Min((double)maxWidth / image.Width, (double)maxHeight / image.Height);
                int newWidth = (int)(image.Width * ratio);
                int newHeight = (int)(image.Height * ratio);

                var resized = new System.Drawing.Bitmap(newWidth, newHeight);
                using var g = System.Drawing.Graphics.FromImage(resized);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(image, 0, 0, newWidth, newHeight);
                image = resized;
            }

            using var ms = new MemoryStream();
            image.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            byte[] bytes = ms.ToArray();
            string hexString = BitConverter.ToString(bytes).Replace("-", "");

            return $@"{{\rtf1{{\pict\pngblip\picw{image.Width}\pich{image.Height}\picwgoal{image.Width * 15}\pichgoal{image.Height * 15} {hexString}}}}}";
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);

            _activityListener?.Dispose();
            _meterListener?.Dispose();

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

        private void cmbLogLevel_SelectedIndexChanged(object sender, EventArgs e)
        {
        }
    }
}
