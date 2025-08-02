namespace BaluMediaServer.Models;
public class H264FrameEventArgs : EventArgs
{
    public List<byte[]> NalUnits { get; set; } = new();
    public bool IsKeyFrame { get; set; }
    public long Timestamp { get; set; }
    public byte[]? Sps { get; set; }
    public byte[]? Pps { get; set; }
}