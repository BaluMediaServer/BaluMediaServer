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

    // Manual bitrate override per camera, in bits/sec. 0 = automatic (resolution-scaled).
    // A positive value is used verbatim — even below the auto recommendation — so the
    // consuming app can deliberately run, e.g., 2 Mbps at 2K if it wants the smaller stream.
    private int _backManualBitrate = 0;
    private int _frontManualBitrate = 0;

    // Encoder frame rate. Kept here (not just at the constructor call site) so the auto
    // bitrate calculation matches what the encoder is actually configured with.
    // 30 matches the camera sensor's delivery rate (measured steady 30fps on MT6768);
    // configuring the encoder below the camera rate just drops frames for no benefit.
    private const int EncoderFrameRate = 30;

    // Auto bitrate target in bits per pixel per frame. ~0.1 bpp is a good quality/size
    // balance for camera content (e.g. 2560x1440@30 → ~11.1 Mbps, 1280x720@30 → ~2.8 Mbps).
    private const double AutoBitsPerPixel = 0.1;

    // Bounds for the *automatic* calculation only. Manual overrides are not clamped up to
    // AutoBitrateMin — they are honored down to ManualBitrateMin.
    private const int AutoBitrateMin = 1_500_000;
    private const int AutoBitrateMax = 20_000_000;
    private const int ManualBitrateMin = 100_000;
    private const int ManualBitrateMax = 100_000_000;

    // Global SPS/PPS cache for SDP generation
    private byte[]? _currentSps;
    private byte[]? _currentPps;
    private readonly object _spsPpsLock = new();

    // Per-client buffer: a few frames of slack to absorb encoder/send timing jitter.
    // Capacity 1 was too aggressive — the encoder produces ~12-30fps and the send loop drains
    // one frame per iteration, so the slightest jitter (a large motion frame, a scheduling hiccup)
    // left the channel momentarily full and DropOldest discarded an encoded frame. Dropping a
    // P-frame breaks the decoder's reference chain → "melted" smearing on moving regions until the
    // next IDR. A 4-frame buffer absorbs that jitter so steady-state drops disappear; it only drops
    // under genuine sustained overload (which then signals the resolution is too high for the SoC).
    // Cost is bounded latency: up to ~4 frame intervals (~130ms at 30fps) — invisible for live view.
    // DropOldest still guarantees a slow client never stalls the shared fan-out loop.
    private const int MaxH264QueueSize = 4;

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
        BaluLogger.Debug("[EncoderManager]", $"Registered frame channel for client {clientId} camera {cameraId}");
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
        BaluLogger.Debug("[EncoderManager]", $"Unregistered frame channel for client {clientId} camera {cameraId}");
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
                BaluLogger.Info("[EncoderManager]", "Back encoder stalled — restarting");
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
                    int bitrate = ResolveBitrate(_backManualBitrate, width, height);
                    BaluLogger.Debug("[EncoderManager]", $"Starting H264 encoder: {width}x{height} @ {bitrate}bps ({(_backManualBitrate > 0 ? "manual" : "auto")})");
                    _h264BackEncoder = new H264Encoder(width, height, bitrate: bitrate, frameRate: EncoderFrameRate);

                    // Check if encoder fell back to different resolution
                    if (_h264BackEncoder.ActualWidth != width || _h264BackEncoder.ActualHeight != height)
                    {
                        BaluLogger.Warn("[EncoderManager]", $"Encoder using {_h264BackEncoder.ActualWidth}x{_h264BackEncoder.ActualHeight} instead of {width}x{height}");
                    }

                    int expectedSize = (_h264BackEncoder.ActualWidth * _h264BackEncoder.ActualHeight * 3) / 2;
                    _h264BackEncoderExpectedFrameSize = actualFrameSize > 0 ? actualFrameSize : expectedSize;

                    _h264BackEncoder.FrameEncoded += OnH264BackFrameEncoded;
                    if (!_h264BackEncoder.Start())
                    {
                        BaluLogger.Error("[EncoderManager]", "Encoder failed to start");
                        _h264BackEncoder.Dispose();
                        _h264BackEncoder = null;
                        return;
                    }
                    BaluLogger.Debug("[EncoderManager]", $"H264 back encoder started: {_h264BackEncoder.ActualWidth}x{_h264BackEncoder.ActualHeight}");
                }
                catch (Exception ex)
                {
                    BaluLogger.Error("[EncoderManager]", $"Failed to start H264 encoder: {ex.Message}");
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
                BaluLogger.Info("[EncoderManager]", "Front encoder stalled — restarting");
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
                    int bitrate = ResolveBitrate(_frontManualBitrate, width, height);
                    BaluLogger.Debug("[EncoderManager]", $"Starting H264 front encoder: {width}x{height} @ {bitrate}bps ({(_frontManualBitrate > 0 ? "manual" : "auto")})");
                    _h264FrontEncoder = new H264Encoder(width, height, bitrate: bitrate, frameRate: EncoderFrameRate);

                    // Check if encoder fell back to different resolution
                    if (_h264FrontEncoder.ActualWidth != width || _h264FrontEncoder.ActualHeight != height)
                    {
                        BaluLogger.Warn("[EncoderManager]", $"Front encoder using {_h264FrontEncoder.ActualWidth}x{_h264FrontEncoder.ActualHeight} instead of {width}x{height}");
                    }

                    int expectedSize = (_h264FrontEncoder.ActualWidth * _h264FrontEncoder.ActualHeight * 3) / 2;
                    _h264FrontEncoderExpectedFrameSize = expectedSize;

                    _h264FrontEncoder.FrameEncoded += OnH264FrontFrameEncoded;
                    if (!_h264FrontEncoder.Start())
                    {
                        BaluLogger.Error("[EncoderManager]", "Front encoder failed to start");
                        _h264FrontEncoder.Dispose();
                        _h264FrontEncoder = null;
                        return;
                    }
                    BaluLogger.Debug("[EncoderManager]", $"H264 front encoder started: {_h264FrontEncoder.ActualWidth}x{_h264FrontEncoder.ActualHeight}");
                }
                catch (Exception ex)
                {
                    BaluLogger.Error("[EncoderManager]", $"Failed to start H264 front encoder: {ex.Message}");
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
                BaluLogger.Info("[EncoderManager]", "H264 back encoder stopped");
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
                BaluLogger.Info("[EncoderManager]", "H264 front encoder stopped");
            }
        }
    }

    /// <inheritdoc/>
    public void RestartEncoderWithNewSize(int cameraId, int frameSize, int hintWidth, int hintHeight)
    {
        var (width, height) = CalculateDimensionsFromFrameSize(frameSize, hintWidth, hintHeight);
        BaluLogger.Info("[EncoderManager]", $"Restarting encoder with new dimensions: {width}x{height}");

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
    public H264FrameEventArgs? WaitDequeueFrame(int cameraId, string clientId, int timeoutMs, CancellationToken cancellationToken)
    {
        Channel<H264FrameEventArgs>? channel = GetClientChannel(cameraId, clientId);
        if (channel == null) return null;

        try
        {
            var vt = channel.Reader.WaitToReadAsync(cancellationToken);

            // Fast path: frame already in channel — no OS wait needed.
            if (vt.IsCompleted)
            {
                if (!vt.Result) return null; // channel completed (encoder stopped)
                channel.Reader.TryRead(out var immediateFrame);
                return immediateFrame;
            }

            // Slow path: block the current OS thread (a LongRunning/Thread-class thread).
            // Task.Wait() uses a kernel futex → the OS wakes this thread the moment the
            // encoder writes a frame, with ~1ms scheduling latency vs 10–70ms for async
            // continuation dispatch on Android's thread pool.
            if (!vt.AsTask().Wait(timeoutMs, cancellationToken)) return null; // timeout
            channel.Reader.TryRead(out var frame);
            return frame;
        }
        catch (OperationCanceledException)
        {
            return null;
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
        // CRITICAL: camera2 SENSOR_TIMESTAMP is in NANOSECONDS, but MediaCodec presentation
        // timestamps must be MICROSECONDS. Feeding raw nanoseconds makes consecutive frames
        // appear 1000x further apart (e.g. 40ms -> 40 apparent seconds). The MediaTek C2
        // encoder's time-based GOP logic then sees "last IDR > sync-frame-interval (30s) ago"
        // on EVERY frame and forces all-IDR output: ~190KB per frame, 2x bitrate overshoot,
        // and 80-220ms IDR encode times that capped the pipeline at ~7-11fps at 2K. The same
        // ns-scale PTS echoed on encoder output is what historically looked like "MT6768
        // reports PresentationTimeUs in units ~1000x larger than microseconds".
        long timestampUs = frame.Timestamp / 1000;

        if (cameraId == 1) // Front camera
        {
            _h264FrontEncoder?.QueueFrame(frame.Data, timestampUs, frame.Width, frame.Height);
        }
        else // Back camera
        {
            _h264BackEncoder?.QueueFrame(frame.Data, timestampUs, frame.Width, frame.Height);
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
    public void SetBitrate(int cameraId, int bitrate)
    {
        // bitrate <= 0 selects automatic (resolution-scaled) mode.
        int manual = bitrate <= 0 ? 0 : Math.Clamp(bitrate, ManualBitrateMin, ManualBitrateMax);

        if (cameraId == 1) // Front camera
        {
            lock (_h264FrontLock)
            {
                _frontManualBitrate = manual;
                if (_h264FrontEncoder != null)
                {
                    int effective = ResolveBitrate(manual, _h264FrontEncoder.ActualWidth, _h264FrontEncoder.ActualHeight);
                    _h264FrontEncoder.UpdateBitrate(effective);
                    BaluLogger.Info("[EncoderManager]", $"Front camera bitrate set to {effective}bps ({(manual > 0 ? "manual" : "auto")})");
                }
            }
        }
        else // Back camera
        {
            lock (_h264BackLock)
            {
                _backManualBitrate = manual;
                if (_h264BackEncoder != null)
                {
                    int effective = ResolveBitrate(manual, _h264BackEncoder.ActualWidth, _h264BackEncoder.ActualHeight);
                    _h264BackEncoder.UpdateBitrate(effective);
                    BaluLogger.Info("[EncoderManager]", $"Back camera bitrate set to {effective}bps ({(manual > 0 ? "manual" : "auto")})");
                }
            }
        }
    }

    /// <inheritdoc/>
    public int GetBitrate(int cameraId)
    {
        if (cameraId == 1) // Front camera
        {
            lock (_h264FrontLock)
                return ResolveBitrate(_frontManualBitrate, _h264FrontEncoder?.ActualWidth ?? 0, _h264FrontEncoder?.ActualHeight ?? 0);
        }
        lock (_h264BackLock)
            return ResolveBitrate(_backManualBitrate, _h264BackEncoder?.ActualWidth ?? 0, _h264BackEncoder?.ActualHeight ?? 0);
    }

    /// <summary>
    /// Returns the effective bitrate to use: the manual override when positive, otherwise the
    /// automatic resolution-scaled value. The manual value is honored as-is (it may be lower than
    /// the auto recommendation).
    /// </summary>
    private static int ResolveBitrate(int manualBitrate, int width, int height)
        => manualBitrate > 0 ? manualBitrate : CalculateAutoBitrate(width, height, EncoderFrameRate);

    /// <summary>
    /// Computes the recommended bitrate (bits/sec) for a resolution using a fixed
    /// bits-per-pixel target, clamped to a sane hardware range. Exposed so the consuming
    /// app can show the auto value in its bitrate UI.
    /// </summary>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="frameRate">Target frame rate. Defaults to the encoder frame rate.</param>
    public static int CalculateAutoBitrate(int width, int height, int frameRate = EncoderFrameRate)
    {
        if (width <= 0 || height <= 0) return AutoBitrateMin;
        long bits = (long)((double)width * height * frameRate * AutoBitsPerPixel);
        return (int)Math.Clamp(bits, AutoBitrateMin, AutoBitrateMax);
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

        // Snapshot channel list under lock, then write without holding the lock.
        // Clients registering/unregistering only wait for the fast snapshot copy, not all TryWrite calls.
        Channel<H264FrameEventArgs>[]? snapshot = null;
        int count = 0;
        lock (_frontClientChannelsLock)
        {
            count = _frontClientChannels.Count;
            if (count > 0)
            {
                snapshot = System.Buffers.ArrayPool<Channel<H264FrameEventArgs>>.Shared.Rent(count);
                int i = 0;
                foreach (var ch in _frontClientChannels.Values)
                    snapshot[i++] = ch;
            }
        }
        if (snapshot != null)
        {
            for (int i = 0; i < count; i++)
                snapshot[i].Writer.TryWrite(e);
            System.Buffers.ArrayPool<Channel<H264FrameEventArgs>>.Shared.Return(snapshot, clearArray: true);
        }

        try { FrameEncoded?.Invoke(this, e); }
        catch (Exception ex) { BaluLogger.Error("H264EncoderManager", $"FrameEncoded subscriber error: {ex.Message}"); }
    }

    private void OnH264BackFrameEncoded(object? sender, H264FrameEventArgs e)
    {
        UpdateSpsPpsCache(e);

        // Snapshot channel list under lock, then write without holding the lock.
        Channel<H264FrameEventArgs>[]? snapshot = null;
        int count = 0;
        lock (_backClientChannelsLock)
        {
            count = _backClientChannels.Count;
            if (count > 0)
            {
                snapshot = System.Buffers.ArrayPool<Channel<H264FrameEventArgs>>.Shared.Rent(count);
                int i = 0;
                foreach (var ch in _backClientChannels.Values)
                    snapshot[i++] = ch;
            }
        }
        if (snapshot != null)
        {
            for (int i = 0; i < count; i++)
                snapshot[i].Writer.TryWrite(e);
            System.Buffers.ArrayPool<Channel<H264FrameEventArgs>>.Shared.Return(snapshot, clearArray: true);
        }

        try { FrameEncoded?.Invoke(this, e); }
        catch (Exception ex) { BaluLogger.Error("H264EncoderManager", $"FrameEncoded subscriber error: {ex.Message}"); }
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

    /// <inheritdoc/>
    public Channel<H264FrameEventArgs>? GetClientChannelRef(int cameraId, string clientId)
        => GetClientChannel(cameraId, clientId);

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
