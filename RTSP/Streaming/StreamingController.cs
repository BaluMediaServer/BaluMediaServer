using System.Threading.Channels;
using Android.Util;
using BaluMediaServer.Models;
using BaluMediaServer.RTSP.ClientManagement;
using BaluMediaServer.RTSP.Transport;
using BaluMediaServer.Services;

namespace BaluMediaServer.RTSP.Streaming;

/// <summary>
/// Controls video streaming to clients, handling both H.264 and MJPEG codecs.
/// </summary>
public class StreamingController : IStreamingController
{
    private readonly IH264EncoderManager _encoderManager;
    private readonly JpegEncoderService _jpegEncoder;
    private readonly IRtpPacketBuilder _rtpBuilder;
    private readonly ITransportManager _transportManager;
    private readonly IClientManager _clientManager;

    // 0 = not streaming, 1 = streaming.
    // Use Interlocked.CompareExchange (not volatile bool) so the 0→1 transition is atomic:
    // a plain volatile read + separate write allows two simultaneous clients to both read 0
    // before either writes 1, causing double CameraStartRequested / StreamingStateChanged(true).
    private volatile int _isStreamingFlag;

    /// <summary>
    /// Fallback interval in milliseconds used when H.264 frame delivery needs a brief wait.
    /// Primary frame delivery is event-driven via async channel reads (DequeueFrameAsync).
    /// </summary>
    private const int H264PollIntervalMs = 10;

    /// <summary>
    /// Target frame interval in milliseconds for MJPEG streaming (~25 fps).
    /// </summary>
    private const int MjpegFrameIntervalMs = 40;

    /// <summary>
    /// Inactivity timeout in seconds before disconnecting an idle client.
    /// Increased from 10 to 60 seconds to prevent premature disconnections on slow or congested networks.
    /// Activity is tracked during the streaming loop execution, so this timeout only triggers if the loop stalls.
    /// </summary>
    private const int InactivityTimeoutSeconds = 60;

    /// <inheritdoc/>
    public bool IsStreaming => _isStreamingFlag == 1;

    /// <inheritdoc/>
    public event EventHandler<bool>? StreamingStateChanged;

    /// <summary>
    /// Event to request camera start.
    /// </summary>
    public event EventHandler<int>? CameraStartRequested;

    /// <summary>
    /// Event raised when the encoder falls back to a different resolution than requested.
    /// Args: (cameraId, actualWidth, actualHeight).
    /// </summary>
    public event EventHandler<(int cameraId, int width, int height)>? EncoderResolutionFallback;

    /// <summary>
    /// Delegate to retrieve the latest raw frame for a given camera ID.
    /// </summary>
    public Func<int, FrameEventArgs?>? GetLatestFrame { get; set; }

    /// <summary>
    /// Delegate to restart the camera and clear the cached latest frame for a given camera ID.
    /// Called by the stall-recovery path so that <see cref="WaitForFrameAndStartEncoder"/>
    /// blocks on a fresh frame rather than returning immediately from a stale cache.
    /// </summary>
    public Action<int>? RestartCamera { get; set; }

    /// <summary>
    /// Creates a new StreamingController.
    /// </summary>
    public StreamingController(
        IH264EncoderManager encoderManager,
        JpegEncoderService jpegEncoder,
        IRtpPacketBuilder rtpBuilder,
        ITransportManager transportManager,
        IClientManager clientManager)
    {
        _encoderManager = encoderManager;
        _jpegEncoder = jpegEncoder;
        _rtpBuilder = rtpBuilder;
        _transportManager = transportManager;
        _clientManager = clientManager;
    }

    /// <summary>
    /// Externally resets the streaming state (e.g. called by Server when all clients disconnect).
    /// Uses <see cref="Interlocked.Exchange"/> so the write is immediately visible to all threads.
    /// <para>
    /// After this call returns 0, the next <see cref="StreamToClientAsync"/> invocation will be
    /// able to win the <c>CompareExchange(0 → 1)</c> gate and restart the camera.
    /// </para>
    /// </summary>
    public void SetStreamingState(bool streaming)
    {
        Interlocked.Exchange(ref _isStreamingFlag, streaming ? 1 : 0);
        StreamingStateChanged?.Invoke(this, streaming);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// This method manages the streaming loop for a connected client, including:
    /// <list type="bullet">
    /// <item>Atomic camera start — <c>Interlocked.CompareExchange</c> on <c>_isStreamingFlag</c>
    /// ensures <see cref="CameraStartRequested"/> and <see cref="StreamingStateChanged"/> are
    /// each fired exactly once even when multiple clients connect simultaneously.</item>
    /// <item>H.264 encoder initialization via <c>WaitForFrameAndStartEncoder</c>.</item>
    /// <item>Consecutive error detection (10 errors threshold).</item>
    /// <item>Stall detection and automatic camera+encoder restart (5 s after first frame, 3 s warmup).</item>
    /// </list>
    /// </remarks>
    public async Task StreamToClientAsync(Models.Client client, CancellationToken cancellationToken)
    {
        BaluLogger.Debug("[StreamingController]", $"Starting stream to client {client.Id} using {client.Transport}");

        // Register per-client channel before starting the encoder so no frames are missed.
        // The fan-out in H264EncoderManager writes every encoded frame into this client's channel.
        if (client.Codec == CodecType.H264)
        {
            _encoderManager.RegisterClientChannel(client.CameraId, client.Id);
        }

        // Cache the channel reference once — avoids a per-frame dictionary lookup (25×/s per client).
        // The channel is registered above and lives for the entire session, so this reference is stable.
        Channel<H264FrameEventArgs>? h264Channel = client.Codec == CodecType.H264
            ? _encoderManager.GetClientChannelRef(client.CameraId, client.Id)
            : null;

        // Atomic test-and-set: CAS returns the *old* value.
        // The thread that transitions 0→1 wins and is the sole thread that starts the camera.
        // Any thread that arrives while streaming is already active (old value == 1) skips to else-if.
        if (Interlocked.CompareExchange(ref _isStreamingFlag, 1, 0) == 0)
        {
            // Request camera start — exactly once, even under simultaneous connects
            try
            {
                CameraStartRequested?.Invoke(this, client.CameraId);
            }
            catch (Exception ex)
            {
                BaluLogger.Error("[StreamingController]", $"CameraStartRequested subscriber error: {ex.Message}");
            }

            try
            {
                StreamingStateChanged?.Invoke(this, true);
            }
            catch (Exception ex)
            {
                BaluLogger.Error("[StreamingController]", $"StreamingStateChanged subscriber error: {ex.Message}");
            }

            if (client.Codec == CodecType.H264)
            {
                // Wait for first frame and start encoder
                await WaitForFrameAndStartEncoder(client, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (client.Codec == CodecType.H264 && !_encoderManager.IsEncoderRunning(client.CameraId))
        {
            // Encoder stalled since first client — restart it
            BaluLogger.Info("[StreamingController]", $"Encoder stalled for camera {client.CameraId} — restarting");
            await WaitForFrameAndStartEncoder(client, cancellationToken).ConfigureAwait(false);
        }

        // Request an IDR keyframe so this client's decoder can start immediately.
        // Without this, a new/reconnecting client waits up to 1 full second (the I-frame interval)
        // before seeing a keyframe. With this, the encoder emits one within ~40ms (one encode cycle).
        if (client.Codec == CodecType.H264 && _encoderManager.IsEncoderRunning(client.CameraId))
        {
            _encoderManager.RequestKeyFrame(client.CameraId);
        }

        // RTP seq/rtptime are initialized in HandlePlayAsync to match the PLAY response RTP-Info header

        const int frameIntervalMs = 22; // ~45fps
        int consecutiveTimeouts = 0;
        bool gotFirstFrame = false;
        // Before any frame arrives: allow 15 × 200ms = 3s for MediaCodec hardware warmup.
        // After the first frame flows: allow 25 × 200ms = 5s before declaring a stall.
        // 5s gives slow/padded cameras (e.g. VGA on MediaTek delivering ~0.3fps) time to
        // produce the next frame without triggering a premature encoder restart loop.
        const int warmupTimeoutCount = 15;
        const int activeTimeoutCount = 25;
        // How many times to attempt camera+encoder restart before giving up and disconnecting
        // the client (which signals the outer watchdog to do a full server restart).
        // Each attempt includes a ~10s WaitForFrameAndStartEncoder timeout, so 3 attempts
        // gives ~30s of transparent recovery before the client is killed.
        const int MaxStallRecoveryAttempts = 3;
        int stallRecoveryAttempts = 0;

        // Pre-allocate per-session state to avoid per-frame allocations in the hot path.
        // nalSendBuffer is cleared and reused on every frame; the list header is allocated once.
        var nalSendBuffer = new List<byte[]>(8);
        var rtpBatchBuffer = new List<byte[]>(32);

        try
        {
            BaluLogger.Info("[StreamingController]", $"Entering streaming loop for client {client.Id} (codec={client.Codec}, transport={client.Transport}, isPlaying={client.IsPlaying})");

            while (client.IsPlaying && !cancellationToken.IsCancellationRequested)
            {
                // Check for too many consecutive errors (fast, no blocking)
                // Socket disconnection is detected reliably via send errors in TransportManager.
                // We do NOT use IsSocketConnected(Poll+Available) here because the HandleClient
                // reader loop competes on the same RTSP socket, causing a TOCTOU race that
                // falsely detects disconnection and breaks the streaming loop.
                if (client.ConsecutiveSendErrors >= 10)
                {
                    BaluLogger.Warn("[StreamingController]", $"Client {client.Id} disconnecting - {client.ConsecutiveSendErrors} consecutive send errors (transport: {client.Transport})");
                    break;
                }

                bool frameSent = false;

                if (client.Codec == CodecType.H264)
                {
                    frameSent = StreamH264ToClient(client, cancellationToken, nalSendBuffer, rtpBatchBuffer, h264Channel);

                    if (!frameSent)
                    {
                        consecutiveTimeouts++;

                        // Use a longer grace period during warmup (before first frame ever arrives)
                        // to give MediaCodec time to initialise. After that, use the short
                        // active threshold so stalls are caught within 400ms.
                        int maxTimeouts = gotFirstFrame ? activeTimeoutCount : warmupTimeoutCount;

                        if (consecutiveTimeouts >= maxTimeouts)
                        {
                            stallRecoveryAttempts++;
                            BaluLogger.Warn("[StreamingController]", $"No frames for {consecutiveTimeouts * FrameDequeueTimeoutMs}ms on camera {client.CameraId} (running={_encoderManager.IsEncoderRunning(client.CameraId)}, warmup={!gotFirstFrame}) — recovery attempt {stallRecoveryAttempts}/{MaxStallRecoveryAttempts}");
                            try
                            {
                                _encoderManager.StopEncoder(client.CameraId);

                                // Restart the camera and clear the cached frame so
                                // WaitForFrameAndStartEncoder blocks on a live frame instead
                                // of returning instantly from a stale cache (which would restart
                                // the encoder against a still-dead camera and loop indefinitely).
                                RestartCamera?.Invoke(client.CameraId);

                                await WaitForFrameAndStartEncoder(client, cancellationToken).ConfigureAwait(false);
                                _encoderManager.RequestKeyFrame(client.CameraId);
                                consecutiveTimeouts = 0;
                                gotFirstFrame = false;
                                stallRecoveryAttempts = 0; // successful recovery — reset counter
                                BaluLogger.Info("[StreamingController]", $"Camera+encoder restarted successfully for camera {client.CameraId}");
                            }
                            catch (TimeoutException)
                            {
                                if (stallRecoveryAttempts >= MaxStallRecoveryAttempts)
                                {
                                    BaluLogger.Error("[StreamingController]", $"All {MaxStallRecoveryAttempts} recovery attempts exhausted for camera {client.CameraId} — disconnecting client to trigger full restart");
                                    break;
                                }

                                // Camera did not recover yet — keep client connected and retry
                                // after another stall window (~5s). The outer watchdog will do a
                                // full server restart if we eventually exhaust all attempts.
                                BaluLogger.Warn("[StreamingController]", $"Recovery attempt {stallRecoveryAttempts}/{MaxStallRecoveryAttempts} failed for camera {client.CameraId} — retrying in ~{activeTimeoutCount * FrameDequeueTimeoutMs}ms");
                                consecutiveTimeouts = 0;
                                gotFirstFrame = false;
                            }
                        }
                    }
                    else
                    {
                        gotFirstFrame = true;
                        consecutiveTimeouts = 0;
                    }
                }
                else
                {
                    var frameStart = DateTime.UtcNow;
                    await StreamMjpegToClientAsync(client, cancellationToken).ConfigureAwait(false);
                    var elapsed = (DateTime.UtcNow - frameStart).TotalMilliseconds;
                    var waitTime = frameIntervalMs - (int)elapsed;

                    if (waitTime > 0)
                    {
                        await Task.Delay(waitTime, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            // Log why we exited
            BaluLogger.Info("[StreamingController]", $"Streaming loop exited for client {client.Id}: IsPlaying={client.IsPlaying}, Cancelled={cancellationToken.IsCancellationRequested}, SendErrors={client.ConsecutiveSendErrors}");
        }
        catch (Exception ex)
        {
            BaluLogger.Error("[StreamingController]", $"Streaming error for client {client.Id}: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // Unregister channel before cleanup so the fan-out stops writing to this client.
            if (client.Codec == CodecType.H264)
            {
                _encoderManager.UnregisterClientChannel(client.CameraId, client.Id);
            }
            _clientManager.CleanupClient(client);
        }
    }

    /// <summary>
    /// Waits for the first camera frame using exponential backoff, then starts
    /// the H.264 hardware encoder with the detected frame dimensions.
    /// </summary>
    /// <param name="client">The client requesting H.264 streaming.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="TimeoutException">Thrown if no frame arrives within the retry limit.</exception>
    private async Task WaitForFrameAndStartEncoder(Models.Client client, CancellationToken cancellationToken)
    {
        FrameEventArgs? frame = null;

        // Wait for first frame with exponential backoff.
        // Camera hardware init can take several seconds on MediaTek (especially after stop/start).
        int retries = 0;
        const int maxRetries = 100; // ~10 seconds with 100ms cap

        while ((frame = GetLatestFrame?.Invoke(client.CameraId)) == null || frame.Data == null)
        {
            if (retries++ > maxRetries)
            {
                throw new TimeoutException($"Timeout waiting for first frame from camera {client.CameraId}");
            }

            // Exponential backoff: 10ms, 20ms, 40ms, 80ms, then cap at 100ms
            int delayMs = Math.Min(10 * (1 << Math.Min(retries, 4)), 100);
            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        }

        // Use the reported dimensions from the camera
        int frameSize = frame.Data.Length;
        int reportedWidth = frame.Width;
        int reportedHeight = frame.Height;
        int expectedSize = (reportedWidth * reportedHeight * 3) / 2;

        if (frameSize != expectedSize)
        {
            BaluLogger.Info("[StreamingController]", $"Frame has stride padding: {frameSize} bytes (image: {reportedWidth}x{reportedHeight} = {expectedSize} bytes)");
        }

        BaluLogger.Info("[StreamingController]", $"Starting H264 encoder with {reportedWidth}x{reportedHeight}");
        _encoderManager.StartEncoder(client.CameraId, reportedWidth, reportedHeight, frameSize);

        // Check if encoder fell back to a different resolution
        var (actualW, actualH) = _encoderManager.GetActualResolution(client.CameraId);
        if (actualW > 0 && actualH > 0 && (actualW != reportedWidth || actualH != reportedHeight))
        {
            BaluLogger.Warn("[StreamingController]", $"Encoder fell back to {actualW}x{actualH} — requesting camera restart");
            // This stops the encoder and restarts the camera at the encoder's actual resolution
            EncoderResolutionFallback?.Invoke(this, (client.CameraId, actualW, actualH));

            // Wait for camera to produce frames at the new resolution, then restart encoder
            BaluLogger.Info("[StreamingController]", $"Waiting for camera to produce {actualW}x{actualH} frames...");
            frame = null;
            retries = 0;
            const int maxFallbackRetries = 50; // More retries — camera restart takes longer

            while ((frame = GetLatestFrame?.Invoke(client.CameraId)) == null || frame.Data == null)
            {
                if (retries++ > maxFallbackRetries)
                {
                    throw new TimeoutException($"Timeout waiting for frame at {actualW}x{actualH} from camera {client.CameraId}");
                }
                int delayMs = Math.Min(10 * (1 << Math.Min(retries, 4)), 100);
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }

            BaluLogger.Info("[StreamingController]", $"Got frame at {frame.Width}x{frame.Height} after camera restart, starting encoder");
            _encoderManager.StartEncoder(client.CameraId, frame.Width, frame.Height, frame.Data.Length);
        }
    }

    /// <summary>
    /// Timeout for waiting on encoded frames. If the encoder stalls (no output for this duration),
    /// the loop continues instead of blocking forever.
    /// </summary>
    private const int FrameDequeueTimeoutMs = 200;

    /// <summary>
    /// Streams a single H.264 frame to the client via RTP. Handles frame dequeuing
    /// with a 200ms timeout to prevent blocking on encoder stalls, IDR detection,
    /// frame pacing, SPS/PPS delivery, NAL filtering, and FU-A fragmentation.
    /// </summary>
    /// <param name="client">The target client.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if a frame was successfully sent, false otherwise.</returns>
    // Synchronous hot-path: no async/await → no thread-pool continuation dispatch.
    // Uses a pre-resolved channel reference (clientChannel) to avoid per-frame dictionary
    // lock acquisitions. WaitOnChannel blocks the OS thread via kernel futex (~1 ms);
    // Socket.Send() is a blocking syscall (~2 ms on LAN).
    private bool StreamH264ToClient(Models.Client client, CancellationToken cancellationToken, List<byte[]> nalSendBuffer, List<byte[]> rtpBatch, Channel<H264FrameEventArgs>? clientChannel)
    {
        H264FrameEventArgs? h264Frame;

        // Use cached channel reference when available — bypasses the per-frame dictionary lock.
        // Falls back to the manager methods if the reference was unavailable at session start.
        if (clientChannel != null)
        {
            if (!clientChannel.Reader.TryRead(out h264Frame))
            {
                if (cancellationToken.IsCancellationRequested) return false;
                h264Frame = WaitOnChannel(clientChannel, FrameDequeueTimeoutMs, cancellationToken);
                if (h264Frame == null)
                {
                    if (!cancellationToken.IsCancellationRequested)
                        BaluLogger.Warn("[StreamingController]", $"Frame dequeue timeout ({FrameDequeueTimeoutMs}ms) - encoder may be stalled for camera {client.CameraId}");
                    return false;
                }
            }
        }
        else
        {
            // Fallback: look up channel each call (used when channel ref is unavailable)
            if (!_encoderManager.TryDequeueFrame(client.CameraId, client.Id, out h264Frame) || h264Frame == null)
            {
                if (cancellationToken.IsCancellationRequested) return false;
                h264Frame = _encoderManager.WaitDequeueFrame(client.CameraId, client.Id, FrameDequeueTimeoutMs, cancellationToken);
                if (h264Frame == null)
                {
                    if (!cancellationToken.IsCancellationRequested)
                        BaluLogger.Warn("[StreamingController]", $"Frame dequeue timeout ({FrameDequeueTimeoutMs}ms) - encoder may be stalled for camera {client.CameraId}");
                    return false;
                }
            }
        }

        if (h264Frame == null || h264Frame.NalUnits.Count == 0)
        {
            return false;
        }

        // Skip frames with timestamps older than the last sent frame.
        // Use strict less-than to allow frames with equal timestamps through,
        // since MediaCodec on some SoCs can output consecutive frames with the same PTS.
        if (h264Frame.Timestamp < client.LastH264FrameTimestamp)
        {
            return false;
        }

        // Detect IDR frame (needed for SPS/PPS injection decision below)
        bool isIdrFrame = h264Frame.IsKeyFrame;
        if (!isIdrFrame && h264Frame.NalUnits.Count > 0)
        {
            var firstNal = h264Frame.NalUnits[0];
            int nalTypeOffset = 0;
            if (firstNal.Length >= 5 && firstNal[0] == 0 && firstNal[1] == 0 && firstNal[2] == 0 && firstNal[3] == 1)
                nalTypeOffset = 4;
            else if (firstNal.Length >= 4 && firstNal[0] == 0 && firstNal[1] == 0 && firstNal[2] == 1)
                nalTypeOffset = 3;

            if (nalTypeOffset > 0 && firstNal.Length > nalTypeOffset)
            {
                int nalType = firstNal[nalTypeOffset] & 0x1F;
                isIdrFrame = (nalType == 5);
            }
        }

        // No pacer delay: the encoder's 25fps output rate is the natural throttle, and the
        // per-client channel (DropOldest, size=1) already discards stale frames on bursts.
        // A software pacer would add 33ms of Thread.Sleep per frame on the now-synchronous path.

        uint frameRtpTimestamp = _rtpBuilder.EncoderTimestampToRtp((ulong)h264Frame.Timestamp, ref client);

        // No lock needed — LastH264FrameTimestamp and FrameCount are written only by this
        // streaming thread and read only within this same method.
        client.LastH264FrameTimestamp = h264Frame.Timestamp;
        client.FrameCount++;

        try
        {
            // Build all RTP packets for this frame into a batch, then send with a single syscall.
            // This eliminates per-packet await/lock overhead (5-20ms per frame on Android).
            rtpBatch.Clear();

            // Build SPS/PPS packets before keyframes/IDR frames, first frame, or if not cached
            bool needsSpsPps = isIdrFrame || client.FrameCount == 1 || !_clientManager.HasCachedSpsPps(client.Id);

            if (needsSpsPps && h264Frame.Sps != null && h264Frame.Pps != null)
            {
                _rtpBuilder.BuildH264NalRtpPackets(client, h264Frame.Sps, frameRtpTimestamp, false, rtpBatch);
                _rtpBuilder.BuildH264NalRtpPackets(client, h264Frame.Pps, frameRtpTimestamp, false, rtpBatch);
                _clientManager.CacheSpsPps(client.Id, h264Frame.Sps, h264Frame.Pps);
            }
            // Filter NAL units
            // SPS (7) and PPS (8) are already sent above when needsSpsPps is true,
            // so skip them here to avoid sending duplicate parameter sets.
            bool spsPpsSentSeparately = needsSpsPps && h264Frame.Sps != null && h264Frame.Pps != null;
            nalSendBuffer.Clear(); // reuse pre-allocated list from outer scope
            foreach (var nal in h264Frame.NalUnits)
            {
                if (nal.Length > 4)
                {
                    int offset = 0;
                    if (nal[0] == 0 && nal[1] == 0 && nal[2] == 0 && nal[3] == 1) offset = 4;
                    else if (nal[0] == 0 && nal[1] == 0 && nal[2] == 1) offset = 3;

                    if (offset > 0 && nal.Length > offset)
                    {
                        int nalType = nal[offset] & 0x1F;
                        // Skip AUD (9), filler (12)
                        if (nalType == 9 || nalType == 12) continue;
                        // Skip SPS (7), PPS (8) if already sent separately
                        if (spsPpsSentSeparately && (nalType == 7 || nalType == 8)) continue;
                    }
                }
                nalSendBuffer.Add(nal);
            }

            // Build all NAL RTP packets into the batch
            int nalCount = nalSendBuffer.Count;
            for (int i = 0; i < nalCount; i++)
            {
                bool isLastNalOfFrame = i == nalCount - 1;
                _rtpBuilder.BuildH264NalRtpPackets(client, nalSendBuffer[i], frameRtpTimestamp, isLastNalOfFrame, rtpBatch);
            }

            // Synchronous send — blocking syscall on the dedicated OS thread, no thread-pool hop
            _transportManager.SendBatchSync(client, rtpBatch);

            return true;
        }
        catch (ObjectDisposedException)
        {
            // Client was disposed (CleanupClient called) — stop streaming immediately
            BaluLogger.Info("[StreamingController]", $"Client disposed, stopping H264 stream");
            client.IsPlaying = false;
            return false;
        }
        catch (Exception ex)
        {
            BaluLogger.Error("[StreamingController]", $"H264 streaming error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Synchronously blocks the calling OS thread until a frame is available on the channel,
    /// the timeout elapses, or cancellation is requested. Mirrors WaitDequeueFrame but operates
    /// on a pre-resolved channel reference to avoid the per-frame dictionary lock.
    /// </summary>
    private static H264FrameEventArgs? WaitOnChannel(Channel<H264FrameEventArgs> channel, int timeoutMs, CancellationToken cancellationToken)
    {
        try
        {
            var vt = channel.Reader.WaitToReadAsync(cancellationToken);
            if (vt.IsCompleted)
            {
                if (!vt.Result) return null; // channel completed (encoder stopped)
                channel.Reader.TryRead(out var immediateFrame);
                return immediateFrame;
            }
            if (!vt.AsTask().Wait(timeoutMs, cancellationToken)) return null; // timeout
            channel.Reader.TryRead(out var frame);
            return frame;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Streams a single MJPEG frame to the client via RTP. Reads pre-encoded JPEG
    /// frames from the shared encoder service channel with a 100ms timeout.
    /// </summary>
    /// <param name="client">The target client.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task StreamMjpegToClientAsync(Models.Client client, CancellationToken cancellationToken)
    {
        try
        {
            // Consume pre-encoded JPEG frames from shared encoder service
            var channel = client.CameraId == 1 ? _jpegEncoder.FrontCameraOutput : _jpegEncoder.BackCameraOutput;

            // Try to read the latest frame with a short timeout
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(100); // 100ms timeout

            if (await channel.WaitToReadAsync(timeoutCts.Token).ConfigureAwait(false))
            {
                if (channel.TryRead(out var jpegFrame))
                {
                    if (jpegFrame.Data != null && jpegFrame.Data.Length > 0)
                    {
                        await _rtpBuilder.SendJpegAsRtpAsync(client, jpegFrame.Data).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout is normal - no frame available
        }
        catch (Exception ex)
        {
            BaluLogger.Error("[StreamingController]", $"MJPEG streaming error: {ex.Message}");
        }
    }

}
