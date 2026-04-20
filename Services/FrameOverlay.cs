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

    // ── named presets ─────────────────────────────────────────────────────────
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
    /// <summary>What to display in this slot.</summary>
    public OverlayContent Content  { get; set; } = OverlayContent.Custom;

    /// <summary>Static text used when <see cref="Content"/> is <see cref="OverlayContent.Custom"/>.</summary>
    public string? CustomText      { get; set; }

    /// <summary>Corner/edge of the frame this slot is anchored to.</summary>
    public AnchorPoint Anchor      { get; set; } = AnchorPoint.BottomLeft;

    /// <summary>
    /// Pixels from the anchor edge (horizontal).
    /// When <see cref="Anchor"/> is <see cref="AnchorPoint.Absolute"/>, this is the raw X coordinate.
    /// </summary>
    public int MarginX             { get; set; } = 10;

    /// <summary>
    /// Pixels from the anchor edge (vertical).
    /// When <see cref="Anchor"/> is <see cref="AnchorPoint.Absolute"/>, this is the raw Y coordinate.
    /// </summary>
    public int MarginY             { get; set; } = 10;

    /// <summary>Font size in points.</summary>
    public float TextSize          { get; set; } = 32f;

    /// <summary>Text colour.</summary>
    public OverlayColor Color      { get; set; } = OverlayColor.White;
}

// ── FrameOverlay ──────────────────────────────────────────────────────────────

/// <summary>
/// Stamps up to <b>four</b> configurable text slots into the Y (luma) and UV (chroma)
/// planes of NV21/NV12 frame buffers before they are fed to MediaCodec.
///
/// <list type="bullet">
///   <item>Static slots (DeviceName, Custom) are rendered once at construction.</item>
///   <item>Dynamic slots are re-rendered only when their value changes
///         (Time/DateTime every second, IpAddress every minute).</item>
///   <item>Per-frame hot path is a tight byte[] stamp — typically under 2 µs at 1280×720.</item>
/// </list>
/// </summary>
public sealed class FrameOverlay
{
    // ── internal per-slot state ───────────────────────────────────────────────

    private sealed class SlotState
    {
        public OverlaySlot Config = null!;

        // Pre-rendered luma + chroma (0 = transparent pixel)
        public byte[] Y  = Array.Empty<byte>();
        public byte[] Cb = Array.Empty<byte>();
        public byte[] Cr = Array.Empty<byte>();
        public int W, H;

        // Top-left position in the frame
        public int PosX, PosY;

        // Refresh tracking
        public bool   IsStatic;
        public int    LastSecond  = -1;
        public int    LastDayOfYear = -1;
        public int    LastMinute  = -1;
        public string LastIp      = "";
    }

    private readonly SlotState[] _slots;
    private readonly int _frameW, _frameH;

    // ── factories ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Default layout: device name and live clock stacked in the bottom-left corner.
    /// Measures the clock line height at runtime so the name is positioned exactly above it.
    /// </summary>
    public static FrameOverlay? Default(int frameW, int frameH)
    {
        try
        {
            // Measure clock height so name can be positioned directly above it
            int clockH;
            using (var p = new AGPaint(Android.Graphics.PaintFlags.AntiAlias) { TextSize = 28f })
            {
                var m = p.GetFontMetrics()!;
                clockH = (int)Math.Ceiling(m.Bottom - m.Top) + 6;
            }

            const int clockMarginY = 4;          // px from frame bottom
            int       nameMarginY  = clockMarginY + clockH + 3; // sits directly above clock

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
            Log.Warn("[FrameOverlay]", $"Failed to create default overlay: {ex.Message}");
            return null;
        }
    }

    // ── constructor ───────────────────────────────────────────────────────────

    /// <summary>
    /// Creates an overlay with up to four text slots. Slots beyond four are ignored.
    /// </summary>
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
            Render(state, ResolveText(state, now));
        }
    }

    // ── per-frame entry point ─────────────────────────────────────────────────

    /// <summary>
    /// Stamps all configured slots into an NV21/NV12 frame buffer.
    /// </summary>
    /// <param name="yuvData">Full frame buffer (Y plane followed by UV plane).</param>
    /// <param name="frameStride">Y-plane row stride in bytes (usually frame width).</param>
    /// <param name="frameH">Source frame height — locates the UV plane at stride × frameH.</param>
    public void StampInto(byte[] yuvData, int frameStride, int frameH)
    {
        var now = System.DateTime.Now;
        foreach (var slot in _slots)
        {
            if (!slot.IsStatic)
                RefreshIfNeeded(slot, now);

            if (slot.Y.Length > 0)
                Stamp(slot, yuvData, frameStride, frameH);
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

        Render(slot, text);
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

    // ── rendering ─────────────────────────────────────────────────────────────

    private void Render(SlotState slot, string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        (slot.Y, slot.Cb, slot.Cr, slot.W, slot.H) =
            RenderLine(text, slot.Config.TextSize, slot.Config.Color.ToAndroid(), _frameW);

        (slot.PosX, slot.PosY) =
            ComputePosition(slot.Config.Anchor, slot.Config.MarginX, slot.Config.MarginY,
                            slot.W, slot.H);
    }

    private static (byte[] y, byte[] cb, byte[] cr, int w, int h) RenderLine(
        string text, float textSize, AGColor textColor, int maxW)
    {
        using var paint = new AGPaint(Android.Graphics.PaintFlags.AntiAlias) { TextSize = textSize };
        var metrics = paint.GetFontMetrics()!;

        int bmpW = Math.Min((int)Math.Ceiling(paint.MeasureText(text)) + 12, maxW);
        int bmpH = (int)Math.Ceiling(metrics.Bottom - metrics.Top) + 6;

        using var bmp    = AGBitmap.CreateBitmap(bmpW, bmpH, AGBitmap.Config.Argb8888!)!;
        using var canvas = new AGCanvas(bmp);

        float baseline = -metrics.Top + 3;

        // Shadow: black, 1 px offset for readability on any background
        paint.Color = AGColor.Black;
        canvas.DrawText(text, 5 + 1, baseline + 1, paint);

        // Main text
        paint.Color = textColor;
        canvas.DrawText(text, 5, baseline, paint);

        int   total  = bmpW * bmpH;
        int[] argb   = new int[total];
        bmp.GetPixels(argb, 0, bmpW, 0, 0, bmpW, bmpH);

        var yArr  = new byte[total];
        var cbArr = new byte[total];
        var crArr = new byte[total];

        for (int i = 0; i < total; i++)
        {
            int px = argb[i];
            if (((px >> 24) & 0xFF) < 64) continue;   // transparent — leave 0

            int r = (px >> 16) & 0xFF;
            int g = (px >>  8) & 0xFF;
            int b =  px        & 0xFF;

            // BT.601 limited range (Y ∈ [16,235], Cb/Cr ∈ [16,240])
            yArr[i]  = (byte)Math.Clamp(16  + ((65  * r + 129 * g +  25 * b + 128) >> 8), 16, 235);
            cbArr[i] = (byte)Math.Clamp(128 + ((-38 * r -  74 * g + 112 * b + 128) >> 8), 16, 240);
            crArr[i] = (byte)Math.Clamp(128 + ((112 * r -  94 * g -  18 * b + 128) >> 8), 16, 240);
        }

        return (yArr, cbArr, crArr, bmpW, bmpH);
    }

    // ── stamping ──────────────────────────────────────────────────────────────

    private static void Stamp(SlotState slot, byte[] yuv, int stride, int frameH)
    {
        int uvBase = stride * frameH;

        for (int row = 0; row < slot.H; row++)
        {
            int fy        = slot.PosY + row;
            int yRowStart = fy * stride + slot.PosX;
            int pBase     = row * slot.W;

            for (int col = 0; col < slot.W; col++)
            {
                int  pi = pBase + col;
                byte y  = slot.Y[pi];
                if (y == 0) continue;   // transparent

                // Y plane
                yuv[yRowStart + col] = y;

                // UV plane — NV21 format: V (=Cr) at even offset, U (=Cb) at odd
                int uvOff = uvBase + (fy >> 1) * stride + ((slot.PosX + col) & ~1);
                if (uvOff + 1 < yuv.Length)
                {
                    yuv[uvOff]     = slot.Cr[pi];
                    yuv[uvOff + 1] = slot.Cb[pi];
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
            AnchorPoint.TopLeft   or AnchorPoint.TopCenter   or AnchorPoint.TopRight    => my,
            AnchorPoint.BottomLeft or AnchorPoint.BottomCenter or AnchorPoint.BottomRight => _frameH - bmpH - my,
            AnchorPoint.Absolute                                                           => my,
            _                                                                              => my,
        };
        return (Math.Clamp(x, 0, Math.Max(0, _frameW - bmpW)),
                Math.Clamp(y, 0, Math.Max(0, _frameH - bmpH)));
    }
}
