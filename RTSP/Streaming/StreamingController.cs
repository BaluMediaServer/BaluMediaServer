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

    private bool _isStreaming;

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
    public bool IsStreaming => _isStreaming;

    /// <inheritdoc/>
    public event EventHandler<bool>? StreamingStateChanged;

    /// <summary>
    /// Event to request camera start.
    /// </summary>
    public event EventHandler<int>? CameraStartRequested;

    /// <summary>
    /// Delegate to retrieve the latest raw frame for a given camera ID.
    /// </summary>
    public Func<int, FrameEventArgs?>? GetLatestFrame { get; set; }

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
    /// Sets the streaming state.
    /// </summary>
    public void SetStreamingState(bool streaming)
    {
        _isStreaming = streaming;
        StreamingStateChanged?.Invoke(this, streaming);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// This method manages the streaming loop for a connected client, including:
    /// - Automatic camera start and encoder initialization for H.264
    /// - Socket health monitoring
    /// - Inactivity timeout tracking (60 seconds)
    /// - Consecutive error detection (10 errors threshold)
    /// - Activity tracking updates at each iteration to prevent false timeouts
    /// </remarks>
    public async Task StreamToClientAsync(Models.Client client, CancellationToken cancellationToken)
    {
        Log.Debug("[StreamingController]", $"Starting stream to client {client.Id} using {client.Transport}");

        if (!_isStreaming)
        {
            // Request camera start
            try
            {
                CameraStartRequested?.Invoke(this, client.CameraId);
            }
            catch (Exception ex)
            {
                Log.Error("[StreamingController]", $"CameraStartRequested subscriber error: {ex.Message}");
            }

            _isStreaming = true;
            try
            {
                StreamingStateChanged?.Invoke(this, true);
            }
            catch (Exception ex)
            {
                Log.Error("[StreamingController]", $"StreamingStateChanged subscriber error: {ex.Message}");
            }

            if (client.Codec == CodecType.H264)
            {
                // Wait for first frame and start encoder
                await WaitForFrameAndStartEncoder(client, cancellationToken).ConfigureAwait(false);
            }
        }

        // RTP seq/rtptime are initialized in HandlePlayAsync to match the PLAY response RTP-Info header

        const int frameIntervalMs = 22; // ~45fps

        try
        {
            Log.Info("[StreamingController]", $"Entering streaming loop for client {client.Id} (codec={client.Codec}, transport={client.Transport}, isPlaying={client.IsPlaying})");

            while (client.IsPlaying && !cancellationToken.IsCancellationRequested)
            {
                // Update activity time at start of each streaming attempt
                lock (client)
                {
                    client.LastActivityTime = DateTime.UtcNow;
                }

                // Check for too many consecutive errors (fast, no blocking)
                // Socket disconnection is detected reliably via send errors in TransportManager.
                // We do NOT use IsSocketConnected(Poll+Available) here because the HandleClient
                // reader loop competes on the same RTSP socket, causing a TOCTOU race that
                // falsely detects disconnection and breaks the streaming loop.
                if (client.ConsecutiveSendErrors >= 10)
                {
                    Log.Warn("[StreamingController]", $"Client {client.Id} disconnecting - {client.ConsecutiveSendErrors} consecutive send errors (transport: {client.Transport})");
                    break;
                }

                var frameStart = DateTime.UtcNow;
                bool frameSent = false;

                if (client.Codec == CodecType.H264)
                {
                    frameSent = await StreamH264ToClientAsync(client, cancellationToken).ConfigureAwait(false);
                }
                else
                {
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
            Log.Info("[StreamingController]", $"Streaming loop exited for client {client.Id}: IsPlaying={client.IsPlaying}, Cancelled={cancellationToken.IsCancellationRequested}, SendErrors={client.ConsecutiveSendErrors}");
        }
        catch (Exception ex)
        {
            Log.Error("[StreamingController]", $"Streaming error for client {client.Id}: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
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

        // Wait for first frame with exponential backoff (should be fast)
        int retries = 0;
        const int maxRetries = 20;

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
            Log.Info("[StreamingController]", $"Frame has stride padding: {frameSize} bytes (image: {reportedWidth}x{reportedHeight} = {expectedSize} bytes)");
        }

        Log.Info("[StreamingController]", $"Starting H264 encoder with {reportedWidth}x{reportedHeight}");
        _encoderManager.StartEncoder(client.CameraId, reportedWidth, reportedHeight, frameSize);
    }

    /// <summary>
    /// Timeout for waiting on encoded frames. If the encoder stalls (no output for this duration),
    /// the loop continues instead of blocking forever.
    /// </summary>
    private const int FrameDequeueTimeoutMs = 2000;

    /// <summary>
    /// Streams a single H.264 frame to the client via RTP. Handles frame dequeuing
    /// with a 2-second timeout to prevent blocking on encoder stalls, IDR detection,
    /// frame pacing, SPS/PPS delivery, NAL filtering, and FU-A fragmentation.
    /// </summary>
    /// <param name="client">The target client.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if a frame was successfully sent, false otherwise.</returns>
    private async Task<bool> StreamH264ToClientAsync(Models.Client client, CancellationToken cancellationToken)
    {
        H264FrameEventArgs? h264Frame;

        // Try non-blocking dequeue first (zero allocation in the hot path)
        if (!_encoderManager.TryDequeueFrame(client.CameraId, out h264Frame) || h264Frame == null)
        {
            // No frame ready — wait with a timeout to prevent blocking forever if encoder stalls
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(FrameDequeueTimeoutMs);
                h264Frame = await _encoderManager.DequeueFrameAsync(client.CameraId, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Timeout — encoder stalled, log and continue the loop
                Log.Warn("[StreamingController]", $"Frame dequeue timeout ({FrameDequeueTimeoutMs}ms) - encoder may be stalled for camera {client.CameraId}");
                return false;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (ChannelClosedException)
            {
                Log.Warn("[StreamingController]", $"Frame channel closed for camera {client.CameraId} - encoder stopped");
                return false;
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

        // Get or create pacer for this client
        var pacer = _clientManager.GetOrCreatePacer(client.Id, 25);

        // Detect IDR frame
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

        // Check if we should drop this frame
        if (pacer.ShouldDropFrame(isIdrFrame))
        {
            pacer.RecordDrop();
            return false;
        }

        // Wait for proper timing
        int delayMs = pacer.GetDelayForNextFrame();
        if (delayMs > 0)
        {
            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        }

        uint frameRtpTimestamp = _rtpBuilder.EncoderTimestampToRtp((ulong)h264Frame.Timestamp, ref client);

        lock (client)
        {
            client.LastH264FrameTimestamp = h264Frame.Timestamp;
            client.FrameCount++;
        }

        try
        {
            // Send SPS/PPS before keyframes/IDR frames, first frame, or if not cached
            bool needsSpsPps = isIdrFrame || client.FrameCount == 1 || !_clientManager.HasCachedSpsPps(client.Id);

            if (needsSpsPps && h264Frame.Sps != null && h264Frame.Pps != null)
            {
                await _rtpBuilder.SendH264NalAsRtpAsync(client, h264Frame.Sps, frameRtpTimestamp, false).ConfigureAwait(false);
                await _rtpBuilder.SendH264NalAsRtpAsync(client, h264Frame.Pps, frameRtpTimestamp, false).ConfigureAwait(false);
                _clientManager.CacheSpsPps(client.Id, h264Frame.Sps, h264Frame.Pps);
            }
            // Filter and send NAL units
            // SPS (7) and PPS (8) are already sent above when needsSpsPps is true,
            // so skip them here to avoid sending duplicate parameter sets.
            bool spsPpsSentSeparately = needsSpsPps && h264Frame.Sps != null && h264Frame.Pps != null;
            var nalUnitsToSend = new List<byte[]>();
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
                nalUnitsToSend.Add(nal);
            }

            int nalCount = nalUnitsToSend.Count;
            for (int i = 0; i < nalCount; i++)
            {
                var nalUnit = nalUnitsToSend[i];
                bool isLastNalOfFrame = i == nalCount - 1;
                await _rtpBuilder.SendH264NalAsRtpAsync(client, nalUnit, frameRtpTimestamp, isLastNalOfFrame).ConfigureAwait(false);
            }

            pacer.MarkFrameSent();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // Client was disposed (CleanupClient called) — stop streaming immediately
            Log.Info("[StreamingController]", $"Client disposed, stopping H264 stream");
            client.IsPlaying = false;
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("[StreamingController]", $"H264 streaming error: {ex.Message}");
            return false;
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
            Log.Error("[StreamingController]", $"MJPEG streaming error: {ex.Message}");
        }
    }

}
