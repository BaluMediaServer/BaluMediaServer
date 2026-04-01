using System.Threading.Channels;
using Android.Util;
using BaluMediaServer.Models;
using BaluMediaServer.Services;

namespace BaluMediaServer.RTSP.Streaming;

/// <summary>
/// Manages H.264 encoder lifecycle and per-client frame channels for front and back cameras.
/// Uses a fan-out model: each encoded frame is written to every registered client's own channel,
/// so all clients receive every frame independently (no round-robin starvation).
/// </summary>
public class H264EncoderManager : IH264EncoderManager
{
    private H264Encoder? _h264FrontEncoder;
    private H264Encoder? _h264BackEncoder;
    private readonly object _h264FrontLock = new();
    private readonly object _h264BackLock = new();

    // Per-client channels: one bounded channel per registered client per camera.
    // The encoder fan-out writes each frame into every client's channel simultaneously,
    // so N clients each receive all frames rather than competing for 1/N of them.
    private readonly Dictionary<string, Channel<H264FrameEventArgs>> _backClientChannels = new();
    private readonly Dictionary<string, Channel<H264FrameEventArgs>> _frontClientChannels = new();
    private readonly object _backClientChannelsLock = new();
    private readonly object _frontClientChannelsLock = new();

    private int _h264BackEncoderExpectedFrameSize = 0;
    private int _h264FrontEncoderExpectedFrameSize = 0;

    // Global SPS/PPS cache for SDP generation
    private byte[]? _currentSps;
    private byte[]? _currentPps;
    private readonly object _spsPpsLock = new();

    // Per-client buffer: 3 frames at 25fps = 120ms max buffering delay.
    // DropOldest ensures slow clients never stall the fan-out loop.
    private const int MaxH264QueueSize = 3;

    /// <inheritdoc/>
    public event EventHandler<H264FrameEventArgs>? FrameEncoded;

    /// <inheritdoc/>
    public void RegisterClientChannel(int cameraId, string clientId)
    {
        var channel = Channel.CreateBounded<H264FrameEventArgs>(
            new BoundedChannelOptions(MaxH264QueueSize)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

        if (cameraId == 1)
        {
            lock (_frontClientChannelsLock) { _frontClientChannels[clientId] = channel; }
        }
        else
        {
            lock (_backClientChannelsLock) { _backClientChannels[clientId] = channel; }
        }
        Log.Debug("[EncoderManager]", $"Registered frame channel for client {clientId} camera {cameraId}");
    }

    /// <inheritdoc/>
    public void UnregisterClientChannel(int cameraId, string clientId)
    {
        Channel<H264FrameEventArgs>? channel = null;

        if (cameraId == 1)
        {
            lock (_frontClientChannelsLock)
            {
                _frontClientChannels.TryGetValue(clientId, out channel);
                _frontClientChannels.Remove(clientId);
            }
        }
        else
        {
            lock (_backClientChannelsLock)
            {
                _backClientChannels.TryGetValue(clientId, out channel);
                _backClientChannels.Remove(clientId);
            }
        }

        // Complete the writer so any awaiting ReadAsync gets ChannelClosedException
        channel?.Writer.TryComplete();
        Log.Debug("[EncoderManager]", $"Unregistered frame channel for client {clientId} camera {cameraId}");
    }

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

    /// <inheritdoc/>
    public bool IsEncoderRunning(int cameraId)
    {
        if (cameraId == 1)
        {
            lock (_h264FrontLock) { return _h264FrontEncoder?.IsRunning == true; }
        }
        lock (_h264BackLock) { return _h264BackEncoder?.IsRunning == true; }
    }

    private void StartBackEncoder(int width, int height, int actualFrameSize = 0)
    {
        lock (_h264BackLock)
        {
            // Restart encoder if it exists but stopped running (stalled/crashed)
            if (_h264BackEncoder != null && !_h264BackEncoder.IsRunning)
            {
                Log.Info("[EncoderManager]", "Back encoder stalled — restarting");
                _h264BackEncoder.FrameEncoded -= OnH264BackFrameEncoded;
                var staleBackEncoder = _h264BackEncoder;
                _h264BackEncoder = null;
                _ = Task.Run(() => { try { staleBackEncoder.Dispose(); } catch { } });

                // Drain stale frames from all registered client channels
                DrainClientChannels(_backClientChannels, _backClientChannelsLock);
            }

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
            // Restart encoder if it exists but stopped running (stalled/crashed)
            if (_h264FrontEncoder != null && !_h264FrontEncoder.IsRunning)
            {
                Log.Info("[EncoderManager]", "Front encoder stalled — restarting");
                _h264FrontEncoder.FrameEncoded -= OnH264FrontFrameEncoded;
                var staleFrontEncoder = _h264FrontEncoder;
                _h264FrontEncoder = null;
                _ = Task.Run(() => { try { staleFrontEncoder.Dispose(); } catch { } });

                // Drain stale frames from all registered client channels
                DrainClientChannels(_frontClientChannels, _frontClientChannelsLock);
            }

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
                Log.Info("[EncoderManager]", "H264 back encoder stopped");
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
                Log.Info("[EncoderManager]", "H264 front encoder stopped");
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
    public bool TryDequeueFrame(int cameraId, string clientId, out H264FrameEventArgs? frame)
    {
        Channel<H264FrameEventArgs>? channel = GetClientChannel(cameraId, clientId);
        if (channel != null)
            return channel.Reader.TryRead(out frame);

        frame = null;
        return false;
    }

    /// <inheritdoc/>
    public async ValueTask<H264FrameEventArgs> DequeueFrameAsync(int cameraId, string clientId, CancellationToken cancellationToken)
    {
        Channel<H264FrameEventArgs>? channel = GetClientChannel(cameraId, clientId);
        if (channel == null)
            throw new ChannelClosedException($"No channel registered for client {clientId} camera {cameraId}");

        return await channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
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
            _h264FrontEncoder?.QueueFrame(frame.Data, frame.Timestamp, frame.Width, frame.Height);
        }
        else // Back camera
        {
            _h264BackEncoder?.QueueFrame(frame.Data, frame.Timestamp, frame.Width, frame.Height);
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

    /// <inheritdoc/>
    public void RequestKeyFrame(int cameraId)
    {
        if (cameraId == 1)
        {
            lock (_h264FrontLock) { _h264FrontEncoder?.RequestKeyFrame(); }
        }
        else
        {
            lock (_h264BackLock) { _h264BackEncoder?.RequestKeyFrame(); }
        }
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
        UpdateSpsPpsCache(e);

        // Fan-out: deliver this frame to every registered front-camera client
        lock (_frontClientChannelsLock)
        {
            foreach (var ch in _frontClientChannels.Values)
                ch.Writer.TryWrite(e);
        }

        try { FrameEncoded?.Invoke(this, e); }
        catch (Exception ex) { Android.Util.Log.Error("H264EncoderManager", $"FrameEncoded subscriber error: {ex.Message}"); }
    }

    private void OnH264BackFrameEncoded(object? sender, H264FrameEventArgs e)
    {
        UpdateSpsPpsCache(e);

        // Fan-out: deliver this frame to every registered back-camera client
        lock (_backClientChannelsLock)
        {
            foreach (var ch in _backClientChannels.Values)
                ch.Writer.TryWrite(e);
        }

        try { FrameEncoded?.Invoke(this, e); }
        catch (Exception ex) { Android.Util.Log.Error("H264EncoderManager", $"FrameEncoded subscriber error: {ex.Message}"); }
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

    private Channel<H264FrameEventArgs>? GetClientChannel(int cameraId, string clientId)
    {
        if (cameraId == 1)
        {
            lock (_frontClientChannelsLock)
            {
                _frontClientChannels.TryGetValue(clientId, out var ch);
                return ch;
            }
        }
        else
        {
            lock (_backClientChannelsLock)
            {
                _backClientChannels.TryGetValue(clientId, out var ch);
                return ch;
            }
        }
    }

    private static void DrainClientChannels(Dictionary<string, Channel<H264FrameEventArgs>> channels, object lockObj)
    {
        lock (lockObj)
        {
            foreach (var ch in channels.Values)
                while (ch.Reader.TryRead(out _)) { }
        }
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
