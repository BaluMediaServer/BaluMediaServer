namespace BaluMediaServer.Models;

/// <summary>
/// Represents event arguments for H.264 encoded frame data.
/// Contains NAL units and codec configuration data for H.264 streaming.
/// </summary>
public class H264FrameEventArgs : EventArgs
{
    /// <summary>
    /// Gets or sets the list of NAL (Network Abstraction Layer) units for this frame.
    /// Each NAL unit represents a portion of the encoded video data.
    /// </summary>
    public List<byte[]> NalUnits { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether this frame is a key frame (IDR frame).
    /// Key frames are complete reference frames that don't require previous frames for decoding.
    /// </summary>
    public bool IsKeyFrame { get; set; }

    /// <summary>
    /// Gets or sets the presentation timestamp of the frame in encoder time units.
    /// </summary>
    public long Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the Sequence Parameter Set (SPS) NAL unit.
    /// Contains essential encoding parameters required for decoder initialization.
    /// </summary>
    public byte[]? Sps { get; set; }

    /// <summary>
    /// Gets or sets the Picture Parameter Set (PPS) NAL unit.
    /// Contains picture-specific parameters required for decoding.
    /// </summary>
    public byte[]? Pps { get; set; }
}
