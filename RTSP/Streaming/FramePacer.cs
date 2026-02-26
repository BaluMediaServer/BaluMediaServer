using System.Diagnostics;
using Android.Util;

namespace BaluMediaServer.Services;

/// <summary>
/// Controls frame delivery timing for smooth H264 streaming.
/// Ensures frames are sent at consistent intervals matching target FPS,
/// preventing bursts that can cause playback stuttering.
/// </summary>
public class FramePacer
{
    private readonly int _targetFps;
    private readonly double _frameIntervalMs;
    private readonly Stopwatch _stopwatch = new();
    private long _lastFrameSentTicks;
    private int _consecutiveDrops;
    private readonly int _maxConsecutiveDrops;

    /// <summary>
    /// Creates a new frame pacer.
    /// </summary>
    /// <param name="targetFps">Target frames per second (default 25)</param>
    /// <param name="maxConsecutiveDrops">Maximum P-frames to drop in a row (default 3)</param>
    public FramePacer(int targetFps = 25, int maxConsecutiveDrops = 3)
    {
        _targetFps = targetFps;
        _frameIntervalMs = 1000.0 / targetFps;
        _maxConsecutiveDrops = maxConsecutiveDrops;
        _stopwatch.Start();
        _lastFrameSentTicks = _stopwatch.ElapsedTicks;
    }

    /// <summary>
    /// Calculates the delay needed before sending the next frame.
    /// Returns 0 if frame should be sent immediately.
    /// </summary>
    /// <returns>Milliseconds to wait, or 0 for immediate send</returns>
    public int GetDelayForNextFrame()
    {
        long currentTicks = _stopwatch.ElapsedTicks;
        double elapsedMs = (currentTicks - _lastFrameSentTicks) * 1000.0 / Stopwatch.Frequency;
        double delayMs = _frameIntervalMs - elapsedMs;

        // Only delay if significant (>1ms), otherwise send immediately
        return delayMs > 1 ? (int)delayMs : 0;
    }

    /// <summary>
    /// Marks a frame as sent and updates the timing state.
    /// Call this after successfully sending a frame.
    /// </summary>
    public void MarkFrameSent()
    {
        _lastFrameSentTicks = _stopwatch.ElapsedTicks;
        _consecutiveDrops = 0;
    }

    /// <summary>
    /// Determines if a frame should be dropped based on timing drift.
    /// Never drops keyframes. Limits consecutive drops to prevent quality degradation.
    /// </summary>
    /// <param name="isKeyFrame">True if this is an IDR/keyframe</param>
    /// <returns>True if the frame should be dropped</returns>
    public bool ShouldDropFrame(bool isKeyFrame)
    {
        // Never drop keyframes - they're essential for decoder sync
        if (isKeyFrame) return false;

        // Don't drop too many frames in a row
        if (_consecutiveDrops >= _maxConsecutiveDrops)
            return false;

        long currentTicks = _stopwatch.ElapsedTicks;
        double elapsedMs = (currentTicks - _lastFrameSentTicks) * 1000.0 / Stopwatch.Frequency;

        // Drop frames arriving too fast (less than half a frame interval since last send).
        // This thins out bursts without creating gaps after stalls.
        return elapsedMs < _frameIntervalMs * 0.5;
    }

    /// <summary>
    /// Records that a frame was dropped. Call when ShouldDropFrame returns true.
    /// </summary>
    public void RecordDrop()
    {
        _consecutiveDrops++;
        Log.Debug("FramePacer", $"Dropped frame (consecutive: {_consecutiveDrops})");
    }

    /// <summary>
    /// Resets the pacer state. Call when streaming restarts.
    /// </summary>
    public void Reset()
    {
        _stopwatch.Restart();
        _lastFrameSentTicks = _stopwatch.ElapsedTicks;
        _consecutiveDrops = 0;
    }

    /// <summary>
    /// Gets the target frame interval in milliseconds.
    /// </summary>
    public double FrameIntervalMs => _frameIntervalMs;

    /// <summary>
    /// Gets the target frames per second.
    /// </summary>
    public int TargetFps => _targetFps;
}
