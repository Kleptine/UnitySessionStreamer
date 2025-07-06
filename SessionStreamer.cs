// Enable this #define to output verbose logs from the WebRTC peer connection.

// #define SESSION_STREAMING_VERBOSE_LOGGING

// This service sends several streams to the server:
//  - Video capture of the main camera (captured using render textures and sent through the hardware encoder)
//  - A tail of the Unity Player.log. This is more detailed than the stream of Debug.Log messages.
//  - General event log from the session stream itself. 
// 
// Timestamps are applied to all data streams, to allow replaying the data stream later and synchronizing it with video.

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using Unity.Profiling;
using Unity.Serialization.Json;
using Unity.WebRTC;
using UnityEditor;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Experimental.Rendering;
using Debug = UnityEngine.Debug;

namespace Core.Streaming
{
    /// <summary>Manages streaming video and log data to the session streamer server.</summary>
    /// <remarks>
    ///     This streamer uses the WHIP webrtc protocol to connect to the streaming service, and then connects a WebRTC
    ///     peer to stream the actual data. This is a monobehavior, because the Unity WebRTC plugin expects to be driven in
    ///     Update().
    /// </remarks>
    public class SessionStreamer : MonoBehaviour
    {
        // Configuration
        private string hostUrl;
        private string sessionId;
        private string projectId;
        private bool streamDebugLogs;
        private List<(string, string)> sessionMetadata;

        // WebRTC state
        private WebCamTexture webCamTexture;
        private MediaStream videoStream;
        private AudioStreamTrack audioStreamTrack;
        private RTCPeerConnection pc;
        private RTCDataChannel generalDataChannel;
        private RTCDataChannel logDataChannel;
        private RTCDataChannel structuredLogDataChannel;

        // Recording video from the screen
        private RenderTexture capturedScreenTexture; // This is the resolution of the screen.
        private RenderTexture sessionScreenTexture; // This is the resolution of the stream.

        // The size of the Unity log on startup. Used in editor to make sure we read from the correct place.
        private long logBytesOnStartup;
        
        // Background threads that send text and structured logs to the server.
        private PollingFileTailer playerLogReader;
        private AsyncLogSender structuredLogSender;
        
        private int statVideoFramesCaptured;

        // Time this client will wait for the Whip+WebRTC connection to connect. It doesn't make sense to wait any longer
        // because the WebRTC server connection will assume failure after ~10 seconds of never connecting.
        private const double ConnectionTimeoutSec = 10.0;

        // Sometimes the Unity package never properly 'finishes' gathering ICE candidates, and instead just waits.
        // But they were properly added, so instead we attempt to connect anyway with the candidates already gathered.
        // Once we hit this timer, any ICE candidates we find will immediately sent to the WHIP.
        private const double GatherTimeoutWithCandidatesFallbackSec = 0.5;

        // If we receive no ICE Candidates within this time we call it, and assume the client machine cannot connect.
        private const double GatherTimeoutNoCandidatesSec = 5.0; // Should really not take more than 5 sec to find candidates.

        /// <summary>Connects to the streaming server, and then begins streaming data to it.</summary>
        /// <param name="streamingServer">The URL of the server to stream to.</param>
        /// <param name="projectId">The project id to file this session under.</param>
        /// <param name="sessionId">A unique identifier for every session. We recommend a time-sorted UUID.</param>
        /// <param name="streamDebugLogs">
        ///     Whether to stream the C# Debug.Log messages Unity receives, and associated stack traces.
        ///     Has a small performance impact because Unity doesn't normally send these to C# unless requested, so it must convert
        ///     the logs and stack traces to UTF16 and call into managed code.
        /// </param>
        /// <param name="sessionMetadata">Any additional metadata to send along with the session. Displayed in the viewer.</param>
        public static void StartStreamingSession(string streamingServer, string projectId, string sessionId,
                                                 bool streamDebugLogs,
                                                 params (string metadataKey, string metadataValue)[] sessionMetadata)
        {
            // Grab the current size of the Unity.log. In the editor, this is useful so that we start streaming logs
            // from the start of the session rather than the start of the file. We want to grab this size as early as possible,
            // because logs will be generated by the below code!
            long logSize = new FileInfo(Application.consoleLogPath).Length;

            Assert.IsTrue(Application.isPlaying, "Cannot start streaming in edit mode. Application must be playing!");
            Assert.IsNull(FindAnyObjectByType<SessionStreamer>(),
                "Session streamer already exists! Previous session did not close properly.");

            GameObject streamGo = new("SessionStreamer");
            DontDestroyOnLoad(streamGo);

            var streamer = streamGo.AddComponent<SessionStreamer>();
            
            // Start receiving log messges. We do this as early as possible.
            streamer.structuredLogSender = new AsyncLogSender();
            
            streamer.hostUrl = streamingServer;
            streamer.sessionId = sessionId;
            streamer.projectId = projectId;
            streamer.sessionMetadata = sessionMetadata.ToList();
            streamer.logBytesOnStartup = logSize;
            streamer.streamDebugLogs = streamDebugLogs;

            // Default metadata.
            streamer.sessionMetadata.Add(("timestamp_utc",
                XmlConvert.ToString(DateTime.Now, XmlDateTimeSerializationMode.Utc)));
            streamer.sessionMetadata.Add(("timezone", TimeZoneInfo.Local.Id));
            streamer.sessionMetadata.Add(("is_editor", Application.isEditor ? "true" : "false"));
            streamer.sessionMetadata.Add(("engine", "unity"));
            streamer.sessionMetadata.Add(("client_version", "1"));
        }

        /// <summary>
        ///     The WebRTC connection must use a coroutine, because the WebRTC APIs are not thread safe and must be run on the
        ///     main thread.
        /// </summary>
        private IEnumerator Start()
        {
            // The WHIP stream url to connect to. Metadata is encoded as query parameters.
            string url = hostUrl + "/whip?session_id=" + sessionId + "&project_id=" + projectId;

            foreach (var (key, value) in sessionMetadata)
            {
                string urlWithParam = url + "&" + key + "=" + value;
                if (!Uri.IsWellFormedUriString(url, UriKind.Absolute))
                {
                    Debug.LogError(
                        $"(SessionStreamer) Metadata key/value is not valid URL string: [{key}={value}]. Skipping. [{url}]");
                    continue;
                }

                url = urlWithParam;
            }

            Debug.Log($"(SessionStreamer) Creating new stream on url [{url}]");

            // Create the Peer Connection. We use Google's STUN servers to detect our public IP address.
            var config = new RTCConfiguration
            {
                iceServers = new[]
                {
                    new RTCIceServer
                    {
                        urls = new[] { "stun:stun.l.google.com:19302", "stun:stun1.l.google.com:19302" }
                    }
                }
            };

            pc = new RTCPeerConnection(ref config);

            // Some default logging.
            DateTime startConnectTime = DateTime.Now;
            List<(TimeSpan, string)> iceCandidates = new();
            pc.OnIceCandidate = candidate =>
            {
                iceCandidates.Add((DateTime.Now - startConnectTime, candidate.Address));
                VerboseLog("WebRTC", $"Found Ice Candidate: [{candidate.Address}]");
            };
            pc.OnIceConnectionChange = state => VerboseLog("WebRTC", $"Ice Connection Changed: {state}");
            pc.OnIceGatheringStateChange = state => VerboseLog("WebRTC", $"Ice Gather state changed: {state}");
            pc.OnTrack = e => VerboseLog("WebRTC", $"OnTrack: {e.Track.Kind} id={e.Track.Id}");
            pc.OnConnectionStateChange = state => VerboseLog("WebRTC", $"PeerConnection State Changed: {state}");

            // Connect our general data channel for sending and receiving logs from the server.
            VerboseLog("SessionStreamer", "Adding data channel: [general]");
            generalDataChannel = pc.CreateDataChannel("general");
            generalDataChannel.OnMessage = HandleServerMessage;

            // Unity Full Log Data Channel: Streams the Unity Player.log file.
            // Timestamps are applied during polling of this file, and are in nanoseconds since the Unix epoch (UTC).
            if (!string.IsNullOrEmpty(Application.consoleLogPath))
            {
                VerboseLog("SessionStreamer", "Adding Unity logs channel: [text_log]");
                logDataChannel = pc.CreateDataChannel("text_log", new RTCDataChannelInit
                {
                    // This is our custom timestamped format so we can associate logs with the time they originated.
                    // Each data packet sent is prepended with the timestamp (nanos since unix epoch)
                    protocol = "timestamped_bytes"
                });
                logDataChannel.OnOpen = StartStreamUnityLogs;
                logDataChannel.OnClose = StopStreamUnityLogs;
            }
            else
            {
                var message = $"Platform [{Application.platform}] does not support Unity.log files. Not sending.";
                SendClientMessage(new ClientMessage
                {
                    message = message,
                    kind = ClientMessageKind.Info
                });
                Debug.LogWarning("(SessionStreamer)" + message);
            }

            // Unity Structured C# Log
            // Streams the Log, Warning, Error, etc objects that come from Unity's callback. 
            // Will not report native errors, but useful for knowing precisely about gameplay errors.
            if (streamDebugLogs)
            {
                structuredLogDataChannel = pc.CreateDataChannel("structured_log", new RTCDataChannelInit
                {
                    // Each message is a byte-packed buffer of the timestamp, log type, stack trace, and message.
                    // See the sending function for more details.
                    protocol = "structured_log_unity_packed"
                });

                structuredLogDataChannel.OnOpen = StartStreamingDebugLogs;
                structuredLogDataChannel.OnClose = StopStreamingDebugLogs;
            }

            // Video Track: Records the main camera video.
            // Timestamps are applied during packetizing to RTP, and will have units specified in number of 'clock ticks'
            // of the video stream's clock rate (usually 90000Hz). This is all handled by WebRTC & RTP.
            foreach (var c in RTCRtpSender.GetCapabilities(TrackKind.Video).codecs)
            {
                VerboseLog(
                    "WebRTC", "Sender Video Encoder Capability: " +
                              $"mimetype={c.mimeType} clockrate={c.clockRate} channels={c.channels} sdpline={c.sdpFmtpLine}");
            }

            Vector2Int capturedScreenSize = GetScreenSize();

            GraphicsFormat format = WebRTC.GetSupportedGraphicsFormat(SystemInfo.graphicsDeviceType);
            capturedScreenTexture = new RenderTexture(capturedScreenSize.x, capturedScreenSize.y, 0, format);
            capturedScreenTexture.Create();

            Vector2Int sessionScreenSize = FitInside1280X720(capturedScreenSize);
            sessionScreenTexture = new RenderTexture(sessionScreenSize.x, sessionScreenSize.y, 0, format);
            sessionScreenTexture.Create();

            Debug.Log(
                $"(SessionStreamer) Adding video track. Captured resolution: [{capturedScreenSize}] => Sent resolution: [{sessionScreenSize}]");

            // Graphics.Blit is necessary to avoid a default vertical flip. I'm not sure why that's the default.
            VideoStreamTrack videoStreamTrack = new(sessionScreenTexture, Graphics.Blit);

            RTCRtpSender videoTrackSender = pc.AddTrack(videoStreamTrack);

            var parameters = videoTrackSender.GetParameters();
            foreach (RTCRtpEncodingParameters encoding in parameters.encodings)
            {
                encoding.maxFramerate = 30;
            }

            foreach (var transceiver in pc.GetTransceivers())
            {
                transceiver.Direction = RTCRtpTransceiverDirection.SendOnly;
            }

            ValidateTransceivers();

            // Begin polling for Ice Candidates.
            var offerOp = pc.CreateOffer();
            while (!offerOp.IsDone)
            {
                yield return null;
            }

            if (offerOp.IsError)
            {
                Debug.LogError("(SessionStreamer) Error creating client offer for WebRTC: " + offerOp.Error);
                yield break;
            }

            // Set up the local connection description so Ice connections can be gathered.
            VerboseLog("WebRTC", "Starting Ice gathering..");
            var localOffer = offerOp.Desc;
            var setDescOp = pc.SetLocalDescription(ref localOffer);

            while (!setDescOp.IsDone)
            {
                yield return null;
            }

            if (setDescOp.IsError)
            {
                Debug.LogError("Error setting local description: " + setDescOp.Error);
                yield break;
            }

            VerboseLog("WebRTC", "Waiting to gather all Ice candidates..");

            DateTime beginTime = DateTime.Now;
            while (pc.GatheringState != RTCIceGatheringState.Complete)
            {
                if (DateTime.Now - beginTime > TimeSpan.FromSeconds(GatherTimeoutWithCandidatesFallbackSec) &&
                    iceCandidates.Count > 1)
                {
                    bool foundNonLocal = false;
                    foreach (var candidate in iceCandidates)
                    {
                        if (!IsLocalIpAddress(candidate.Item2))
                        {
                            foundNonLocal = true;
                        }
                    }

                    if (foundNonLocal)
                    {
                        // Unity sometimes doesn't properly end gathering of candidates. So we fire a WHIP request after
                        // a short wait as long as we have at least one non-local ICE candidate.
                        StringBuilder builder = new();
                        foreach (var candidate in iceCandidates)
                        {
                            builder.Append(candidate.Item1);
                            builder.Append(IsLocalIpAddress(candidate.Item2) ? "(local)" : "(remote)");
                            builder.AppendLine(candidate.Item2);
                        }

                        Debug.LogWarning(
                            "(SessionStreamer) Candidate gathering took too long, but we found a remote interface." +
                            $" Attempting with [{iceCandidates.Count}] candidates:" + builder);
                        break;
                    }
                }

                if (DateTime.Now - beginTime > TimeSpan.FromSeconds(GatherTimeoutNoCandidatesSec))
                {
                    // Quit. We found no ice candidates within a meaty timeout. 
                    // This means that either there's truly no network interface on this device, or more likely,
                    // the candidate gathering failed or was blocked somehow. In any case, there's no way to connect
                    // to the server.
                    Debug.LogWarning(
                        "(SessionStreamer) Could not find a network interface to connect to the server on. Skipping stream.");
                    yield break;
                }

                yield return null;
            }

            // Now that the Ice gather is finished, we can query the local description to pull out the full offer.
            VerboseLog("WebRTC", $"Created offer. Offer:\n{pc.LocalDescription.sdp}");

            // Use Task to call async methods.
            Task<string> task = Task.Run(async () =>
            {
                Uri uri = new UriBuilder(url).Uri;

                var content = new StringContent(pc.LocalDescription.sdp);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/sdp");

                VerboseLog("WebRTC", $"Send POST request to WHIP URL: {uri}");
                var client = new HttpClient();
                var res = await client.PostAsync(uri, content);

                string body = await res.Content.ReadAsStringAsync();

                if (!res.IsSuccessStatusCode)
                {
                    throw new Exception(
                        $"(SessionStreamer) Failed to connect to server. ({res.StatusCode}, \"{body}\"). Url: [{url}]");
                }

                VerboseLog("WebRTC", $"Received successful WHIP response. SDP Answer is:\n{body}");
                return body;
            });

            // Covert async to coroutine yield, wait for task to be completed.
            yield return new WaitUntil(() => task.IsCompleted);

            if (task.Exception != null)
            {
                Debug.LogWarning(
                    $"(SessionStreamer): Couldn't connect to server. Skipping stream. Details:\nurl:[{url}]\n\nException:\n{task.Exception.Message}\n\nInnerExceptions:\n{string.Join("\n\n", task.Exception.InnerExceptions)}");
                yield break;
            }

            string body = task.Result;

            RTCSessionDescription desc = new()
            {
                type = RTCSdpType.Answer,
                sdp = body
            };

            var op = pc.SetRemoteDescription(ref desc);
            while (!op.IsDone)
            {
                yield return null;
            }

            if (op.IsError)
            {
                Debug.LogError("(SessionStreamer) Error setting remote description: " + op.Error);
                yield break;
            }

            VerboseLog("WebRTC", "Finished setting up PeerConnection. Waiting to connect...");

            // Wait for some resolution to the connection attempt.
            beginTime = DateTime.Now;
            while (pc.ConnectionState == RTCPeerConnectionState.Connecting)
            {
                if (DateTime.Now - beginTime > TimeSpan.FromSeconds(ConnectionTimeoutSec))
                {
                    // Either this is a bug, or the server went down in the middle of the connection.
                    Debug.LogWarning("(SessionStreamer) Timed out waiting for WebRTC to connect. Skipping session.");
                    yield break;
                }

                yield return null;
            }

            if (pc.ConnectionState != RTCPeerConnectionState.Connected)
            {
                Debug.LogError(
                    $"(SessionStreamer) Failed to make WebRTC connection to server. Connection state: {pc.ConnectionState}");
                yield break;
            }

            VerboseLog("WebRTC", "WebRTC Connected!");

            string sessionViewingUrl = hostUrl + $"/sessions/{projectId}/{sessionId}";

            Debug.Log(Application.isEditor
                ? $"(SessionStreamer) This session can be viewed at <a href=\"{sessionViewingUrl}\">{sessionViewingUrl}</a>"
                : $"(SessionStreamer) This session can be viewed at [{sessionViewingUrl}]");

            ValidateTransceivers();

            // Once we are connected, capturing and pumping frames will be kicked off by the Start() coroutine.
            StartCoroutine(WebRTC.Update());
            StartCoroutine(RecordScreenFrames());
            StartCoroutine(CheckStats());
        }

        private void StartStreamingDebugLogs()
        {
            // Send the logs we queued up before connecting to the structured logs.
            structuredLogSender.BeginSending(packet =>
            {
                // Note: This lambda is called from a background thread!
                SendPacket(structuredLogDataChannel, packet.AsMemory());
            });
        }

        private void StopStreamingDebugLogs()
        {
            structuredLogSender?.Dispose();
            structuredLogSender = null;
        }


        private void ValidateTransceivers()
        {
            VerboseLog("WebRTC", $"==== Validating Transceivers ({pc.GetTransceivers().Count()}) ====");
            int i = 0;
            foreach (var t in pc.GetTransceivers())
            {
                VerboseLog("WebRTC",
                    $"Transceiver {i}: Mid={t.Mid}, Direction={t.Direction}, CurrentDirection={t.CurrentDirection}");

                // Log Sender Information
                var sender = t.Sender;
                if (sender != null)
                {
                    VerboseLog("WebRTC",
                        $"  Sender: TrackId={sender.Track?.Id}, TrackKind={sender.Track?.Kind}, TrackEnabled={sender.Track?.Enabled}, TrackReadyState={sender.Track?.ReadyState}");
                    var senderParams = sender.GetParameters();
                    if (senderParams != null)
                    {
                        VerboseLog("WebRTC", $"    Sender Params: TransactionId={senderParams.transactionId}");
                        foreach (var codec in senderParams.codecs)
                        {
                            VerboseLog("WebRTC",
                                $"      Sender Codec: MimeType={codec.mimeType}, ClockRate={codec.clockRate}, Channels={codec.channels}, SdpFmtpLine={codec.sdpFmtpLine}, PayloadType={codec.payloadType}");
                        }
                        foreach (var encoding in senderParams.encodings)
                        {
                            VerboseLog("WebRTC",
                                $"      Sender Encoding: Active={encoding.active}, MaxBitrate={encoding.maxBitrate}, MaxFramerate={encoding.maxFramerate}, ScaleResolutionDownBy={encoding.scaleResolutionDownBy}, MinBitrate={encoding.minBitrate}, Rid={encoding.rid}");
                        }
                        if (senderParams.headerExtensions != null)
                        {
                            foreach (var headerExt in senderParams.headerExtensions)
                            {
                                VerboseLog("WebRTC",
                                    $"      Sender RTP Header Extension: URI={headerExt.uri}, Id={headerExt.id}, Encrypted={headerExt.encrypted}");
                            }
                        }
                    }
                }
                else
                {
                    VerboseLog("WebRTC", "  Sender: null");
                }

                i++;
            }
            VerboseLog("WebRTC", "==== Finished Validating Transceivers ====");
        }

        private void SendPacket(RTCDataChannel channel, Memory<byte> packet)
        {
            // Send the data. Requires unsafe to grab the pointer.
            // UNSAFE: The packet length can never be longer than the memory region, so we can't read uninitialized
            // memory. We clean up the pinned memory with 'using'.
            using var handle = packet.Pin();
            unsafe
            {
                channel.Send(handle.Pointer, packet.Length);
            }
        }

        public void SendClientMessage(ClientMessage message)
        {
            if (generalDataChannel == null)
            {
                VerboseLog("SessionStreamer", "Couldn't send server message. Data channel not created.");
                return;
            }

            if (generalDataChannel.ReadyState != RTCDataChannelState.Open)
            {
                VerboseLog("SessionStreamer",
                    $"Couldn't send server message. Data channel is not open. [{generalDataChannel.ReadyState}]");
                return;
            }

            generalDataChannel.Send(JsonSerialization.ToJson(message, new JsonSerializationParameters
            {
                UserDefinedAdapters = new List<IJsonAdapter> { new ClientMessageKindAdapter() }
            }));
        }

        private void StartStreamUnityLogs()
        {
            string consoleLogPath = Application.consoleLogPath;

            // Generally, 16Kb is the maximum packet size in WebRTC
            // We do 15Kb to allow some room for our header bytes, just for safety.
            const int maxChunkSize = 1024 * 15;

            // If we're running in the editor seek to 1000 bytes before the end of the log so we don't send the
            // entire editor log.
            long startPosition = Application.isEditor ? Math.Max(logBytesOnStartup - 16000, 0) : 0;

            VerboseLog("SessionStreamer",
                $"Streaming Unity log to server. [{consoleLogPath}] Position: {startPosition}");

            playerLogReader = new PollingFileTailer(consoleLogPath, startPosition, TimeSpan.FromMilliseconds(16),
                maxChunkSize, (timestamp, chunk) =>
                {
                    if (logDataChannel.ReadyState != RTCDataChannelState.Open)
                    {
                        Debug.LogError(
                            $"(SessionStreamer) Data channel is not open, cannot send logs. [{logDataChannel.ReadyState}]");
                        playerLogReader.Dispose();
                        return;
                    }

                    // One additional long for the timestamp.
                    int packetSize = chunk.Length + sizeof(long);

                    // Get a temporary buffer.
                    using var bufferOwned = MemoryPool<byte>.Shared.Rent(packetSize);
                    Memory<byte> buffer = bufferOwned.Memory.Slice(0, packetSize);

                    // Set timestamp as first ~8 bytes.
                    MemoryMarshal.Write(buffer.Span, ref timestamp);

                    // Set packet body as chunk of data.
                    // Length of the overall packet is handled by RTP.
                    chunk.CopyTo(buffer.Slice(sizeof(long), chunk.Length));

                    SendPacket(logDataChannel, buffer);
                });
        }

        private void StopStreamUnityLogs()
        {
            Debug.Log("(SessionStreamer) Stop tailing Unity log file.");
            playerLogReader.Stop();
        }

        // private void OnAudioFilterRead(float[] data, int channels)
        // {
        //     if (handleOnAudioFilterRead != null)
        //     {
        //         handleOnAudioFilterRead(data, channels);
        //     }
        // }

        private static Vector2Int GetScreenSize()
        {
            // Screen.width/height returns size of the active window.
            // However, it is mandatory to get size of the game view when player mode.
            // UnityStats is used here because it returns the size of game view anytime.
#if UNITY_EDITOR
            string[] screenres = UnityStats.screenRes.Split('x');
            int screenWidth = int.Parse(screenres[0]);
            int screenHeight = int.Parse(screenres[1]);

            // Set Screen.width/height forcely because UnityStats returns zero when batch mode.
            if (screenWidth == 0 || screenHeight == 0)
            {
                screenWidth = Screen.width;
                screenHeight = Screen.height;
            }
#else
                int screenWidth = Screen.width;
                int screenHeight = Screen.height;
#endif

            return new(screenWidth, screenHeight);
        }

        // Returns the largest screen size that fits within 1280x720. This corresponds to level 3.1 in H264.
        private static Vector2Int FitInside1280X720(Vector2Int screenDimensions)
        {
            const int maxWidth = 1280;
            const int maxHeight = 720;

            float aspectRatio = (float)screenDimensions.x / screenDimensions.y;
            int width = screenDimensions.x;
            int height = screenDimensions.y;

            if (width > maxWidth)
            {
                width = maxWidth;
                height = (int)(width / aspectRatio);
            }

            if (height > maxHeight)
            {
                height = maxHeight;
                width = (int)(height * aspectRatio);
            }

            return new Vector2Int(width, height);
        }

        private IEnumerator CheckStats()
        {
            yield return new WaitForEndOfFrame();

            var waitOp = new WaitForSeconds(1);

            while (true)
            {
                yield return waitOp;

                foreach (var sender in pc.GetSenders())
                {
                    var op = sender.GetStats();
                    yield return op;

                    int senderIdx = 0;
                    foreach (var stat in op.Value.Stats.Values)
                    {
                        if (stat is RTCOutboundRTPStreamStats outboundStat)
                        {
                            VerboseLog($"SessionStreamer video track {senderIdx}",
                                $"Frames captured: {statVideoFramesCaptured} " +
                                $"=> Frames encoded: {outboundStat.framesEncoded} " +
                                $"=> bytes sent: {outboundStat.bytesSent}");
                            if (outboundStat.framesEncoded > 250 || statVideoFramesCaptured > 250)
                            {
                                if (outboundStat.bytesSent == 0)
                                {
                                    Debug.LogError(
                                        "(SessionStreamer) Video stream is not sending to server. Check configuration.");
                                    ValidateTransceivers();
                                    yield break;
                                }
                                VerboseLog("SessionStreamer", "Data is sending. All good.");
                                yield break;
                            }
                        }

                        senderIdx++;
                    }
                }
            }
        }

        // ReSharper disable once IteratorNeverReturns
        private IEnumerator RecordScreenFrames()
        {
            Debug.Log("(SessionStreamer) Start recording screen captures to stream to server.");
            WaitForEndOfFrame waitOp = new WaitForEndOfFrame();
            while (true)
            {
                yield return waitOp;
                ScreenCapture.CaptureScreenshotIntoRenderTexture(capturedScreenTexture);
                Graphics.Blit(capturedScreenTexture, sessionScreenTexture);
                statVideoFramesCaptured++;
            }
        }

        // Parse and log any messages sent to us from the server.
        private static void HandleServerMessage(byte[] msg)
        {
            try
            {
                string text = Encoding.UTF8.GetString(msg);
                ServerMessage message = JsonSerialization.FromJson<ServerMessage>(text);

                switch (message.kind)
                {
                    case ServerMessageKind.Unparsed:
                        Debug.LogWarning("(SessionStreamer) Failed to parse server message: " + text);
                        break;
                    case ServerMessageKind.Error:
                        Debug.LogError("(SessionStreamer) Server error: " + message.message);
                        break;
                    case ServerMessageKind.Notice:
                        Debug.Log("(SessionStreamer) Server notice: " + message.message);
                        break;
                    case ServerMessageKind.Info:
                        VerboseLog("SessionStreamer", "Server message: " + message.message);
                        break;
                    case ServerMessageKind.SessionComplete:
                        Debug.Log("(SessionStreamer) Session ended.");
                        break;
                    default:
                        VerboseLog("SessionStreamer", "Server notice: " + message.kind);
                        break;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("(SessionStreamer) Failed to parse server message. " + e.Message);
            }
        }

        private enum ServerMessageKind
        {
            Unparsed, // Only set if the json message didn't contain this enum.
            Notice, // Important information that should be logged back to the user.
            Info, // General information on connection status, etc.
            Error, // An error originating on the server. May or may not be the client's fault.
            SessionComplete // The session completed after requesting a graceful shutdown.
        }

        private struct ServerMessage
        {
            // Names here match the json object sent from the server.
            // ReSharper disable InconsistentNaming
            public ServerMessageKind kind;

            public string message;
            // ReSharper restore InconsistentNaming
        }

        public enum ClientMessageKind
        {
            Info, // An INFO level message with a string message.
            SessionEnding // No message, just informs the server that we're closing the session.
        }

        public class ClientMessageKindAdapter : IJsonAdapter<ClientMessageKind>
        {
            public void Serialize(in JsonSerializationContext<ClientMessageKind> context, ClientMessageKind value)
            {
                context.Writer.WriteValue(value.ToString());
            }

            public ClientMessageKind Deserialize(in JsonDeserializationContext<ClientMessageKind> context)
            {
                // The default deserializer works fine for this type.
                throw new NotImplementedException("Serialization only");
            }
        }

        // This struct matches the JSON encoding the server uses for the client message.
        [SuppressMessage("ReSharper", "InconsistentNaming")]
        public struct ClientMessage
        {
            public ClientMessageKind kind;
            public string message;
        }

        [Conditional("SESSION_STREAMING_VERBOSE_LOGGING")]
        private static void VerboseLog(string header, string text)
        {
            Debug.Log($"({header}) {text}");
        }

        private void OnDestroy()
        {
            Debug.Log($"(SessionStreamer) Shutting down game stream. Captured {statVideoFramesCaptured} frames.");

            playerLogReader?.Dispose();
            playerLogReader = null;

            SendClientMessage(new ClientMessage
            {
                kind = ClientMessageKind.SessionEnding
            });

            videoStream?.Dispose();
            videoStream = null;

            // Drain and dispose the logs we've stored.
            structuredLogSender?.Dispose();
            structuredLogSender = null;

            // handleOnAudioFilterRead = null;

            audioStreamTrack?.Dispose();
            audioStreamTrack = null;

            pc?.Close();
            pc?.Dispose();
            pc = null;

            webCamTexture?.Stop();
            webCamTexture = null;

            if (capturedScreenTexture)
            {
                capturedScreenTexture.Release();
            }

            if (sessionScreenTexture)
            {
                sessionScreenTexture.Release();
            }

            VerboseLog("WebRTC", "Dispose ok");
        }

        public static bool IsLocalIpAddress(string ipAddressString)
        {
            if (ipAddressString.StartsWith("192.168"))
            {
                return true;
            }

            if (IPAddress.TryParse(ipAddressString, out IPAddress addr))
            {
                if (IPAddress.IsLoopback(addr))
                {
                    return true; // handles 127/8 and ::1
                }

                if (addr.AddressFamily == AddressFamily.InterNetwork) // IPv4
                {
                    var b = addr.GetAddressBytes();
                    return b[0] switch
                    {
                        10 => true, // 10/8
                        172 when b[1] >= 16 && b[1] <= 31 => true, // 172.16/12
                        192 when b[1] == 168 => true, // 192.168/16
                        169 when b[1] == 254 => true, // 169.254/16
                        _ => false
                    };
                }
                if (addr.AddressFamily == AddressFamily.InterNetworkV6) // IPv6
                {
                    if (addr.IsIPv6LinkLocal)
                    {
                        return true;
                    }
                    var b = addr.GetAddressBytes();
                    return (b[0] & 0xFE) == 0xFC; // fc00::/7 (fc or fd)
                }
                return false;
            }
            return false; // Assume public if not matched or parse failed
        }
    }
}
