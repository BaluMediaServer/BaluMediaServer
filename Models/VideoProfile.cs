namespace BaluMediaServer.Models;

public class VideoProfile
{
    private string _name = string.Empty;
    public string Name
    {
        get => _name; set
        {
            if (!String.IsNullOrEmpty(value))
            {
                _name = value.Trim().Replace(" ", "").Replace("/", "");
            }
        }
    }
    public int Height { get; set; } = 640;
    public int Width { get; set; } = 480;
    public int MaxBitrate { get; set; } = 4000000;
    public int MinBitrate { get; set; } = 500000;
    private int _quality = 80;
    public int Quality
    {
        get => _quality;
        set
        {
            if (value > 100) _quality = 100;
            _quality = value > 100 ? 100 : value < 10 ? 10 : value;
        }
    }
}