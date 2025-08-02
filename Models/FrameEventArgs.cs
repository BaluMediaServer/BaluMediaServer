namespace BaluMediaServer.Models;
public class FrameEventArgs : EventArgs
{
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public int Width { get; set; }
    public int Height { get; set; }
    public long Timestamp { get; set; }
    public int Format { get; set; }
    public string CameraId { get; set; } = string.Empty;
}