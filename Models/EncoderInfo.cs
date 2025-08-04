using Android.Media;
namespace BaluMediaServer.Models;
public class EncoderInfo
{
    public MediaCodecInfo Codec { get; set; } = default!;
    public string Name { get; set; } = string.Empty;
    public int Score { get; set; }
    public bool SupportsLowLatency { get; set; }
    public bool SupportsBitrateMode { get; set; }
    public bool SupportsIntraRefresh { get; set; }
    public List<int> ColorFormats { get; set; } = new();
    public MediaCodecInfo.CodecCapabilities Capabilities { get; set; } = default!;
}