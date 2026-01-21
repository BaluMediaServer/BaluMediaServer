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
    private readonly IRtpPacketBuilder _rtpBuilder;
    private readonly ITransportManager _transportManager;
    private readonly IClientManager _clientManager;

    private bool _isStreaming;
    private const int H264PollIntervalMs = 10;
    private const int MjpegFrameIntervalMs = 40;
    private const int InactivityTimeoutSeconds = 10;

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
        IRtpPacketBuilder rtpBuilder,
        ITransportManager transportManager,
        IClientManager clientManager)
    {
        _encoderManager = encoderManager;
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
    public async Task StreamToClientAsync(Models.Client client, CancellationToken cancellationToken)
    {
        Log.Debug("[StreamingController]", $"Starting stream to client {client.Id} using {client.Transport}");

        if (!_isStreaming)
        {
            // Request camera start
            CameraStartRequested?.Invoke(this, client.CameraId);

            _isStreaming = true;
            StreamingStateChanged?.Invoke(this, true);

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
                // Check socket health
                if (!_transportManager.IsSocketConnected(client.Socket))
                {
                    Log.Warn("[StreamingController]", $"Client {client.Id} socket disconnected");
                    break;
                }

                // Check for inactivity timeout
                var timeSinceLastActivity = (DateTime.UtcNow - client.LastActivityTime).TotalSeconds;
                if (timeSinceLastActivity > InactivityTimeoutSeconds)
                {
                    Log.Warn("[StreamingController]", $"Client {client.Id} inactive for {timeSinceLastActivity:F0}s, disconnecting");
                    break;
                }

                // Check for too many consecutive errors
                if (client.ConsecutiveSendErrors >= 10)
                {
                    Log.Warn("[StreamingController]", $"Client {client.Id} has {client.ConsecutiveSendErrors} consecutive errors, disconnecting");
                    break;
                }

                var frameStart = DateTime.UtcNow;
                bool frameSent = false;

                if (client.Codec == CodecType.H264)
                {
                    frameSent = await StreamH264ToClientAsync(client, cancellationToken).ConfigureAwait(false);
                    if (!frameSent)
                    {
                        await Task.Delay(H264PollIntervalMs, cancellationToken).ConfigureAwait(false);
                    }
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

        // Wait for first frame
        while ((frame = GetLatestFrame?.Invoke(client.CameraId)) == null || frame.Data == null)
        {
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
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
        if (!_encoderManager.TryDequeueFrame(client.CameraId, out var h264Frame) || h264Frame == null || h264Frame.NalUnits.Count == 0)
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
        var frame = GetLatestFrame?.Invoke(client.CameraId);

        if (frame != null && frame.Data != null && frame.Data.Length > 0)
        {
            try
            {
                var jpegData = EncodeToJpeg(frame.Data, frame.Width, frame.Height, Android.Graphics.ImageFormatType.Nv21, client.VideoProfile.Quality);

                if (jpegData != null && jpegData.Length > 0)
                {
                    await _rtpBuilder.SendJpegAsRtpAsync(client, jpegData).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log.Error("[StreamingController]", $"MJPEG frame encoding error: {ex.Message}");
            }
        }
    }

    private static byte[] EncodeToJpeg(byte[] rawImageData, int width, int height, Android.Graphics.ImageFormatType format, int quality = 80)
    {
        try
        {
            using var outputStream = new MemoryStream();
            if (format == Android.Graphics.ImageFormatType.Nv21 || format == Android.Graphics.ImageFormatType.Yuv420888)
            {
                var yuvImage = new Android.Graphics.YuvImage(rawImageData, Android.Graphics.ImageFormatType.Nv21, width, height, null);
                var rect = new Android.Graphics.Rect(0, 0, width, height);
                yuvImage.CompressToJpeg(rect, quality, outputStream);
            }
            else
            {
                var bitmap = Android.Graphics.BitmapFactory.DecodeByteArray(rawImageData, 0, rawImageData.Length);
                if (bitmap != null)
                {
                    bitmap.Compress(Android.Graphics.Bitmap.CompressFormat.Jpeg!, quality, outputStream);
                    bitmap.Dispose();
                }
                else
                {
                    Log.Error("[StreamingController]", "Failed to decode image data");
                    return Array.Empty<byte>();
                }
            }
            return outputStream.ToArray();
        }
        catch (Exception ex)
        {
            Log.Error("[StreamingController]", $"JPEG encoding error: {ex.Message}");
            return Array.Empty<byte>();
        }
    }
}
