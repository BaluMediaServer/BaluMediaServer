using Android.Util;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using AGBitmap = Android.Graphics.Bitmap;
using AGCanvas = Android.Graphics.Canvas;
using AGColor  = Android.Graphics.Color;
using AGPaint  = Android.Graphics.Paint;

namespace BaluMediaServer.Services;

// ── Public API ───────────────────────────────────────────────────────────────

/// <summary>What text a slot displays.</summary>
public enum OverlayContent
{
    /// <summary>Android Build.Manufacturer + Build.Model (static).</summary>
    DeviceName,
    /// <summary>Local IPv4 address, refreshed every minute.</summary>
    IpAddress,
    /// <summary>Full date and time: yyyy-MM-dd HH:mm:ss (refreshed each second).</summary>
    DateTime,
    /// <summary>Date only: yyyy-MM-dd (refreshed each day).</summary>
    Date,
    /// <summary>Time only: HH:mm:ss (refreshed each second).</summary>
    Time,
    /// <summary>Static text supplied via <see cref="OverlaySlot.CustomText"/>.</summary>
    Custom,
}

/// <summary>Which corner or edge of the frame the slot is anchored to.</summary>
public enum AnchorPoint
{
    TopLeft, TopCenter, TopRight,
    BottomLeft, BottomCenter, BottomRight,
    /// <summary>
    /// <see cref="OverlaySlot.MarginX"/> and <see cref="OverlaySlot.MarginY"/> are
    /// treated as absolute pixel coordinates (top-left of the text bounding box).
    /// </summary>
    Absolute,
}

/// <summary>
/// RGB text colour. Use the named static properties or supply your own RGB values.
/// </summary>
public readonly struct OverlayColor
{
    public byte R { get; }
    public byte G { get; }
    public byte B { get; }

    public OverlayColor(byte r, byte g, byte b) { R = r; G = g; B = b; }

    public static OverlayColor White  => new(255, 255, 255);
    public static OverlayColor Yellow => new(255, 255,   0);
    public static OverlayColor Orange => new(255, 165,   0);
    public static OverlayColor Red    => new(255,   0,   0);
    public static OverlayColor Green  => new(  0, 220,   0);
    public static OverlayColor Cyan   => new(  0, 255, 255);
    public static OverlayColor Gray   => new(180, 180, 180);

    internal AGColor ToAndroid() => AGColor.Rgb(R, G, B);
}

/// <summary>Configuration for one text element in the overlay.</summary>
public sealed class OverlaySlot
{
    public OverlayContent Content  { get; set; } = OverlayContent.Custom;
    public string? CustomText      { get; set; }
    public AnchorPoint Anchor      { get; set; } = AnchorPoint.BottomLeft;
    public int MarginX             { get; set; } = 10;
    public int MarginY             { get; set; } = 10;
    public float TextSize          { get; set; } = 32f;
    public OverlayColor Color      { get; set; } = OverlayColor.White;
}

// ── FrameOverlay ──────────────────────────────────────────────────────────────

/// <summary>
/// Stamps up to <b>four</b> configurable text slots into the Y (luma) and UV (chroma)
/// planes of NV21/NV12 frame buffers before they are fed to MediaCodec.
///
/// <list type="bullet">
///   <item>Static slots (DeviceName, Custom) are rendered once at construction via Android Canvas.</item>
///   <item>Dynamic slots use a <b>glyph atlas</b>: each needed character is rendered to YUV
///         once at startup, then per-second composition is pure managed array blitting —
///         zero JNI calls, zero GC pressure, no ART stop-the-world pauses.</item>
///   <item>Per-frame hot path reads one volatile reference and does a tight byte[] stamp.</item>
/// </list>
/// </summary>
public sealed class FrameOverlay : IDisposable
{
    // ── immutable render snapshot (swapped atomically via volatile reference) ──

    private sealed class RenderSnapshot
    {
        public readonly byte[] Y, Cb, Cr;
        public readonly int W, H, PosX, PosY;
        public RenderSnapshot(byte[] y, byte[] cb, byte[] cr, int w, int h, int posX, int posY)
            => (Y, Cb, Cr, W, H, PosX, PosY) = (y, cb, cr, w, h, posX, posY);
    }

    // ── pre-rendered glyph: one character, already in YUV ────────────────────

    private sealed class GlyphData
    {
        public readonly byte[] Y, Cb, Cr;
        public readonly int W, H;
        public GlyphData(byte[] y, byte[] cb, byte[] cr, int w, int h)
            => (Y, Cb, Cr, W, H) = (y, cb, cr, w, h);
    }

    // ── internal per-slot state ───────────────────────────────────────────────

    private sealed class SlotState
    {
        public OverlaySlot Config = null!;

        // Written by render task, read by Stamp hot path.
        public volatile RenderSnapshot? Snap;
        public volatile bool RenderInFlight;

        public bool   IsStatic;
        public int    LastSecond    = -1;
        public int    LastDayOfYear = -1;
        public int    LastMinute    = -1;
        public string LastIp        = "";

        // Glyph atlas: pre-rendered characters in YUV, built once at startup.
        // Non-null for dynamic slots. Per-second composition uses this atlas — no JNI.
        public Dictionary<char, GlyphData>? Atlas;
        public int   AtlasLineH;

        // Cached Java resources used only for non-atlas (initial/static) renders.
        public AGPaint?  RenderPaint;
        public AGBitmap? RenderBitmap;
        public AGCanvas? RenderCanvas;
        public int[]?    RenderArgb;
        public int       RenderBmpW, RenderBmpH;
        public float     RenderBaseline;

        public void DisposeRenderResources()
        {
            try { RenderCanvas?.Dispose(); } catch { }
            try { RenderBitmap?.Recycle(); RenderBitmap?.Dispose(); } catch { }
            try { RenderPaint?.Dispose();  } catch { }
            RenderCanvas = null; RenderBitmap = null; RenderPaint = null; RenderArgb = null;
        }
    }

    private readonly SlotState[] _slots;
    private readonly int _frameW, _frameH;
    private bool _disposed;

    // ── factories ─────────────────────────────────────────────────────────────

    public static FrameOverlay? Default(int frameW, int frameH)
    {
        try
        {
            int clockH;
            using (var p = new AGPaint(Android.Graphics.PaintFlags.AntiAlias) { TextSize = 28f })
            {
                var m = p.GetFontMetrics()!;
                clockH = (int)Math.Ceiling(m.Bottom - m.Top) + 6;
            }

            const int clockMarginY = 4;
            int       nameMarginY  = clockMarginY + clockH + 3;

            return new FrameOverlay(frameW, frameH,
                new OverlaySlot
                {
                    Content  = OverlayContent.DeviceName,
                    Anchor   = AnchorPoint.BottomLeft,
                    MarginX  = 10, MarginY = nameMarginY,
                    TextSize = 34f, Color  = OverlayColor.White,
                },
                new OverlaySlot
                {
                    Content  = OverlayContent.Time,
                    Anchor   = AnchorPoint.BottomLeft,
                    MarginX  = 10, MarginY = clockMarginY,
                    TextSize = 28f, Color  = OverlayColor.White,
                });
        }
        catch (Exception ex)
        {
            BaluLogger.Warn("[FrameOverlay]", $"Failed to create default overlay: {ex.Message}");
            return null;
        }
    }

    // ── constructor ───────────────────────────────────────────────────────────

    public FrameOverlay(int frameW, int frameH, params OverlaySlot[] slots)
    {
        if (slots.Length == 0)
            throw new ArgumentException("At least one slot is required.", nameof(slots));

        _frameW = frameW;
        _frameH = frameH;

        int count = Math.Min(slots.Length, 4);
        _slots = new SlotState[count];

        var now = System.DateTime.Now;
        for (int i = 0; i < count; i++)
        {
            var state = new SlotState
            {
                Config   = slots[i],
                IsStatic = slots[i].Content is OverlayContent.DeviceName or OverlayContent.Custom,
            };
            _slots[i] = state;

            // Initial render via Canvas (one-time JNI — sets up first visible frame).
            RenderSync(state, ResolveText(state, now));

            // Build glyph atlas for dynamic slots so all subsequent renders are JNI-free.
            if (!state.IsStatic)
                BuildAtlas(state);
        }
    }

    // ── per-frame entry point ─────────────────────────────────────────────────

    /// <summary>
    /// Stamps all configured slots into an NV21/NV12 frame buffer.
    /// Dynamic slots kick off a background composition when their value changes;
    /// the hot path reads a pre-built snapshot — no JNI, no allocation, no GC pressure.
    /// </summary>
    public void StampInto(byte[] yuvData, int frameStride, int frameH)
    {
        var now = System.DateTime.Now;
        foreach (var slot in _slots)
        {
            if (!slot.IsStatic)
                RefreshIfNeeded(slot, now);

            var snap = slot.Snap;
            if (snap != null)
                Stamp(snap, yuvData, frameStride, frameH);
        }
    }

    // ── refresh logic ─────────────────────────────────────────────────────────

    private void RefreshIfNeeded(SlotState slot, System.DateTime now)
    {
        bool needed = slot.Config.Content switch
        {
            OverlayContent.Time or
            OverlayContent.DateTime   => slot.LastSecond    != now.Second,
            OverlayContent.Date       => slot.LastDayOfYear != now.DayOfYear,
            OverlayContent.IpAddress  => slot.LastMinute    != now.Minute,
            _                          => false,
        };
        if (!needed) return;

        slot.LastSecond    = now.Second;
        slot.LastDayOfYear = now.DayOfYear;
        slot.LastMinute    = now.Minute;

        string text = ResolveText(slot, now);
        if (slot.Config.Content == OverlayContent.IpAddress && text == slot.LastIp)
            return;
        slot.LastIp = text;

        StartRenderAsync(slot, text);
    }

    // ── async render ──────────────────────────────────────────────────────────

    private void StartRenderAsync(SlotState slot, string text)
    {
        if (slot.RenderInFlight) return;
        slot.RenderInFlight = true;

        bool hasAtlas = slot.Atlas != null;
        // Only capture color/paint resources when atlas is unavailable (rare fallback).
        AGColor color   = hasAtlas ? default : slot.Config.Color.ToAndroid();
        AnchorPoint anchor = slot.Config.Anchor;
        int mx = slot.Config.MarginX, my = slot.Config.MarginY;

        Task.Run(() =>
        {
            try
            {
                // Atlas path: pure managed array blitting — zero JNI, zero GC pressure.
                // Fallback path: JNI Canvas render (only if atlas build failed at startup).
                var (y, cb, cr, w, h) = hasAtlas
                    ? ComposeFromAtlas(slot, text)
                    : RenderIntoSlot(slot, text, color);

                var (posX, posY) = ComputePosition(anchor, mx, my, w, h);
                slot.Snap = new RenderSnapshot(y, cb, cr, w, h, posX, posY);
            }
            catch (Exception ex)
            {
                BaluLogger.Warn("[FrameOverlay]", $"Async render failed: {ex.Message}");
            }
            finally
            {
                slot.RenderInFlight = false;
            }
        });
    }

    // ── glyph atlas ───────────────────────────────────────────────────────────

    // Characters needed per content type.
    private static string AtlasCharset(OverlayContent c) => c switch
    {
        OverlayContent.Time      => "0123456789:",
        OverlayContent.DateTime  => "0123456789:- ",
        OverlayContent.Date      => "0123456789-",
        OverlayContent.IpAddress => "0123456789.",
        _                        => "",
    };

    /// <summary>
    /// Renders each character in the atlas charset once using Android Canvas and stores
    /// the result as YUV arrays.  Called once per dynamic slot at construction time.
    /// All subsequent per-second renders use <see cref="ComposeFromAtlas"/> — no JNI.
    /// </summary>
    private void BuildAtlas(SlotState slot)
    {
        string charset = AtlasCharset(slot.Config.Content);
        if (charset.Length == 0) return;

        float   textSize  = slot.Config.TextSize;
        AGColor textColor = slot.Config.Color.ToAndroid();

        using var paint = new AGPaint(Android.Graphics.PaintFlags.AntiAlias) { TextSize = textSize };
        var m     = paint.GetFontMetrics()!;
        int lineH = (int)Math.Ceiling(m.Bottom - m.Top) + 6;
        float baseline = -m.Top + 3;

        slot.AtlasLineH = lineH;
        slot.Atlas = new Dictionary<char, GlyphData>(charset.Length);

        foreach (char c in charset)
        {
            if (slot.Atlas.ContainsKey(c)) continue;

            string s    = c.ToString();
            int    charW = Math.Max((int)Math.Ceiling(paint.MeasureText(s)) + 3, 4);

            using var bmp    = AGBitmap.CreateBitmap(charW, lineH, AGBitmap.Config.Argb8888!)!;
            using var canvas = new AGCanvas(bmp);
            bmp.EraseColor(0);

            paint.Color = AGColor.Black;
            canvas.DrawText(s, 2f, baseline + 1, paint);   // shadow
            paint.Color = textColor;
            canvas.DrawText(s, 1f, baseline, paint);        // text

            int   total = charW * lineH;
            var   argb  = new int[total];
            bmp.GetPixels(argb, 0, charW, 0, 0, charW, lineH);

            slot.Atlas[c] = new GlyphData(ArgbToY(argb, total), ArgbToCb(argb, total),
                                          ArgbToCr(argb, total), charW, lineH);
        }
    }

    /// <summary>
    /// Composes a text string from the pre-rendered glyph atlas.
    /// Pure managed code — no JNI calls, no Java heap allocation.
    /// </summary>
    private static (byte[] y, byte[] cb, byte[] cr, int w, int h) ComposeFromAtlas(
        SlotState slot, string text)
    {
        var atlas = slot.Atlas!;
        int lineH = slot.AtlasLineH;

        // Measure total width.
        int totalW = 0;
        foreach (char c in text)
            totalW += atlas.TryGetValue(c, out var g) ? g.W : 8;
        if (totalW == 0) totalW = 1;

        var yArr  = new byte[totalW * lineH];
        var cbArr = new byte[totalW * lineH];
        var crArr = new byte[totalW * lineH];

        int curX = 0;
        foreach (char c in text)
        {
            if (!atlas.TryGetValue(c, out var g)) { curX += 8; continue; }

            for (int row = 0; row < g.H; row++)
            {
                int srcBase = row * g.W;
                int dstBase = row * totalW + curX;
                for (int col = 0; col < g.W; col++)
                {
                    byte y = g.Y[srcBase + col];
                    if (y == 0) continue;
                    yArr [dstBase + col] = y;
                    cbArr[dstBase + col] = g.Cb[srcBase + col];
                    crArr[dstBase + col] = g.Cr[srcBase + col];
                }
            }
            curX += g.W;
        }

        return (yArr, cbArr, crArr, totalW, lineH);
    }

    // ── text resolution ───────────────────────────────────────────────────────

    private static string ResolveText(SlotState slot, System.DateTime now) =>
        slot.Config.Content switch
        {
            OverlayContent.DeviceName => GetDeviceName(),
            OverlayContent.IpAddress  => GetIpAddress(),
            OverlayContent.DateTime   => now.ToString("yyyy-MM-dd HH:mm:ss"),
            OverlayContent.Date       => now.ToString("yyyy-MM-dd"),
            OverlayContent.Time       => now.ToString("HH:mm:ss"),
            OverlayContent.Custom     => slot.Config.CustomText ?? "",
            _                          => "",
        };

    private static string GetDeviceName()
    {
        try { return $"{Android.OS.Build.Manufacturer} {Android.OS.Build.Model}".Trim(); }
        catch { return "Unknown Device"; }
    }

    private static string GetIpAddress()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                         && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                ?.Address.ToString() ?? "—";
        }
        catch { return "—"; }
    }

    // ── JNI rendering — used for initial/static snapshots only ───────────────

    private void RenderSync(SlotState slot, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var (y, cb, cr, w, h) = RenderIntoSlot(slot, text, slot.Config.Color.ToAndroid());
        var (posX, posY)      = ComputePosition(slot.Config.Anchor, slot.Config.MarginX, slot.Config.MarginY, w, h);
        slot.Snap = new RenderSnapshot(y, cb, cr, w, h, posX, posY);
    }

    private (byte[] y, byte[] cb, byte[] cr, int w, int h) RenderIntoSlot(
        SlotState slot, string text, AGColor textColor)
    {
        float textSize = slot.Config.TextSize;

        if (slot.RenderPaint == null || Math.Abs(slot.RenderPaint.TextSize - textSize) > 0.1f)
        {
            slot.RenderPaint?.Dispose();
            slot.RenderPaint = new AGPaint(Android.Graphics.PaintFlags.AntiAlias) { TextSize = textSize };
        }

        var paint   = slot.RenderPaint;
        var metrics = paint.GetFontMetrics()!;
        int bmpW = Math.Min((int)Math.Ceiling(paint.MeasureText(text)) + 12, _frameW);
        int bmpH = (int)Math.Ceiling(metrics.Bottom - metrics.Top) + 6;
        slot.RenderBaseline = -metrics.Top + 3;

        if (slot.RenderBitmap == null || slot.RenderBmpW != bmpW || slot.RenderBmpH != bmpH)
        {
            slot.RenderCanvas?.Dispose();
            slot.RenderBitmap?.Recycle();
            slot.RenderBitmap?.Dispose();
            slot.RenderBitmap = AGBitmap.CreateBitmap(bmpW, bmpH, AGBitmap.Config.Argb8888!)!;
            slot.RenderCanvas = new AGCanvas(slot.RenderBitmap);
            slot.RenderBmpW   = bmpW;
            slot.RenderBmpH   = bmpH;
        }

        slot.RenderBitmap.EraseColor(0);

        float baseline = slot.RenderBaseline;
        paint.Color = AGColor.Black;
        slot.RenderCanvas!.DrawText(text, 5 + 1, baseline + 1, paint);
        paint.Color = textColor;
        slot.RenderCanvas.DrawText(text, 5, baseline, paint);

        int total = bmpW * bmpH;
        if (slot.RenderArgb == null || slot.RenderArgb.Length < total)
            slot.RenderArgb = new int[total];
        slot.RenderBitmap.GetPixels(slot.RenderArgb, 0, bmpW, 0, 0, bmpW, bmpH);

        return (ArgbToY(slot.RenderArgb, total), ArgbToCb(slot.RenderArgb, total),
                ArgbToCr(slot.RenderArgb, total), bmpW, bmpH);
    }

    // ── ARGB → YUV conversion helpers (BT.601 limited range) ────────────────

    private static byte[] ArgbToY(int[] argb, int count)
    {
        var arr = new byte[count];
        for (int i = 0; i < count; i++)
        {
            int px = argb[i];
            if (((px >> 24) & 0xFF) < 64) continue;
            int r = (px >> 16) & 0xFF, g = (px >> 8) & 0xFF, b = px & 0xFF;
            arr[i] = (byte)Math.Clamp(16 + ((65 * r + 129 * g + 25 * b + 128) >> 8), 16, 235);
        }
        return arr;
    }

    private static byte[] ArgbToCb(int[] argb, int count)
    {
        var arr = new byte[count];
        for (int i = 0; i < count; i++)
        {
            int px = argb[i];
            if (((px >> 24) & 0xFF) < 64) continue;
            int r = (px >> 16) & 0xFF, g = (px >> 8) & 0xFF, b = px & 0xFF;
            arr[i] = (byte)Math.Clamp(128 + ((-38 * r - 74 * g + 112 * b + 128) >> 8), 16, 240);
        }
        return arr;
    }

    private static byte[] ArgbToCr(int[] argb, int count)
    {
        var arr = new byte[count];
        for (int i = 0; i < count; i++)
        {
            int px = argb[i];
            if (((px >> 24) & 0xFF) < 64) continue;
            int r = (px >> 16) & 0xFF, g = (px >> 8) & 0xFF, b = px & 0xFF;
            arr[i] = (byte)Math.Clamp(128 + ((112 * r - 94 * g - 18 * b + 128) >> 8), 16, 240);
        }
        return arr;
    }

    // ── stamping ──────────────────────────────────────────────────────────────

    private static void Stamp(RenderSnapshot snap, byte[] yuv, int stride, int frameH)
    {
        int uvBase = stride * frameH;

        for (int row = 0; row < snap.H; row++)
        {
            int fy        = snap.PosY + row;
            int yRowStart = fy * stride + snap.PosX;
            int pBase     = row * snap.W;

            for (int col = 0; col < snap.W; col++)
            {
                int  pi = pBase + col;
                byte y  = snap.Y[pi];
                if (y == 0) continue;

                yuv[yRowStart + col] = y;

                int uvOff = uvBase + (fy >> 1) * stride + ((snap.PosX + col) & ~1);
                if (uvOff + 1 < yuv.Length)
                {
                    yuv[uvOff]     = snap.Cr[pi];
                    yuv[uvOff + 1] = snap.Cb[pi];
                }
            }
        }
    }

    // ── position helpers ──────────────────────────────────────────────────────

    private (int x, int y) ComputePosition(AnchorPoint anchor, int mx, int my, int bmpW, int bmpH)
    {
        int x = anchor switch
        {
            AnchorPoint.TopLeft    or AnchorPoint.BottomLeft   => mx,
            AnchorPoint.TopCenter  or AnchorPoint.BottomCenter => (_frameW - bmpW) / 2,
            AnchorPoint.TopRight   or AnchorPoint.BottomRight  => _frameW - bmpW - mx,
            AnchorPoint.Absolute                                => mx,
            _                                                   => mx,
        };
        int y = anchor switch
        {
            AnchorPoint.TopLeft   or AnchorPoint.TopCenter   or AnchorPoint.TopRight     => my,
            AnchorPoint.BottomLeft or AnchorPoint.BottomCenter or AnchorPoint.BottomRight => _frameH - bmpH - my,
            AnchorPoint.Absolute                                                           => my,
            _                                                                              => my,
        };
        return (Math.Clamp(x, 0, Math.Max(0, _frameW - bmpW)),
                Math.Clamp(y, 0, Math.Max(0, _frameH - bmpH)));
    }

    // ── disposal ──────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var slot in _slots)
            slot.DisposeRenderResources();
    }
}
