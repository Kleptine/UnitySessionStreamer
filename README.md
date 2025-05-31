# Session Streamer Unity Plugin

**Easily stream video and logs from your Unity project using WebRTC.**

The Session Streamer plugin for Unity provides an way to capture and stream video from your game's main camera, along with Unity logs and general event data, directly to a remote server for recording and session replay.

---

## Features

* **Video Capture**: Streams real-time video captured from your main camera using WebRTC with hardware encoding for optimal performance.
* **Unity Logs**: Captures and streams the Unity `Player.log` file, providing detailed debugging and session insights.
* **High Performance**: Great care has been taken to make sure that the performance impact of the stream is near-zero. 

---

## Installation

1. **Clone the repository** into your Unity project's `Assets/Plugins` directory:

   ```bash
   git clone https://github.com/yourusername/session-streamer-unity.git Assets/Plugins/SessionStreamer
   ```

2. **Install dependencies**:

   * Ensure the official [Unity WebRTC package](https://docs.unity3d.com/Packages/com.unity.webrtc@2.4/manual/index.html) is installed from the Unity Package Manager.

---

## Usage

### Quick Start

```csharp
using Core.Streaming;

// Start a streaming session.
SessionStreamer.StartStreamingSession(
    hostUrl: "https://stream.yourserver.com",
    projectId: "your_project_id",
    sessionId: "unique_session_id",
    ("username", "player123"),
    ("session_type", "beta_test")
);
```

* \`\`: Your unique project identifier. 
* \`\`: A unique identifier for each streaming session (we recommend a time-sorted UUID).
* **Metadata**: Optional key-value pairs that will be recorded and displayed in the viewer.

### Viewing Recorded Sessions

Sessions are recorded for later replay via the server viewer interface:

```
https://stream.yourserver.com/sessions/<projectId>/<sessionId>
```

The session link will also be emitted in the Unity log for easy access.

---

## Performance
Video compression is hardware accelerated. This means that captured frames are not read back to the CPU, but instead compressed on the GPU. This is often free because unless you have another stream recording, these hardware units are usually unused. Hardware acceleration is supported by most modern platforms. See [here](https://docs.unity3d.com/Packages/com.unity.webrtc@2.4/manual/videostreaming.html#hardware-acceleration-codecs) for platform compatibility. Additionally, streams are downscaled to 1280x720 to limit network traffic[^1].

Logs are streamed as opaque bytes directly from disk, skipping the Unity callback stack. Disk polling and streaming happens on a separate thread. Processing of the text stream (ie. splitting by new lines, converting text encodings) is done on the server during ingestion.

Very little should happen during Update(), however memory allocations during Update are considered a bug and should be reported. 

---

## Development and Contributions

Contributions, feature requests, and bug reports are welcome:

* **Submit issues and feature requests** via GitHub Issues.
* **Contribute improvements and bug fixes** through pull requests.

---

## License

This client plugin is available under the MIT License. See `LICENSE` for more details.

[^1]: 1280x720 is also the maximum supported resolution for H264 Hardware encoding on many platforms. 
