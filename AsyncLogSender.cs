using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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
        // The maximum size in bytes a datachannel message can be. This is usually negotiated via max-message-size, 
        // however there's no easy way to pull this out of the WebRTC C# package. The package requests 2^18, but 
        // that seems to still cause failures so we've lowered it to 2^16, which is the first multiple of 2 that
        // doesn't close the chanel.
        private const int MaxSizeDataChannelMessageBytes = 1 << 16; 
            
        // Before the session connects, this list stores received log messages, and they are sent immediately
        // on connection. This must be thread safe since the log callback is multi-threaded.
        private readonly BlockingCollection<ArraySegment<byte>> queuedDebugLogMessages = new(1024);

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

                ArraySegment<byte> packet = PacketizeLogMessage(nanosSinceLinuxEpoch, type, message, stackTrace);

                // Queue the new packet up for sending on a background thread.
                try
                {
                    if (!queuedDebugLogMessages.TryAdd(packet))
                    {
                        // If this fails, the queue is full. We've queued too many messages without the structured log connecting
                        // and draining them. Just drop the message, since we can't log.
                        ArrayPool<byte>.Shared.Return(packet.Array);
                    }
                }
                catch (InvalidOperationException)
                {
                    // InvalidOperationException is when the queue is complete for addition. This can happen if we have
                    // unfinished ReceiveThreadedLogMessage callbacks running while the session shuts down. Just drop them.
                    ArrayPool<byte>.Shared.Return(packet.Array);
                }
                catch
                {
                    // If an unhandled exception is thrown, that's a bug. Let it bubble up.
                    // Can't be a finally, because we move the packet into the queue in the try {}
                    ArrayPool<byte>.Shared.Return(packet.Array);
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
        private ArraySegment<byte> PacketizeLogMessage(long nanosecondsSinceUnixEpoch, LogType logType, string message,
                                                       string stackTrace)
        {
            // ReSharper disable once ConditionIsAlwaysTrueOrFalse
            if (sizeof(LogType) != sizeof(int))
            {
                throw new Exception($"Log type {logType} has an unsupported size [{sizeof(LogType)}].");
            }
            
            // The packet size varies, but is capped to avoid closing the RTC data channel on a big message or stack.
            // First calculate the size of the metadata in bytes:
            int packetSize =
                sizeof(long) + // timestamp
                sizeof(LogType) + // log type
                sizeof(int) + // message length
                sizeof(int); // stacktrace length
                
            // Fill out the stack trace first, since we assume that's smaller than the message, and when
            // the message is too big, we assume it's more critical to have the stack trace.
            int maxStackSizeChars = (MaxSizeDataChannelMessageBytes - packetSize) / sizeof(char); // floor to nearest number of char
            int stackTraceSizeChars = Math.Min(stackTrace.Length, maxStackSizeChars);
            int stackTraceSizeBytes = stackTraceSizeChars * sizeof(char);
            packetSize += stackTraceSizeBytes;
            
            // Now fill out the message, ensuring we don't exceed the total message size.
            int maxMessageSizeChars = (MaxSizeDataChannelMessageBytes - packetSize) / sizeof(char); // floor to nearest number of char
            int messageSizeChars = Math.Min(message.Length, maxMessageSizeChars);
            int messageSizeBytes = messageSizeChars * sizeof(char);
            packetSize += messageSizeBytes;

            // Rent a buffer from the shared memory pool. This may be larger than the packet and contain junk data.
            var packetBuffer = ArrayPool<byte>.Shared.Rent(packetSize);

            Span<byte> span = packetBuffer.AsSpan(0, packetSize);

            // Write the timestamp.
            int offset = 0;
            MemoryMarshal.Write(span, ref nanosecondsSinceUnixEpoch);
            offset += sizeof(long);

            // Write the log type.
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset), (int) logType);
            offset += sizeof(int);

            // Write the message length.
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset), messageSizeBytes);
            offset += sizeof(int);

            // Write the message string UTF16 bytes.
            MemoryMarshal.AsBytes(message.AsSpan(0, messageSizeChars)).CopyTo(span.Slice(offset));
            offset += messageSizeBytes;

            // Write the stacktrace length.
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset), stackTraceSizeBytes);
            offset += sizeof(int);

            // Write the stacktrace string UTF16 bytes.
            MemoryMarshal.AsBytes(stackTrace.AsSpan(0, stackTraceSizeChars)).CopyTo(span.Slice(offset));

            return new ArraySegment<byte>(packetBuffer, 0, packetSize);
        }

        public void BeginSending(Action<ArraySegment<byte>> sendAction)
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
                        ArrayPool<byte>.Shared.Return(packet.Array);
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
                    ArrayPool<byte>.Shared.Return(packet.Array);
                }
                queuedDebugLogMessages.Dispose();
            }
            // The background thread will now continue and drop the remaining packets, and finish cleanup.
        }
    }
}
