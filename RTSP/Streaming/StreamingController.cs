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
    /// Polling interval in milliseconds for checking H.264 frame availability.
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
    /// Event to get latest frame.
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

        // Initialize RTP timestamp with random value
        lock (client)
        {
            client.RtpTimestamp = (uint)Random.Shared.Next(0, int.MaxValue);
            client.SequenceNumber = (ushort)Random.Shared.Next(0, ushort.MaxValue);
            client.LastRtpTime = DateTime.UtcNow;
        }

        const int frameIntervalMs = 22; // ~45fps

        try
        {
            while (client.IsPlaying && !cancellationToken.IsCancellationRequested)
            {
                // Update activity time at start of each streaming attempt
                // This prevents timeout as long as the streaming loop is active
                lock (client)
                {
                    client.LastActivityTime = DateTime.UtcNow;
                }

                // Check socket health
                if (!_transportManager.IsSocketConnected(client.Socket))
                {
                    Log.Warn("[StreamingController]", $"Client {client.Id} disconnecting - socket no longer connected (transport: {client.Transport})");
                    break;
                }

                // Check for inactivity timeout (should rarely trigger now)
                var timeSinceLastActivity = (DateTime.UtcNow - client.LastActivityTime).TotalSeconds;
                if (timeSinceLastActivity > InactivityTimeoutSeconds)
                {
                    Log.Warn("[StreamingController]", $"Client {client.Id} inactive for {timeSinceLastActivity:F0}s (timeout: {InactivityTimeoutSeconds}s), disconnecting due to inactivity");
                    break;
                }

                // Check for too many consecutive errors
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
                    // No polling needed - StreamH264ToClientAsync now waits for frames asynchronously
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
        }
        catch (Exception ex)
        {
            Log.Error("[StreamingController]", $"Streaming error: {ex.Message}");
        }
        finally
        {
            _clientManager.CleanupClient(client);
        }
    }

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

    private async Task<bool> StreamH264ToClientAsync(Models.Client client, CancellationToken cancellationToken)
    {
        H264FrameEventArgs h264Frame;

        try
        {
            // Asynchronously wait for next frame (no polling!)
            h264Frame = await _encoderManager.DequeueFrameAsync(client.CameraId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        if (h264Frame == null || h264Frame.NalUnits.Count == 0)
        {
            return false;
        }

        // Check if this is a new frame
        if (h264Frame.Timestamp <= client.LastH264FrameTimestamp)
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
        catch (Exception ex)
        {
            Log.Error("[StreamingController]", $"H264 streaming error: {ex.Message}");
            return false;
        }
    }

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
