using System.Diagnostics;
using BaluMediaServer.Models;

namespace BaluMediaServer.Services;

/// <summary>
/// In-place, zero-allocation post-processing of NV21 camera frames to reduce visible
/// noise and camera shake <b>before</b> the frame reaches the H.264 encoder. Modelled on
/// <see cref="FrameOverlay"/>: one instance per camera, lazy-sized on the first frame,
/// reused scratch buffers (no steady-state GC), and a tight per-frame hot path.
///
/// <para>Two cooperating stages, both operating only on the bytes the AAR hands back —
/// nothing here touches the native camera library:</para>
/// <list type="number">
///   <item><b>Motion-adaptive temporal denoise.</b> Each byte is blended toward its value
///   in the previous (denoised) frame when the inter-frame delta is below a threshold
///   (static background → smoothed, removing sensor noise), and passed through unchanged
///   when the delta is large (moving content → no ghosting). Killing sensor noise also
///   lowers H.264 bitrate/blocking, because noise is high-entropy data the encoder would
///   otherwise waste bits on.</item>
///   <item><b>Translation-only digital stabilization.</b> Global inter-frame motion is
///   estimated from integral projections of a downscaled luma plane, the camera trajectory
///   is low-pass filtered to separate jitter from intentional panning, and the residual
///   jitter is cancelled by integer-pixel shifting the frame within a small crop margin
///   (border-replicated). No rotation or feature matching — too heavy for this SoC.</item>
/// </list>
///
/// <para><b>Conservative by design.</b> The whole module is opt-in
/// (<see cref="VideoStabilizationOptions.Enabled"/> defaults to <c>false</c>). Each call is
/// timed; if processing exceeds the per-frame budget for a sustained run the module
/// self-disables so the encoder framerate can never silently regress.</para>
/// </summary>
public sealed class FrameStabilizer
{
    private readonly VideoStabilizationOptions _opt;

    // ── lazily-sized state (allocated once per resolution) ──────────────────────
    private bool _initialized;
    private int _w, _h, _total;

    // Denoise: previous (denoised) full NV21 buffer, fed back recursively.
    private byte[]? _prevFull;

    // Stabilization scratch + downscaled-luma projection state.
    private byte[]? _scratch;          // full NV21, shift destination
    private int _scale;                // luma downscale factor for motion estimation
    private int _dw, _dh;              // downscaled dimensions
    private byte[]? _downY;            // current downscaled luma
    private int[]? _colSum, _rowSum;   // current projections
    private int[]? _prevColSum, _prevRowSum;
    private bool _haveProjections;
    private int _maxShiftPx, _maxShiftDown;

    // Smoothed-trajectory state (full-resolution pixel units).
    private double _accX, _accY;       // accumulated raw camera path
    private double _smX, _smY;         // low-pass filtered (smooth) path

    private bool _havePrevFrame;

    // Tuning, resolved from options once.
    private short _denoiseThr;
    private short _denoiseAlphaQ7;
    private double _smoothing;

    // ── auto-disable guard ──────────────────────────────────────────────────────
    private readonly Stopwatch _sw = new();
    private int _overBudgetStreak;
    private volatile bool _autoDisabled;
    private bool _loggedDisable;

    /// <summary>Whether the module is currently doing any work.</summary>
    public bool IsActive => _opt.Enabled && !_autoDisabled
                            && (_opt.DenoiseEnabled || _opt.StabilizationEnabled);

    public FrameStabilizer(VideoStabilizationOptions options)
    {
        _opt = options ?? new VideoStabilizationOptions();
    }

    // ── per-frame entry point ─────────────────────────────────────────────────

    /// <summary>
    /// Denoises and/or stabilizes an NV21 frame in place. Safe to call on every frame;
    /// returns immediately when disabled. <paramref name="data"/> is mutated directly.
    /// </summary>
    /// <param name="data">NV21 buffer: <c>width*height</c> luma followed by VU-interleaved chroma.</param>
    /// <param name="width">Frame width in pixels (also the row stride).</param>
    /// <param name="height">Frame height in pixels.</param>
    public void Process(byte[] data, int width, int height)
    {
        if (!_opt.Enabled || _autoDisabled || data == null) return;
        if (width <= 0 || height <= 0) return;

        int expected = width * height * 3 / 2;
        if (data.Length < expected) return; // not a frame we understand — skip safely

        try
        {
            if (!_initialized || _w != width || _h != height)
                Init(width, height, data.Length);

            _sw.Restart();

            if (_opt.DenoiseEnabled)
                Denoise(data);

            if (_opt.StabilizationEnabled)
                Stabilize(data);

            _havePrevFrame = true;

            _sw.Stop();
            GuardBudget(_sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            // A processing fault must never break the stream — disable and pass the
            // raw frame through untouched from here on.
            _autoDisabled = true;
            BaluLogger.Warn("[FrameStabilizer]", $"Disabled after error: {ex.Message}");
        }
    }

    // ── initialization ──────────────────────────────────────────────────────────

    private void Init(int width, int height, int bufferLength)
    {
        _w = width;
        _h = height;
        _total = bufferLength;

        _denoiseThr = (short)Math.Clamp(_opt.DenoiseThreshold, 1, 64);
        _denoiseAlphaQ7 = (short)Math.Clamp((int)Math.Round(_opt.DenoiseStrength * 128.0), 0, 128);
        _smoothing = Math.Clamp(_opt.SmoothingFactor, 0.0, 0.98);

        _prevFull = _opt.DenoiseEnabled ? new byte[_total] : null;

        if (_opt.StabilizationEnabled)
        {
            _scratch = new byte[_total];
            // Downscale luma so an integral-projection match stays cheap: aim for ~320px wide.
            _scale = Math.Max(1, (int)Math.Round(width / 320.0));
            _dw = Math.Max(1, width / _scale);
            _dh = Math.Max(1, height / _scale);
            _downY = new byte[_dw * _dh];
            _colSum = new int[_dw];
            _rowSum = new int[_dh];
            _prevColSum = new int[_dw];
            _prevRowSum = new int[_dh];
            _maxShiftPx = Math.Max(1, (int)Math.Round(width * _opt.MaxShiftPercent / 100.0));
            _maxShiftDown = Math.Max(1, _maxShiftPx / _scale);
        }

        _accX = _accY = _smX = _smY = 0;
        _haveProjections = false;
        _havePrevFrame = false;
        _overBudgetStreak = 0;
        _initialized = true;

        BaluLogger.Info("[FrameStabilizer]",
            $"Init {width}x{height} denoise={_opt.DenoiseEnabled}(thr={_denoiseThr},aQ7={_denoiseAlphaQ7}) " +
            $"stab={_opt.StabilizationEnabled}(scale={_scale},maxShift={_maxShiftPx}px,smooth={_smoothing:0.00})");
    }

    // ── stage 1: motion-adaptive temporal denoise ──────────────────────────────

    /// <summary>
    /// Temporal denoise over the whole NV21 buffer (luma and chroma alike). The previous
    /// buffer is fed back recursively; the first frame just seeds history.
    /// </summary>
    private void Denoise(byte[] data)
    {
        var prev = _prevFull!;
        if (!_havePrevFrame)
        {
            Array.Copy(data, prev, _total);   // seed history, no blend on first frame
            return;
        }
        FrameStabilizerMath.Denoise(data, prev, _total, _denoiseThr, _denoiseAlphaQ7);
    }

    // ── stage 2: translation-only digital stabilization ────────────────────────

    private void Stabilize(byte[] data)
    {
        FrameStabilizerMath.DownscaleLuma(data, _downY!, _w, _dw, _dh, _scale);
        FrameStabilizerMath.ComputeProjections(_downY!, _colSum!, _rowSum!, _dw, _dh);

        if (_haveProjections)
        {
            // d minimizing |cur[i]-prev[i+d]| means current content sits at prev[i+d],
            // i.e. content moved by -d (downscaled). Scale back to full-res pixels.
            int dCol = FrameStabilizerMath.BestShift1D(_colSum!, _prevColSum!, _dw, _maxShiftDown);
            int dRow = FrameStabilizerMath.BestShift1D(_rowSum!, _prevRowSum!, _dh, _maxShiftDown);
            double measuredDx = -dCol * (double)_scale;
            double measuredDy = -dRow * (double)_scale;

            // Accumulate the raw path and low-pass it; cancel the residual jitter.
            _accX += measuredDx;
            _accY += measuredDy;
            _smX = _smoothing * _smX + (1.0 - _smoothing) * _accX;
            _smY = _smoothing * _smY + (1.0 - _smoothing) * _accY;

            int corrX = (int)Math.Round(_smX - _accX);
            int corrY = (int)Math.Round(_smY - _accY);
            corrX = Math.Clamp(corrX, -_maxShiftPx, _maxShiftPx);
            corrY = Math.Clamp(corrY, -_maxShiftPx, _maxShiftPx);

            if (corrX != 0 || corrY != 0)
            {
                FrameStabilizerMath.ShiftNv21(data, _scratch!, _w, _h, corrX, corrY);
                Array.Copy(_scratch!, data, _total);
            }
        }

        // Current projections become the reference for the next frame.
        (_prevColSum, _colSum) = (_colSum, _prevColSum);
        (_prevRowSum, _rowSum) = (_rowSum, _prevRowSum);
        _haveProjections = true;
    }

    // ── auto-disable guard ──────────────────────────────────────────────────────

    private void GuardBudget(double elapsedMs)
    {
        if (elapsedMs > _opt.FrameBudgetMs)
        {
            if (++_overBudgetStreak >= _opt.OverBudgetFramesToDisable)
            {
                _autoDisabled = true;
                if (!_loggedDisable)
                {
                    _loggedDisable = true;
                    BaluLogger.Warn("[FrameStabilizer]",
                        $"Auto-disabled: {_overBudgetStreak} consecutive frames over " +
                        $"{_opt.FrameBudgetMs:0.0}ms budget (last {elapsedMs:0.0}ms). Passing frames through untouched.");
                }
            }
        }
        else
        {
            _overBudgetStreak = 0;
        }
    }
}
