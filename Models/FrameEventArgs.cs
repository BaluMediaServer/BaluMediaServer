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
    /// Gets or sets the timestamp of the frame capture in microseconds.
    /// </summary>
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
