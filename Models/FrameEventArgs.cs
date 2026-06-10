namespace BaluMediaServer.Models;

/// <summary>
/// Represents event arguments for raw camera frame data.
/// Contains the raw YUV frame data along with metadata about the frame dimensions and source.
/// </summary>
public class FrameEventArgs : EventArgs
{
    /// <summary>
    /// Gets or sets the raw frame data in YUV format.
    /// </summary>
    /// <remarks>
    /// <b>Buffer lifetime (v1.5.29+):</b> this array comes from a small reusable ring buffer in
    /// the camera service (avoiding a multi-MB managed allocation per frame) and is overwritten
    /// roughly 6 frames later (~200ms at 30fps). All built-in consumers (H.264 encoder, MJPEG,
    /// snapshots) copy or consume the data immediately. Event subscribers that keep frame data
    /// beyond their callback must make their own copy.
    /// </remarks>
    public byte[] Data { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Gets or sets the width of the frame in pixels.
    /// </summary>
    public int Width { get; set; }

    /// <summary>
    /// Gets or sets the height of the frame in pixels.
    /// </summary>
    public int Height { get; set; }

    /// <summary>
    /// Gets or sets the timestamp of the frame capture in <b>nanoseconds</b>
    /// (camera2 <c>SENSOR_TIMESTAMP</c>, boottime clock).
    /// </summary>
    /// <remarks>
    /// This was historically documented as microseconds, and feeding the raw value into
    /// MediaCodec (which expects microseconds) made the MediaTek encoder's time-based GOP
    /// logic emit an IDR on every frame. Convert with <c>/ 1000</c> before use as a
    /// MediaCodec presentation timestamp — <c>H264EncoderManager.FeedFrame</c> does this.
    /// </remarks>
    public long Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the image format type (e.g., NV21, YUV_420_888).
    /// </summary>
    public int Format { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the camera that captured this frame.
    /// </summary>
    public string CameraId { get; set; } = string.Empty;
}
