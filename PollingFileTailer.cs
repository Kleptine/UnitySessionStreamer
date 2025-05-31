using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Unity.Profiling;
using UnityEngine; // For Task.Delay

public class PollingFileTailer : IDisposable
{
    private CancellationTokenSource cancelSource;

    private static ProfilerMarker PollLog = new ProfilerMarker("Poll Unity Log");
    private string filePath;

    public PollingFileTailer(string filePath, long startPositionBytes, TimeSpan pollingInterval, long maxChunkSize,
                             Action<long, Memory<byte>> onNewChunk)
    {
        this.filePath = filePath;

        cancelSource = new CancellationTokenSource();
        CancellationToken token = cancelSource.Token;

        Task.Run(async () =>
        {
            Debug.Log($"Started polling '{filePath}' every {pollingInterval.TotalMilliseconds}ms.");

            try
            {
                await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                long lastKnownPosition = startPositionBytes;
                long tailedPacketsSent = 0;

                byte[] bufferBytes = new byte[maxChunkSize];
                Memory<byte> buffer = bufferBytes;

                while (!token.IsCancellationRequested)
                {
                    using var _ = PollLog.Auto();

                    if (!File.Exists(filePath))
                    {
                        // If file was deleted after monitoring started, reset position for when it's recreated.
                        Debug.Log($"File '{filePath}' deleted early. Stopping tail.");
                        break;
                    }

                    if (fs.Length < lastKnownPosition)
                    {
                        // File was truncated or replaced with a smaller file.
                        Debug.Log(
                            $"[INFO] File '{filePath}' was truncated or replaced. Last read location was {lastKnownPosition}, now size {fs.Length}");
                        break;
                    }

                    if (fs.Length > lastKnownPosition)
                    {
                        DateTimeOffset currentUtcTimestamp = DateTimeOffset.UtcNow;
                        long ticksSinceUnixEpoch = currentUtcTimestamp.Ticks - DateTimeOffset.UnixEpoch.Ticks;
                        long nanosecondsSinceUnixEpoch = ticksSinceUnixEpoch * 100;

                        // Seek to last known position.
                        fs.Seek(lastKnownPosition, SeekOrigin.Begin);

                        int bytesRead;
                        while ((bytesRead = await fs.ReadAsync(buffer, token)) > 0 && !token.IsCancellationRequested)
                        {
                            try
                            {
                                // Send a slice with the chunk we've read.
                                onNewChunk(nanosecondsSinceUnixEpoch, buffer[..bytesRead]);

                                tailedPacketsSent++;
                                if (tailedPacketsSent % 100 == 0)
                                {
                                    Debug.Log(
                                        $"(PollingFileTailer) Tailed {tailedPacketsSent} packets from {filePath}.");
                                }
                            }
                            catch (Exception e)
                            {
                                Debug.LogException(e);
                            }
                        }

                        lastKnownPosition = fs.Position;
                    }

                    await Task.Delay(pollingInterval, token);
                }
            }
            catch (TaskCanceledException) { }
            catch (Exception e)
            {
                // Log the exception, because Task.Run does not print errors by default unless awaited on.
                Debug.LogException(e);
            }
        });
    }

    public void Stop()
    {
        if (cancelSource == null)
        {
            return;
        }
        Debug.Log($"Tailing stopped for file [{filePath}].");
        cancelSource.Cancel();
        cancelSource.Dispose();
        cancelSource = null;
    }

    public void Dispose()
    {
        Stop();
    }
}
