# Session Streamer Unity Plugin

**Easily stream video and logs from your Unity project using WebRTC.**

The Session Streamer plugin is a playtest tool that streams video from the game's main camera, along with Unity logs and general event data, directly to a remote server for recording and session replay. It functions in both the editor and standalone builds.

Great care has been taken to make sure that the performance impact of the stream is very low. Noticeable performance overhead is considered a bug.

---

## Installation

This package can be installed either by using Unity's instructions for "[Installing a Git Package](https://docs.unity3d.com/6000.1/Documentation/Manual/upm-ui-giturl.html)". Alternatively, you can clone or copy this folder anywhere in your project folder.

Note: This package includes a patched version of the Unity WebRTC package. 

---

## Usage

### Quick Start

```csharp
using Core.Streaming;

// Run this at the start of your game.
SessionStreamer.StartStreamingSession(
    "https://stream.yourserver.com",
    "your_project_id",
    "unique_session_id"
);

```
* `projectId`: Your unique project identifier.
* `sessionId`: A unique identifier for the streaming session. We recommend a time-sorted UUID.

For more configuration, you can set the optional parameters:

```csharp
SessionStreamer.StartStreamingSession(
    "https://stream.yourserver.com",
    "your_project_id",
    "unique_session_id",
    username: "unique_user_id", // Used to group sessions from the same user in the web viewer. We use Environment.UserName.
    disableDebugLogs: false, // Disables sending structured "Debug.Log" logs from Unity.
    disableTextLogs: false, // Disables sending the Player.log or Editor.log file.
    showRecordingIcon: false, // Draws a tiny red dot in the corner of the screen when recording.
    metadata: new() {
        ["some_key"] = "data", // Additional key/value pairs to add to a session's metadata. 
    }
);
```

### Viewing Recorded Sessions

Sessions are recorded for later replay via the server viewer interface:

```
https://stream.yourserver.com/sessions/<projectId>/<sessionId>
```

The session link will also be emitted in the Unity log for easy access.

### Running in the Editor

Session Streamer *can* run in the Editor during Play Mode, in which case the Editor.log will be streamed instead of the Player.log.

### In-Game Log Viewer and Download

The `LogDisplayUI` component provides an in-game UI for viewing and downloading Unity logs:

```csharp
using Core.Streaming;

// Add LogDisplayUI to view and download logs
GameObject logUIObject = new GameObject("LogDisplayUI");
DontDestroyOnLoad(logUIObject);
logUIObject.AddComponent<LogDisplayUI>();
```

**Features:**
- Toggle visibility with the backtick (`` ` ``) key
- View all Unity logs (Log, Warning, Error, Exception) with color coding
- Download logs to a text file with the "Download Logs" button
- Scrollable log viewer with up to 1000 entries
- Logs are saved to `Application.persistentDataPath/ExportedLogs/`

**Usage:**
1. Press `` ` `` to open the log viewer
2. View captured logs in the scrollable window
3. Click "Download Logs" to save all logs to a timestamped file
4. Click "Clear" to clear the current log buffer
5. Click "Close" or press `` ` `` again to hide the viewer

See `LogDisplayUI_Example.cs` for more integration examples.

---

## Performance
Video compression is hardware accelerated on many platforms. This means that captured frames are not read back to the CPU, but instead compressed on the GPU. This is often free because unless you have another stream recording, these hardware units are usually unused. Hardware acceleration is supported by most modern platforms. See [here](https://docs.unity3d.com/Packages/com.unity.webrtc@2.4/manual/videostreaming.html#hardware-acceleration-codecs) for platform compatibility. Additionally, streams are downscaled to 1280x720 to limit network traffic[^1].

Note that Unity's WebRTC package doesn't support hardware acceleration on AMD GPUs yet, but most other platforms are supported. On platforms that don't support hardware encoding, a CPU encoder is used as a fallback. This is runs on a background thread, but is not as fast as GPU encoding.

Logs are streamed as opaque bytes directly from disk, skipping the Unity callback stack. Disk polling and streaming happens on a separate thread. Processing of the text stream (ie. splitting by new lines, converting text encodings) is done on the server during ingestion.

## Debugging
Verbose debugging logs can be enabled with the `SESSION_STREAMING_VERBOSE_LOGGING` C# pre-processor define.

---

## Development and Contributions

Contributions, feature requests, and bug reports are welcome:

* **Submit issues and feature requests** via GitHub Issues.
* **Contribute improvements and bug fixes** through pull requests.

---

## License

This client plugin is available under the MIT License. See `LICENSE` for more details.

[^1]: 1280x720 is also the maximum supported resolution for H264 Hardware encoding on many platforms. 
