using System.Collections.Concurrent;
using Android.Util;
using BaluMediaServer.Models;
using BaluMediaServer.Services;

namespace BaluMediaServer.RTSP.Streaming;

/// <summary>
/// Manages H.264 encoder lifecycle and frame queues for front and back cameras.
/// Handles encoder start/stop, frame buffering, and SPS/PPS caching.
/// </summary>
public class H264EncoderManager : IH264EncoderManager
{
    private H264Encoder? _h264FrontEncoder;
    private H264Encoder? _h264BackEncoder;
    private readonly object _h264FrontLock = new();
    private readonly object _h264BackLock = new();
    private readonly ConcurrentQueue<H264FrameEventArgs> _h264FrameQueueBack = new();
    private readonly ConcurrentQueue<H264FrameEventArgs> _h264FrameQueueFront = new();
    private int _h264BackEncoderExpectedFrameSize = 0;
    private int _h264FrontEncoderExpectedFrameSize = 0;

    // Global SPS/PPS cache for SDP generation
    private byte[]? _currentSps;
    private byte[]? _currentPps;
    private readonly object _spsPpsLock = new();

    // Maximum frames to buffer before dropping
    private const int MaxH264QueueSize = 10;

    /// <inheritdoc/>
    public event EventHandler<H264FrameEventArgs>? FrameEncoded;

    /// <inheritdoc/>
    public void StartEncoder(int cameraId, int width, int height, int frameSize = 0)
    {
        if (cameraId == 1) // Front camera
        {
            StartFrontEncoder(width, height);
        }
        else // Back camera
        {
            StartBackEncoder(width, height, frameSize);
        }
    }

    private void StartBackEncoder(int width, int height, int actualFrameSize = 0)
    {
        lock (_h264BackLock)
        {
            if (_h264BackEncoder == null)
            {
                try
                {
                    Log.Debug("[EncoderManager]", $"Starting H264 encoder: {width}x{height}");
                    _h264BackEncoder = new H264Encoder(width, height, bitrate: 2000000, frameRate: 25);

                    // Check if encoder fell back to different resolution
                    if (_h264BackEncoder.ActualWidth != width || _h264BackEncoder.ActualHeight != height)
                    {
                        Log.Warn("[EncoderManager]", $"Encoder using {_h264BackEncoder.ActualWidth}x{_h264BackEncoder.ActualHeight} instead of {width}x{height}");
                    }

                    int expectedSize = (_h264BackEncoder.ActualWidth * _h264BackEncoder.ActualHeight * 3) / 2;
                    _h264BackEncoderExpectedFrameSize = actualFrameSize > 0 ? actualFrameSize : expectedSize;

                    _h264BackEncoder.FrameEncoded += OnH264BackFrameEncoded;
                    if (!_h264BackEncoder.Start())
                    {
                        Log.Error("[EncoderManager]", "Encoder failed to start");
                        _h264BackEncoder.Dispose();
                        _h264BackEncoder = null;
                        return;
                    }
                    Log.Debug("[EncoderManager]", $"H264 back encoder started: {_h264BackEncoder.ActualWidth}x{_h264BackEncoder.ActualHeight}");
                }
                catch (Exception ex)
                {
                    Log.Error("[EncoderManager]", $"Failed to start H264 encoder: {ex.Message}");
                    _h264BackEncoder?.Dispose();
                    _h264BackEncoder = null;
                }
            }
        }
    }

    private void StartFrontEncoder(int width, int height)
    {
        lock (_h264FrontLock)
        {
            if (_h264FrontEncoder == null)
            {
                try
                {
                    Log.Debug("[EncoderManager]", $"Starting H264 front encoder: {width}x{height}");
                    _h264FrontEncoder = new H264Encoder(width, height, bitrate: 2000000, frameRate: 25);

                    // Check if encoder fell back to different resolution
                    if (_h264FrontEncoder.ActualWidth != width || _h264FrontEncoder.ActualHeight != height)
                    {
                        Log.Warn("[EncoderManager]", $"Front encoder using {_h264FrontEncoder.ActualWidth}x{_h264FrontEncoder.ActualHeight} instead of {width}x{height}");
                    }

                    int expectedSize = (_h264FrontEncoder.ActualWidth * _h264FrontEncoder.ActualHeight * 3) / 2;
                    _h264FrontEncoderExpectedFrameSize = expectedSize;

                    _h264FrontEncoder.FrameEncoded += OnH264FrontFrameEncoded;
                    if (!_h264FrontEncoder.Start())
                    {
                        Log.Error("[EncoderManager]", "Front encoder failed to start");
                        _h264FrontEncoder.Dispose();
                        _h264FrontEncoder = null;
                        return;
                    }
                    Log.Debug("[EncoderManager]", $"H264 front encoder started: {_h264FrontEncoder.ActualWidth}x{_h264FrontEncoder.ActualHeight}");
                }
                catch (Exception ex)
                {
                    Log.Error("[EncoderManager]", $"Failed to start H264 front encoder: {ex.Message}");
                    _h264FrontEncoder?.Dispose();
                    _h264FrontEncoder = null;
                }
            }
        }
    }

    /// <inheritdoc/>
    public void StopEncoder(int cameraId)
    {
        if (cameraId == 1) // Front camera
        {
            StopFrontEncoder();
        }
        else // Back camera
        {
            StopBackEncoder();
        }
    }

    private void StopBackEncoder()
    {
        lock (_h264BackLock)
        {
            if (_h264BackEncoder != null)
            {
                _h264BackEncoder.FrameEncoded -= OnH264BackFrameEncoded;
                _h264BackEncoder.Stop();
                _h264BackEncoder.Dispose();
                _h264BackEncoder = null;
                _h264BackEncoderExpectedFrameSize = 0;

                // Clear frame queue
                while (_h264FrameQueueBack.TryDequeue(out _)) { }

                Log.Debug("[EncoderManager]", "H264 back encoder stopped");
            }
        }
    }

    private void StopFrontEncoder()
    {
        lock (_h264FrontLock)
        {
            if (_h264FrontEncoder != null)
            {
                _h264FrontEncoder.FrameEncoded -= OnH264FrontFrameEncoded;
                _h264FrontEncoder.Stop();
                _h264FrontEncoder.Dispose();
                _h264FrontEncoder = null;
                _h264FrontEncoderExpectedFrameSize = 0;

                // Clear frame queue
                while (_h264FrameQueueFront.TryDequeue(out _)) { }

                Log.Debug("[EncoderManager]", "H264 front encoder stopped");
            }
        }
    }

    /// <inheritdoc/>
    public void RestartEncoderWithNewSize(int cameraId, int frameSize, int hintWidth, int hintHeight)
    {
        var (width, height) = CalculateDimensionsFromFrameSize(frameSize, hintWidth, hintHeight);
        Log.Info("[EncoderManager]", $"Restarting encoder with new dimensions: {width}x{height}");

        StopEncoder(cameraId);
        StartEncoder(cameraId, width, height);
    }

    /// <inheritdoc/>
    public bool TryDequeueFrame(int cameraId, out H264FrameEventArgs? frame)
    {
        if (cameraId == 1) // Front camera
        {
            return _h264FrameQueueFront.TryDequeue(out frame);
        }
        else // Back camera
        {
            return _h264FrameQueueBack.TryDequeue(out frame);
        }
    }

    /// <inheritdoc/>
    public (byte[]? sps, byte[]? pps) GetSpsPps()
    {
        lock (_spsPpsLock)
        {
            return (_currentSps, _currentPps);
        }
    }

    /// <inheritdoc/>
    public (int width, int height) GetActualResolution(int cameraId)
    {
        if (cameraId == 1) // Front camera
        {
            lock (_h264FrontLock)
            {
                if (_h264FrontEncoder != null)
                {
                    return (_h264FrontEncoder.ActualWidth, _h264FrontEncoder.ActualHeight);
                }
            }
        }
        else // Back camera
        {
            lock (_h264BackLock)
            {
                if (_h264BackEncoder != null)
                {
                    return (_h264BackEncoder.ActualWidth, _h264BackEncoder.ActualHeight);
                }
            }
        }
        return (0, 0);
    }

    /// <inheritdoc/>
    public void FeedFrame(int cameraId, FrameEventArgs frame)
    {
        if (cameraId == 1) // Front camera
        {
            _h264FrontEncoder?.FeedInputBuffer(new H264Encoder.FrameData { Data = frame.Data, Timestamp = frame.Timestamp });
        }
        else // Back camera
        {
            _h264BackEncoder?.FeedInputBuffer(new H264Encoder.FrameData { Data = frame.Data, Timestamp = frame.Timestamp });
        }
    }

    /// <inheritdoc/>
    public void UpdateBitrate(int cameraId, int bitrate)
    {
        if (cameraId == 1) // Front camera
        {
            _h264FrontEncoder?.UpdateBitrate(bitrate);
        }
        else // Back camera
        {
            _h264BackEncoder?.UpdateBitrate(bitrate);
        }
    }

    /// <inheritdoc/>
    public int GetExpectedFrameSize(int cameraId)
    {
        if (cameraId == 1) // Front camera
        {
            return _h264FrontEncoderExpectedFrameSize;
        }
        return _h264BackEncoderExpectedFrameSize;
    }

    /// <summary>
    /// Clears the SPS/PPS cache.
    /// </summary>
    public void ClearSpsPps()
    {
        lock (_spsPpsLock)
        {
            _currentSps = null;
            _currentPps = null;
        }
    }

    private void OnH264FrontFrameEncoded(object? sender, H264FrameEventArgs e)
    {
        // Update global SPS/PPS cache for SDP generation
        UpdateSpsPpsCache(e);

        // Add to queue with smart dropping
        _h264FrameQueueFront.Enqueue(e);
        while (_h264FrameQueueFront.Count > MaxH264QueueSize)
        {
            if (!TryDropNonKeyFrame(_h264FrameQueueFront))
            {
                _h264FrameQueueFront.TryDequeue(out _);
            }
        }

        FrameEncoded?.Invoke(this, e);
    }

    private void OnH264BackFrameEncoded(object? sender, H264FrameEventArgs e)
    {
        // Update global SPS/PPS cache for SDP generation
        UpdateSpsPpsCache(e);

        // Add to queue with smart dropping
        _h264FrameQueueBack.Enqueue(e);
        while (_h264FrameQueueBack.Count > MaxH264QueueSize)
        {
            if (!TryDropNonKeyFrame(_h264FrameQueueBack))
            {
                _h264FrameQueueBack.TryDequeue(out _);
            }
        }

        FrameEncoded?.Invoke(this, e);
    }

    private void UpdateSpsPpsCache(H264FrameEventArgs e)
    {
        if (e.Sps != null || e.Pps != null)
        {
            lock (_spsPpsLock)
            {
                if (e.Sps != null) _currentSps = e.Sps;
                if (e.Pps != null) _currentPps = e.Pps;
            }
        }
    }

    /// <summary>
    /// Tries to drop the newest non-keyframe from the queue.
    /// </summary>
    private static bool TryDropNonKeyFrame(ConcurrentQueue<H264FrameEventArgs> queue)
    {
        var frames = new List<H264FrameEventArgs>();
        while (queue.TryDequeue(out var frame))
        {
            frames.Add(frame);
        }

        // Find LAST (newest) non-keyframe to drop
        int dropIndex = frames.FindLastIndex(f => !f.IsKeyFrame);
        if (dropIndex >= 0)
        {
            frames.RemoveAt(dropIndex);
            foreach (var f in frames) queue.Enqueue(f);
            Log.Debug("[EncoderManager]", "Dropped newest non-keyframe to maintain queue depth");
            return true;
        }

        // No non-keyframe found, restore queue
        foreach (var f in frames) queue.Enqueue(f);
        return false;
    }

    /// <summary>
    /// Calculates video dimensions from YUV420 frame size.
    /// </summary>
    private static (int width, int height) CalculateDimensionsFromFrameSize(int frameSize, int hintWidth, int hintHeight)
    {
        const int tolerance = 64;

        // Common resolutions to check (YUV420 sizes)
        var commonResolutions = new (int w, int h)[]
        {
            (640, 640),
            (640, 480),
            (720, 480),
            (800, 600),
            (1280, 720),
            (1280, 960),
            (1920, 1080),
            (320, 240),
            (480, 480),
            (480, 640),
            (720, 720),
            (768, 768),
            (960, 540),
        };

        // Check common resolutions first
        foreach (var (w, h) in commonResolutions)
        {
            int expectedSize = (w * h * 3) / 2;
            if (Math.Abs(frameSize - expectedSize) <= tolerance)
            {
                return (w, h);
            }
        }

        // Try to calculate using the hint width
        if (hintWidth > 0)
        {
            int calculatedHeight = (frameSize * 2) / (hintWidth * 3);
            calculatedHeight = ((calculatedHeight + 8) / 16) * 16;
            int checkSize = (hintWidth * calculatedHeight * 3) / 2;
            if (Math.Abs(checkSize - frameSize) <= tolerance && calculatedHeight > 0)
            {
                return (hintWidth, calculatedHeight);
            }
        }

        // Try to calculate using the hint height
        if (hintHeight > 0)
        {
            int calculatedWidth = (frameSize * 2) / (hintHeight * 3);
            calculatedWidth = ((calculatedWidth + 8) / 16) * 16;
            int checkSize = (calculatedWidth * hintHeight * 3) / 2;
            if (Math.Abs(checkSize - frameSize) <= tolerance && calculatedWidth > 0)
            {
                return (calculatedWidth, hintHeight);
            }
        }

        // Last resort: assume square
        double pixels = frameSize / 1.5;
        int side = (int)Math.Sqrt(pixels);
        side = ((side + 8) / 16) * 16;
        if (side > 0)
        {
            int checkSize = (side * side * 3) / 2;
            if (Math.Abs(checkSize - frameSize) <= tolerance)
            {
                return (side, side);
            }
        }

        // Fallback
        return (hintWidth, hintHeight);
    }
}
