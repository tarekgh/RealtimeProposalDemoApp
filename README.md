# RealtimePlayGround

A C# Windows Forms application for recording and playing back audio.

## Features
- **Start/Stop Recording**: Single button toggles between recording and stopping
- **Play Recording**: Play back the captured audio through your speakers
- **NAudio Integration**: Uses the NAudio library for professional audio handling
- **Visual Feedback**: Status label shows current operation state

## How to Run
1. Build the solution:
   ```powershell
   dotnet build
   ```

2. Run the application:
   ```powershell
   dotnet run --project RealtimePlayGround
   ```

## Requirements
- .NET 6.0 or later
- Windows OS
- Microphone and speakers/headphones

## Usage
1. Click **Start Recording** to begin capturing audio from your microphone
2. Click **Stop Recording** when finished
3. Click **Play Recording** to hear the captured audio
4. Click **Stop Playing** to stop playback

## Technical Details
- Audio Format: 44.1 kHz, Mono
- Recording saved to temporary file
- Uses NAudio 2.1.0 for audio processing
