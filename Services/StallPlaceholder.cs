using BaluMediaServer.Repositories;
#if ANDROID
using AGBitmap = Android.Graphics.Bitmap;
using AGCanvas = Android.Graphics.Canvas;
using AGColor  = Android.Graphics.Color;
using AGPaint  = Android.Graphics.Paint;
#endif

namespace BaluMediaServer.Services;

/// <summary>
/// Renders a "stream stalled" placeholder frame as an NV21 buffer: a black background with
/// centered status text and a live elapsed-seconds counter. Fed to the H.264 encoder (and the
/// MJPEG/JPEG path) while the camera is delivering no frames, so the client is never starved
/// into a disconnect and the viewer sees an informative screen instead of a frozen/dropped stream.
///
/// Only the Y (luma) plane is touched: white text on black is chroma-neutral, so the entire
/// frame keeps U=V=128. The text is re-rasterized only when the visible second changes (the
/// rest of the time the same buffer is returned), so the per-frame cost during a stall is ~nil.
/// </summary>
public sealed class StallPlaceholder : IDisposable
{
    private readonly int _w, _h, _ySize, _frameSize;
    private readonly string[] _messageLines;
    private readonly byte[] _output;     // NV21: Y plane then interleaved VU
    private int _lastRenderedSecond = -1;

    /// <summary>Black-with-text NV21 luma background value (BT.601 limited-range black).</summary>
    private const byte LumaBlack = 16;
    /// <summary>Luma value used for fully-opaque text pixels (limited-range white).</summary>
    private const byte LumaWhite = 235;

    /// <param name="width">Frame width in pixels (must match the encoder input).</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="messageLines">Static text lines shown above the live timer.</param>
    public StallPlaceholder(int width, int height, string[] messageLines)
    {
        _w = width;
        _h = height;
        _ySize = width * height;
        _frameSize = _ySize + _ySize / 2;
        _messageLines = messageLines;

        _output = new byte[_frameSize];
        ResetToBlack();
    }

    public int Width => _w;
    public int Height => _h;

    private void ResetToBlack()
    {
        Array.Fill(_output, LumaBlack, 0, _ySize);                   // Y plane = black
        Array.Fill(_output, (byte)128, _ySize, _frameSize - _ySize); // VU plane = neutral
    }

    /// <summary>
    /// Returns an NV21 buffer showing the message and the given elapsed time. The returned
    /// buffer is reused across calls; consumers must copy it if they retain it beyond their
    /// callback (the encoder/JPEG paths copy immediately, matching the camera frame contract).
    /// </summary>
    public byte[] Render(int elapsedSeconds)
    {
        if (elapsedSeconds == _lastRenderedSecond)
            return _output;
        _lastRenderedSecond = elapsedSeconds;

        ResetToBlack();
        try { StampText(elapsedSeconds); }
        catch (System.Exception ex) { BaluLogger.Warn("[StallPlaceholder]", $"Text render failed: {ex.Message}"); }
        return _output;
    }

#if ANDROID
    private void StampText(int elapsedSeconds)
    {
        // Scale text to the frame so it is readable at any resolution.
        float titleSize = System.Math.Clamp(_h * 0.060f, 24f, 96f);
        float bodySize  = System.Math.Clamp(_h * 0.040f, 18f, 64f);

        using var titlePaint = new AGPaint(Android.Graphics.PaintFlags.AntiAlias) { TextSize = titleSize, FakeBoldText = true };
        using var bodyPaint  = new AGPaint(Android.Graphics.PaintFlags.AntiAlias) { TextSize = bodySize };
        titlePaint.Color = AGColor.White;
        bodyPaint.Color  = AGColor.White;

        // Line set: first message line uses the title paint, the rest + the timer use the body paint.
        var lines = new System.Collections.Generic.List<(string text, AGPaint paint)>();
        for (int i = 0; i < _messageLines.Length; i++)
            lines.Add((_messageLines[i], i == 0 ? titlePaint : bodyPaint));
        lines.Add(($"{elapsedSeconds}s", bodyPaint));

        // Measure the text block.
        int blockW = 1, blockH = 0;
        var lineH = new int[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            var (text, paint) = lines[i];
            var m = paint.GetFontMetrics()!;
            lineH[i] = (int)System.Math.Ceiling(m.Bottom - m.Top) + 6;
            blockH += lineH[i];
            blockW = System.Math.Max(blockW, (int)System.Math.Ceiling(paint.MeasureText(text)) + 8);
        }
        blockW = System.Math.Min(blockW, _w);
        blockH = System.Math.Min(blockH, _h);

        using var bmp    = AGBitmap.CreateBitmap(blockW, blockH, AGBitmap.Config.Argb8888!)!;
        using var canvas = new AGCanvas(bmp);
        bmp.EraseColor(0);

        float y = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            var (text, paint) = lines[i];
            var m = paint.GetFontMetrics()!;
            float baseline = y - m.Top + 3;
            float tw = paint.MeasureText(text);
            canvas.DrawText(text, (blockW - tw) / 2f, baseline, paint);
            y += lineH[i];
        }

        // Composite the text block (centered) onto the Y plane, blending anti-aliased edges.
        int total = blockW * blockH;
        var argb = new int[total];
        bmp.GetPixels(argb, 0, blockW, 0, 0, blockW, blockH);

        int posX = (_w - blockW) / 2;
        int posY = (_h - blockH) / 2;

        for (int row = 0; row < blockH; row++)
        {
            int fy = posY + row;
            if (fy < 0 || fy >= _h) continue;
            int dstBase = fy * _w + posX;
            int srcBase = row * blockW;
            for (int col = 0; col < blockW; col++)
            {
                int fx = posX + col;
                if (fx < 0 || fx >= _w) continue;
                int alpha = (argb[srcBase + col] >> 24) & 0xFF;
                if (alpha < 16) continue;
                // Blend white text over black using the glyph alpha.
                byte luma = (byte)(LumaBlack + (alpha * (LumaWhite - LumaBlack)) / 255);
                _output[dstBase + col] = luma;
            }
        }
    }
#else
    private void StampText(int elapsedSeconds) { /* text rendering requires Android.Graphics */ }
#endif

    public void Dispose() { /* no native resources retained between renders */ }
}
