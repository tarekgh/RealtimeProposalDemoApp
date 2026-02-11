# RealtimePlayGround

A C# Windows Forms demo application built to test and showcase the **Microsoft.Extensions.AI.Abstractions** realtime API surface. It connects to OpenAI's Realtime API through the `Microsoft.Extensions.AI` abstraction layer, enabling voice-based conversations with an AI assistant — complete with tool calling, telemetry, and logging — all driven by the new `IRealtimeClient` / `IRealtimeSession` interfaces.

## Purpose

This project serves as a playground and integration test-bed for the **Microsoft.Extensions.AI Realtime** proposal. It exercises the following parts of the abstraction:

| Abstraction concept | How it is used here |
|---|---|
| `IRealtimeClient` / `IRealtimeSession` | Create and manage a realtime session with OpenAI |
| `RealtimeSessionBuilder` middleware pipeline | Wire up function invocation, OpenTelemetry, and logging middleware |
| `RealtimeSessionOptions` | Configure voice, speed, modalities, transcription, and VAD settings |
| `RealtimeClientMessage` / `RealtimeServerMessage` | Send audio buffers and text to the model; receive audio, transcripts, and tool-call requests |
| `AIFunction` / function invocation middleware | Register and auto-invoke tools (e.g. `GetWeather`) during a realtime conversation |
| OpenTelemetry & `ILogger` integration | Capture `Activity` spans and metrics emitted by the middleware and display them in-app |

## Features

- **Realtime voice conversation** — press the call button to open a bidirectional audio session with the AI model
- **Audio recording & playback** — record from your microphone and play back locally using NAudio
- **Text chat** — send text messages to the model alongside or instead of voice
- **Tool / function calling** — demonstrates automatic function invocation (weather lookup) through the middleware pipeline
- **Live telemetry** — Activities (traces) and Metrics from `Microsoft.Extensions.AI` are captured and displayed in a dedicated log panel
- **Structured logging** — all middleware logging is routed to an in-app `RichTextBox` via a custom `ILogger` provider with colour-coded log levels
- **Configurable** — select voice, playback speed, and log level at runtime

## How to Run

### Prerequisites

- .NET 10+ (targets `net10.0-windows`)
- Windows OS
- A microphone and speakers / headphones
- An **OpenAI API key** with access to the realtime model

### Private NuGet packages

The AI libraries (`Microsoft.Extensions.AI.Abstractions`, `Microsoft.Extensions.AI`, and `Microsoft.Extensions.AI.OpenAI`) are pre-release builds (`10.3.0-dev`) that are **not** published to nuget.org. They must be built locally from the [dotnet/extensions](https://github.com/dotnet/extensions) repository, and the resulting packages are expected at:

```
C:\oss\extensions\artifacts\packages\Release\Shipping
```

This path is configured as the `extensions` package source in `nuget.config`. If your local build output is in a different location, update the path in `nuget.config` before restoring:

```xml
<add key="extensions" value="<your-local-extensions-packages-path>" />
```

### Set your API key

The app reads the key from .NET User Secrets:

```powershell
dotnet user-secrets set "OpenAIKey" "<your-openai-api-key>"
```

### Build & run

```powershell
dotnet build
dotnet run --project RealtimePlayGround
```

## Usage

1. Select a **voice** and adjust the **speed** slider
2. Click the **call** button to connect to OpenAI Realtime
3. **Record** audio (microphone button) or type a message and click **Send**
4. The AI responds with streaming audio and/or text
5. Watch the **Events** and **Logs** panels for real-time telemetry and diagnostics
6. Click the **hang-up** button to end the session

## Key Dependencies

| Package | Purpose |
|---|---|
| `Microsoft.Extensions.AI.Abstractions` | Core AI abstractions (`IRealtimeClient`, `IRealtimeSession`, etc.) |
| `Microsoft.Extensions.AI` | Middleware pipeline (`RealtimeSessionBuilder`, function invocation, OpenTelemetry, logging) |
| `Microsoft.Extensions.AI.OpenAI` | OpenAI provider implementation (`OpenAIRealtimeClient`) |
| `NAudio` | Audio capture and playback |
| `Microsoft.Extensions.Logging` | Logging infrastructure |
| `Microsoft.Extensions.Configuration.UserSecrets` | Secure API key storage |

## Technical Details

- **Audio format**: 44.1 kHz (or best available), 16-bit, Mono
- **Streaming model**: Uses `System.Threading.Channels` to asynchronously pipe `RealtimeClientMessage` objects to the session and receive `RealtimeServerMessage` responses
- **Middleware pipeline**: `RealtimeSessionBuilder` → `UseFunctionInvocation` → `UseOpenTelemetry` → `UseLogging` → underlying session
