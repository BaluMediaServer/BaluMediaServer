using Com.BaluMedia.CameraStreamer;

namespace BaluMediaServer.Helpers;
public static class FrameConverterHelper
{
    public static byte[]? ConvertNv21ToRgb888(byte[] data, int width, int height)
    {
        try
        {
            return FrameConverter.Nv21ToRgb888(data, width, height);
        }
        catch
        {
            return null;
        }
    }

    public static byte[]? ConvertNv21ToArgb8888(byte[] data, int width, int height)
    {
        try
        {
            return FrameConverter.Nv21ToArgb8888(data, width, height);
            //return null;
        }
        catch
        {
            return null;
        }
    }
}