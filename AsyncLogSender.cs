using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Unity.Profiling;
using UnityEngine;

namespace Core.Streaming
{
    /// <summary>
    ///     Captures logs from Unity's log callback and packs them into a byte format to be sent to the server over a
    ///     webrtc data channel. Aims to be blazing-fast within the log handler callback, with no allocation, and minimal
    ///     copying.
    /// </summary>
    public class AsyncLogSender : IDisposable
    {
        // Before the session connects, this list stores received log messages, and they are sent immediately
        // on connection. This must be thread safe since the log callback is multi-threaded.
        private readonly BlockingCollection<byte[]> queuedDebugLogMessages = new(1024);

        private bool started;

        public AsyncLogSender()
        {
            Application.logMessageReceivedThreaded += ReceiveThreadedLogMessage;
        }

        private static ProfilerMarker streamUnityLogMessage = new("SessionStreamer.StreamUnityLogMessage");

        private void ReceiveThreadedLogMessage(string message, string stackTrace, LogType type)
        {
            using (streamUnityLogMessage.Auto())
            {
                if (stackTrace == null || message == null)
                {
                    // This should never happen. If stacktrace is not present, an empty string is passed to this function.
                    // However, we avoid logging an assert here, otherwise we could spiral into an infinite log loop.
                    return;
                }

                DateTimeOffset currentUtcTimestamp = DateTimeOffset.UtcNow;
                long ticksSinceUnixEpoch = currentUtcTimestamp.Ticks - DateTimeOffset.UnixEpoch.Ticks;
                long nanosSinceLinuxEpoch = ticksSinceUnixEpoch * 100;

                byte[] packet = PacketizeLogMessage(nanosSinceLinuxEpoch, type, message, stackTrace);

                // Queue the new packet up for sending on a background thread.
                try
                {
                    if (!queuedDebugLogMessages.TryAdd(packet))
                    {
                        // If this fails, the queue is full. We've queued too many messages without the structured log connecting
                        // and draining them. Just drop the message, since we can't log.
                        ArrayPool<byte>.Shared.Return(packet);
                    }
                }
                catch (InvalidOperationException)
                {
                    // InvalidOperationException is when the queue is complete for addition. This can happen if we have
                    // unfinished ReceiveThreadedLogMessage callbacks running while the session shuts down. Just drop them.
                    ArrayPool<byte>.Shared.Return(packet);
                }
                catch
                {
                    // If an unhandled exception is thrown, that's a bug. Let it bubble up.
                    // Can't be a finally, because we move the packet into the queue in the try {}
                    ArrayPool<byte>.Shared.Return(packet);
                    throw;
                }
            }
        }

        // Writes a log message received from Unity's managed logging callback to a byte buffer in the most performant
        // way possible. The packed format is:
        //  - nanosecondsSinceUnixEpoch (i64) (8 bytes) 
        //  - logType (4 bytes) (u32) (LogType)
        //  - message length in bytes (i32) (4 bytes)
        //  - message UTF16 bytes (N bytes)
        //  - stacktrace length in bytes (i32) (4 byte)
        //  - stacktrace UTF16 bytes (M bytes)
        private byte[] PacketizeLogMessage(long nanosecondsSinceUnixEpoch, LogType logType, string message,
                                                       string stackTrace)
        {
            // Calculate the exact required size
            int packetSize =
                sizeof(long) +
                Unsafe.SizeOf<LogType>() +
                sizeof(int) +
                message.Length * sizeof(char) +
                sizeof(int) +
                stackTrace.Length * sizeof(char);

            // Rent a buffer from the shared memory pool. This may be larger than the packet and contain junk data.
            var packetBuffer = ArrayPool<byte>.Shared.Rent(packetSize);

            Span<byte> span = packetBuffer.AsSpan(0, packetSize);

            // Write the timestamp.
            int offset = 0;
            MemoryMarshal.Write(span, ref nanosecondsSinceUnixEpoch);
            offset += sizeof(long);

            // Write the log type.
            Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(span.Slice(offset)), logType);
            offset += Unsafe.SizeOf<LogType>();

            // Write the message length.
            Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(span.Slice(offset)),
                message.Length * sizeof(char));
            offset += Unsafe.SizeOf<int>();

            // Write the message string UTF16 bytes.
            MemoryMarshal.AsBytes(message.AsSpan()).CopyTo(span.Slice(offset));
            offset += message.Length * sizeof(char);

            // Write the stacktrace length.
            Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(span.Slice(offset)),
                stackTrace.Length * sizeof(char));
            offset += Unsafe.SizeOf<int>();

            // Write the stacktrace string UTF16 bytes.
            MemoryMarshal.AsBytes(stackTrace.AsSpan()).CopyTo(span.Slice(offset));

            return packetBuffer;
        }

        public void BeginSending(Action<byte[]> sendAction)
        {
            if (queuedDebugLogMessages.IsAddingCompleted)
            {
                Debug.LogError("Cannot begin sending. The queue is already marked as complete for adding." +
                               " Has BeginSending been called twice, or has Dispose been called already?");
                return;
            }

            started = true;

            Thread senderThread = new(() =>
            {
                if (queuedDebugLogMessages.Count == queuedDebugLogMessages.BoundedCapacity)
                {
                    Debug.LogWarning(
                        "(SessionStreamer) Too many log messages arrived while the session was connecting (). Some logs may have been dropped");
                }

                try
                {
                    foreach (var packet in queuedDebugLogMessages.GetConsumingEnumerable())
                    {
                        // If the queue is marked complete for adding, the session has shut down and there's no
                        // longer anywhere valid to send the packets. So we drop them.
                        if (!queuedDebugLogMessages.IsAddingCompleted)
                        {
                            sendAction(packet);
                        }
                        ArrayPool<byte>.Shared.Return(packet);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }

                Debug.Log("(SessionStreamer) Finished sending structured logs.");
                if (queuedDebugLogMessages.Count > 0)
                {
                    Debug.LogError(
                        "(SessionStreamer) Did not properly cleanup packet queue when shutting down async logger. " +
                        $"[{queuedDebugLogMessages.Count}] packets remain.");
                }
                queuedDebugLogMessages.Dispose();
            });

            senderThread.Name = "SessionStreamer.AsyncLogSender";
            senderThread.Start();
        }

        // Todo: We could add a 'blocking drain' functionality in the SessionStreamer.Destroy that
        // made sure to send out all the remaining packets before shutdown, if we find this to be important missing
        // context.
        /// <summary>Stops sending structured logs to the server. Existing packets in the queue will be dropped.</summary>
        public void Dispose()
        {
            Application.logMessageReceivedThreaded -= ReceiveThreadedLogMessage;

            queuedDebugLogMessages.CompleteAdding();

            if (!started)
            {
                // Streaming never began. Just drain the messages here and dispose.
                foreach (var packet in queuedDebugLogMessages.GetConsumingEnumerable())
                {
                    ArrayPool<byte>.Shared.Return(packet);
                }
                queuedDebugLogMessages.Dispose();
            }
            // The background thread will now continue and drop the remaining packets, and finish cleanup.
        }
    }
}
