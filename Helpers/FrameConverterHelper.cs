using Com.BaluMedia.CameraStreamer;

namespace BaluMediaServer.Helpers;

/// <summary>
/// Helper class for converting video frame formats.
/// Provides wrappers around native frame conversion methods.
/// </summary>
public static class FrameConverterHelper
{
    /// <summary>
    /// Converts NV21 format frame data to RGB888 format.
    /// </summary>
    /// <param name="data">The NV21 frame data.</param>
    /// <param name="width">The frame width in pixels.</param>
    /// <param name="height">The frame height in pixels.</param>
    /// <returns>The converted RGB888 data, or <c>null</c> if conversion fails.</returns>
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

    /// <summary>
    /// Converts NV21 format frame data to ARGB8888 format.
    /// </summary>
    /// <param name="data">The NV21 frame data.</param>
    /// <param name="width">The frame width in pixels.</param>
    /// <param name="height">The frame height in pixels.</param>
    /// <returns>The converted ARGB8888 data, or <c>null</c> if conversion fails.</returns>
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
