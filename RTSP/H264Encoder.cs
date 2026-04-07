using Android.Media;
using Android.OS;
using Java.Nio;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Android.Util;
using BaluMediaServer.Models;
using System.Diagnostics;
using System.Buffers;

namespace BaluMediaServer.Services;

/// <summary>
/// General-purpose H.264 hardware encoder with automatic encoder selection.
/// Selects the best available encoder based on device capabilities and supports multiple vendors.
/// Uses Channels for efficient frame queuing with automatic frame dropping.
/// </summary>
public class H264Encoder : IDisposable
{
    private MediaCodec? _encoder;
    private int _width;
    private int _height;
    private int _bitrate;
    private readonly int _frameRate;
    private bool _isRunning;

    /// <summary>
    /// Gets whether the encoder is currently running.
    /// </summary>
    public bool IsRunning => _isRunning;
    private Thread? _encoderThread;
    private readonly Channel<FrameData> _frameChannel;
    private readonly ArrayPool<byte> _bufferPool = ArrayPool<byte>.Create();

    private readonly object _lock = new();
    private volatile bool _disposed;  // Prevents JNI access after disposal

    // Color formats supported by your device
    private const int COLOR_FormatYUV420Planar = 19;
    private const int COLOR_FormatYUV420SemiPlanar = 21;  // NV12
    private const int COLOR_FormatYUV420PackedSemiPlanar = 39;
    private const int COLOR_Format32bitARGB8888 = 2130708361;
    private const int COLOR_FormatYUV420Flexible = 2135033992;
    private long _lastTimestamp = 0;
    private int _lastLoggedSize = 0;
    private readonly EncoderInfo _bestEncoder;
    private readonly Stopwatch _stopwatch = new();

    // Cache the codec scan result — the system codec list never changes at runtime,
    // so re-scanning on every stall restart wastes 200-700ms for nothing.
    private static EncoderInfo? _cachedBestEncoder;
    private static readonly object _bestEncoderCacheLock = new();
    private int _selectedColorFormat = COLOR_FormatYUV420SemiPlanar;
    private int _encoderStride;    // Actual encoder input row stride (may differ from _width)
    private int _encoderSliceHeight; // Actual encoder Y plane height (may differ from _height)

    // Frame-rate synchronized timestamp management
    private long _frameNumber = 0;
    private long _frameIntervalUs; // Microseconds per frame based on target FPS
    private long _baseTimestamp = -1;

    // Output stall detection: if input is fed but no output for this duration, encoder is stalled
    private long _lastOutputTicks;
    private long _lastInputTicks;
    private static readonly long StallThresholdTicks = Stopwatch.Frequency; // 1 second

    /// <summary>
    /// Event raised when a frame has been encoded and is ready for streaming.
    /// </summary>
    public event EventHandler<H264FrameEventArgs>? FrameEncoded;

    /// <summary>
    /// Gets the actual width being used by the encoder after any resolution fallback.
    /// </summary>
    public int ActualWidth => _width;

    /// <summary>
    /// Gets the actual height being used by the encoder after any resolution fallback.
    /// </summary>
    public int ActualHeight => _height;

    /// <summary>
    /// Represents frame data waiting to be encoded.
    /// </summary>
    public class FrameData
    {
        /// <summary>
        /// Gets or sets the raw YUV frame data.
        /// </summary>
        public byte[] Data { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// Gets or sets the presentation timestamp in microseconds.
        /// </summary>
        public long Timestamp { get; set; }

        /// <summary>
        /// Gets or sets the source frame width (camera resolution, may differ from encoder).
        /// </summary>
        public int SourceWidth { get; set; }

        /// <summary>
        /// Gets or sets the source frame height (camera resolution, may differ from encoder).
        /// </summary>
        public int SourceHeight { get; set; }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="H264Encoder"/> class.
    /// Automatically selects the best available hardware encoder.
    /// </summary>
    /// <param name="width">The video width in pixels.</param>
    /// <param name="height">The video height in pixels.</param>
    /// <param name="bitrate">The target bitrate in bits per second. Default is 2,000,000.</param>
    /// <param name="frameRate">The target frame rate. Default is 30.</param>
    public H264Encoder(int width, int height, int bitrate = 2000000, int frameRate = 30)
    {
        _width = width;
        _height = height;
        _bitrate = bitrate;
        _frameRate = frameRate;
        _frameIntervalUs = 1_000_000L / frameRate; // e.g., 40000us for 25fps

        // Create bounded channel with DropOldest to prevent latency buildup.
        // All frame input MUST go through this channel so that FeedInputBuffer()
        // and DrainOutputBuffer() run on the same thread (EncodingLoop).
        // Concurrent JNI calls to MediaCodec from different threads can cause
        // vendor-specific stalls (especially on MediaTek).
        _frameChannel = Channel.CreateBounded<FrameData>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

        _bestEncoder = GetCachedBestEncoder();
    }

    private static EncoderInfo GetCachedBestEncoder()
    {
        lock (_bestEncoderCacheLock)
        {
            if (_cachedBestEncoder == null)
            {
                var codecList = new MediaCodecList(new());
                var codecInfos = codecList.GetCodecInfos();
                _cachedBestEncoder = SelectBestEncoder(codecInfos!);
                Log.Info("H264MTK", $"Encoder selection cached: {_cachedBestEncoder?.Name ?? "none"}");
            }
            return _cachedBestEncoder;
        }
    }

    private static readonly Dictionary<string, int> EncoderPriority = new Dictionary<string, int>
    {
        // Hardware encoders (highest priority)
        { "OMX.qcom.video.encoder.avc", 100 },      // Qualcomm
        { "OMX.Exynos.avc.enc", 95 },               // Samsung Exynos
        { "OMX.hisi.video.encoder.avc", 85 },       // HiSilicon (Huawei)
        { "OMX.Intel.hw_ve.h264", 80 },             // Intel
        { "OMX.IMG.TOPAZ.VIDEO.ENCODER", 75 },      // PowerVR
        { "OMX.Nvidia.h264.encoder", 70 },          // NVIDIA
        
        // Generic hardware encoders
        { "c2.android.avc.encoder", 60 },           // Android Codec 2.0
        { "c2.mtk.avc.encoder", 50 },               // MediaTek hardware
        { "OMX.MTK.VIDEO.ENCODER.AVC", 45 },        // MediaTek OMX
        { "OMX.google.h264.encoder", 30 },          // Software fallback
        
        // Software encoders (lowest priority)
        { "OMX.SEC.avc.enc", 20 },                  // Older Samsung software
        { "AVC Encoder", 10 }                        // Generic software
    };
    private static readonly HashSet<string> PreferredColorFormats = new HashSet<string>
    {
        "2130708361",  // COLOR_FormatSurface (most efficient for hardware)
        "21",          // COLOR_FormatYUV420SemiPlanar
        "39",          // COLOR_FormatYUV420PackedSemiPlanar
        "2130706688"   // COLOR_FormatYUV420Flexible
    };

    /// <summary>
    /// Selects the best H.264 encoder from available codecs based on capabilities and vendor priority.
    /// </summary>
    /// <param name="codecInfos">Array of available codec information.</param>
    /// <returns>The <see cref="EncoderInfo"/> for the best available encoder.</returns>
    public static EncoderInfo SelectBestEncoder(MediaCodecInfo[] codecInfos)
    {
        var encoders = new List<EncoderInfo>();

        foreach (var info in codecInfos)
        {
            var types = info.GetSupportedTypes();
            if (info.IsEncoder && types!.Contains(MediaFormat.MimetypeVideoAvc))
            {
                var encoderInfo = EvaluateEncoder(info);
                if (encoderInfo != null)
                {
                    encoders.Add(encoderInfo);
                    Log.Debug("EncoderSelector", $"Evaluated {encoderInfo.Name}: Score={encoderInfo.Score}");
                }
            }
        }

        // Sort by score (highest first)
        encoders.Sort((a, b) => b.Score.CompareTo(a.Score));

        // Log the ranking
        Log.Debug("EncoderSelector", "Encoder ranking:");
        
        foreach (var encoder in encoders)
        {
            Log.Debug("EncoderSelector", $"  {encoder.Name}: {encoder.Score} points");
        }

        return encoders.FirstOrDefault()!;
    }
    private static bool IsHardwareEncoder(string encoderName)
    {
        var name = encoderName.ToLower();
        return name.Contains("omx.") &&
               !name.Contains("google") &&
               !name.Contains("ffmpeg") &&
               !name.Contains("software") &&
               !name.Contains("sw");
    }
    private static bool IsHardwareAccelerated(MediaCodecInfo codecInfo)
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
        {
            return codecInfo.IsHardwareAccelerated;
        }
        
        // Fallback for older APIs
        return IsHardwareEncoder(codecInfo.Name);
    }

    private static EncoderInfo EvaluateEncoder(MediaCodecInfo codecInfo)
    {
        try
        {
            var caps = codecInfo.GetCapabilitiesForType(MediaFormat.MimetypeVideoAvc);
            if (caps == null) return null!;

            var encoderInfo = new EncoderInfo
            {
                Codec = codecInfo,
                Name = codecInfo.Name,
                Score = 0,
                Capabilities = caps,
                ColorFormats = new List<int>()
            };

            // 1. Base score from known encoder priority
            if (EncoderPriority.TryGetValue(codecInfo.Name, out int basePriority))
            {
                encoderInfo.Score += basePriority;
            }
            else
            {
                // Unknown encoder - give it a middle score if it's hardware
                encoderInfo.Score += IsHardwareEncoder(codecInfo.Name) ? 50 : 5;
            }

            // 2. Check for hardware acceleration indicators
            if (IsHardwareAccelerated(codecInfo))
            {
                encoderInfo.Score += 20;
            }

            // 3. Evaluate color format support
            if (caps.ColorFormats != null)
            {
                foreach (var format in caps.ColorFormats)
                {
                    encoderInfo.ColorFormats.Add(format);
                    if (PreferredColorFormats.Contains(format.ToString()))
                    {
                        encoderInfo.Score += 10;
                    }

                    // COLOR_FormatSurface is the most efficient
                    if (format == 2130708361)
                    {
                        encoderInfo.Score += 15;
                    }
                }
            }

            // 4. Check encoder capabilities
            var encoderCaps = caps.EncoderCapabilities;
            if (encoderCaps != null)
            {
                // Check for low latency support
                if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
                {
                    if (encoderCaps.IsBitrateModeSupported(BitrateMode.CbrFd))
                    {
                        encoderInfo.SupportsBitrateMode = true;
                        encoderInfo.Score += 5;
                    }
                }

                // Check for quality levels support
                if (encoderCaps.QualityRange != null)
                {
                    encoderInfo.Score += 5;
                }
            }

            // 4b. Check video capabilities for supported resolutions
            var videoCaps = caps.VideoCapabilities;
            if (videoCaps != null)
            {
                var widthUpper = videoCaps.SupportedWidths?.Upper;
                var heightUpper = videoCaps.SupportedHeights?.Upper;
                if (widthUpper != null) encoderInfo.MaxSupportedWidth = (int)widthUpper;
                if (heightUpper != null) encoderInfo.MaxSupportedHeight = (int)heightUpper;
                encoderInfo.Supports4K = videoCaps.IsSizeSupported(3840, 2160);
                encoderInfo.SupportsQHD = videoCaps.IsSizeSupported(2560, 1440);
                encoderInfo.SupportsFullHD = videoCaps.IsSizeSupported(1920, 1080);
                encoderInfo.SupportsHD = videoCaps.IsSizeSupported(1280, 720);
            }

            // 5. Check profile/level support
            if (caps.ProfileLevels != null && caps.ProfileLevels.Count > 0)
            {
                bool supportsBaseline = false;
                bool supportsMain = false;
                bool supportsHigh = false;

                foreach (var pl in caps.ProfileLevels)
                {
                    switch (pl.Profile)
                    {
                        case MediaCodecProfileType.Avcprofilebaseline:
                            supportsBaseline = true;
                            break;
                        case MediaCodecProfileType.Avcprofilemain:
                            supportsMain = true;
                            break;
                        case MediaCodecProfileType.Avcprofilehigh:
                            supportsHigh = true;
                            break;
                    }
                }

                if (supportsBaseline) encoderInfo.Score += 5;
                if (supportsMain) encoderInfo.Score += 3;
                if (supportsHigh) encoderInfo.Score += 2;
            }

            return encoderInfo;
        }
        catch (Exception e)
        {
            Log.Error("EncoderSelector", $"Error evaluating encoder {codecInfo.Name}: {e.Message}");
            return null!;
        }
    }

    /// <summary>
    /// Checks if the specified resolution is supported by the encoder.
    /// </summary>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <returns><c>true</c> if the resolution is supported; otherwise, <c>false</c>.</returns>
    private bool IsResolutionSupported(int width, int height)
    {
        if (_bestEncoder?.Capabilities?.VideoCapabilities == null) return true;
        return _bestEncoder.Capabilities.VideoCapabilities.IsSizeSupported(width, height);
    }

    /// <summary>
    /// Gets the nearest supported resolution that fits within the requested dimensions.
    /// </summary>
    /// <param name="width">The requested width in pixels.</param>
    /// <param name="height">The requested height in pixels.</param>
    /// <returns>A tuple containing the nearest supported width and height.</returns>
    private (int width, int height) GetNearestSupportedResolution(int width, int height)
    {
        var videoCaps = _bestEncoder?.Capabilities?.VideoCapabilities;
        if (videoCaps == null) return (width, height);

        // Try common resolutions in descending order (including 4K and high-res)
        var resolutions = new[] {
            (3840, 2160), // 4K UHD
            (3200, 1800), // QHD+
            (2560, 1440), // QHD/2K
            (1920, 1440), // FHD+ (4:3)
            (1920, 1080), // FHD
            (1280, 720),  // HD
            (800, 600),
            (640, 480),   // VGA
            (480, 360),
            (320, 240)
        };

        foreach (var (w, h) in resolutions)
        {
            if (w <= width && h <= height && videoCaps.IsSizeSupported(w, h))
                return (w, h);
        }
        return (640, 480); // Safe fallback
    }

    /// <summary>
    /// Probes the hardware encoder to find the nearest supported resolution without
    /// actually creating or starting an encoder. Use this to configure the camera at
    /// a resolution the encoder can handle, avoiding a costly camera restart.
    /// </summary>
    public static (int width, int height) ProbeSupportedResolution(int requestedWidth, int requestedHeight)
    {
        try
        {
            var codecList = new MediaCodecList(new());
            var codecInfos = codecList.GetCodecInfos();
            var best = SelectBestEncoder(codecInfos!);
            if (best?.Capabilities?.VideoCapabilities == null)
                return (requestedWidth, requestedHeight);

            var videoCaps = best.Capabilities.VideoCapabilities;
            if (videoCaps.IsSizeSupported(requestedWidth, requestedHeight))
                return (requestedWidth, requestedHeight);

            // Try common resolutions in descending order
            var resolutions = new[] {
                (3840, 2160), (2560, 1440), (1920, 1080), (1280, 720),
                (800, 600), (640, 480), (320, 240)
            };

            foreach (var (w, h) in resolutions)
            {
                if (w <= requestedWidth && h <= requestedHeight && videoCaps.IsSizeSupported(w, h))
                {
                    Log.Info("H264", $"ProbeSupportedResolution: {requestedWidth}x{requestedHeight} not supported, using {w}x{h}");
                    return (w, h);
                }
            }

            return (640, 480);
        }
        catch (Exception ex)
        {
            Log.Error("H264", $"ProbeSupportedResolution error: {ex.Message}");
            return (requestedWidth, requestedHeight);
        }
    }

    /// <summary>
    /// Starts the H.264 encoder and begins the encoding loop.
    /// </summary>
    /// <returns><c>true</c> if the encoder started successfully; otherwise, <c>false</c>.</returns>
    public bool Start()
    {
        lock (_lock)
        {
            if (_isRunning) return true;

            try
            {
                // Check resolution support and fall back if necessary
                if (!IsResolutionSupported(_width, _height))
                {
                    var (newW, newH) = GetNearestSupportedResolution(_width, _height);
                    Log.Warn("H264", $"Resolution {_width}x{_height} not supported, falling back to {newW}x{newH}");
                    _width = newW;
                    _height = newH;
                }

                // Create format with encoder's supported color format
                var format = MediaFormat.CreateVideoFormat(MediaFormat.MimetypeVideoAvc, _width, _height);

                // Prefer NV12 (SemiPlanar) — it has a well-defined buffer layout for raw
                // ByteBuffer writes. COLOR_FormatYUV420Flexible has undefined layout for
                // raw writes and causes green corruption at higher resolutions.
                if (_bestEncoder.ColorFormats.Contains(COLOR_FormatYUV420SemiPlanar))
                {
                    _selectedColorFormat = COLOR_FormatYUV420SemiPlanar; // NV12
                    Log.Debug("H264", "Using COLOR_FormatYUV420SemiPlanar (NV12)");
                }
                else if (_bestEncoder.ColorFormats.Contains(COLOR_FormatYUV420Flexible))
                {
                    _selectedColorFormat = COLOR_FormatYUV420Flexible;
                    Log.Warn("H264", "Using COLOR_FormatYUV420Flexible (NV12 unavailable)");
                }
                else
                {
                    _selectedColorFormat = COLOR_FormatYUV420SemiPlanar; // Default fallback
                    Log.Debug("H264", "Using default COLOR_FormatYUV420SemiPlanar");
                }

                format.SetInteger(MediaFormat.KeyColorFormat, _selectedColorFormat);
                format.SetInteger(MediaFormat.KeyBitRate, _bitrate);
                format.SetInteger(MediaFormat.KeyFrameRate, _frameRate);
                // Use SetInteger (not SetFloat) — MediaTek MT6768 misinterprets sub-second float
                // values as 0, causing EVERY frame to be an IDR keyframe, which exhausts the
                // encoder's internal buffers and causes it to stall after ~1000 frames.
                // Value of 1 = IDR every 1 second (~25 frames at 25fps).
                format.SetInteger(MediaFormat.KeyIFrameInterval, 1);
                
                // Set profile and level for better compatibility
                format.SetInteger(MediaFormat.KeyProfile, (int)MediaCodecProfileType.Avcprofilebaseline);
                format.SetInteger(MediaFormat.KeyLevel, 0x100);

                // Low latency configuration
                if (Build.VERSION.SdkInt >= BuildVersionCodes.R) // API 30+
                {
                    format.SetInteger(MediaFormat.KeyLowLatency, 1);
                    format.SetInteger(MediaFormat.KeyPriority, 0); // Real-time priority
                }

                if (Build.VERSION.SdkInt >= BuildVersionCodes.M) // API 23+
                {
                    format.SetInteger(MediaFormat.KeyOperatingRate, short.MaxValue);
                    
                    // Only set intra refresh if supported
                    if (_bestEncoder.SupportsIntraRefresh)
                    {
                        format.SetInteger(MediaFormat.KeyIntraRefreshPeriod, 10);
                    }
                }
                if (_bestEncoder.Name.Contains("MTK"))
                {
                    try
                    {
                        format.SetInteger("vendor.mtk-ext-enc-low-latency.enable", 1);
                    }
                    catch { }
                }

                // Create encoder
                MediaCodec? encoder = MediaCodec.CreateByCodecName(_bestEncoder.Name);
                if (encoder == null)
                {
                    throw new Exception($"Failed to create encoder: {_bestEncoder.Name}");
                }
                encoder.Configure(format, null, null, MediaCodecConfigFlags.Encode);

                // Now start the encoder
                encoder.Start();

                // Reinforce bitrate after Start() — SetParameters requires a started codec.
                // The bitrate was already set in MediaFormat before Configure(), but some
                // SoCs (MediaTek) may ignore it; this dynamic update ensures compliance.
                if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
                {
                    var bundle = new Bundle();
                    bundle.PutInt(MediaCodec.ParameterKeyVideoBitrate, _bitrate);
                    encoder.SetParameters(bundle);
                }

                _encoder = encoder;
                _isRunning = true;

                // Query actual input format to get stride and sliceHeight
                // MediaCodec may use larger stride/sliceHeight for alignment
                _encoderStride = _width;
                _encoderSliceHeight = _height;
                try
                {
                    var inputFormat = encoder.InputFormat;
                    if (inputFormat != null)
                    {
                        _encoderStride = inputFormat.GetInteger(MediaFormat.KeyStride, _width);
                        _encoderSliceHeight = inputFormat.GetInteger(MediaFormat.KeySliceHeight, _height);

                        // Some encoders return 0 meaning "same as configured"
                        if (_encoderStride <= 0) _encoderStride = _width;
                        if (_encoderSliceHeight <= 0) _encoderSliceHeight = _height;

                        Log.Info("H264", $"Encoder input: stride={_encoderStride}, sliceHeight={_encoderSliceHeight} (video={_width}x{_height})");
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("H264", $"Could not query input format: {ex.Message}");
                }

                // Start encoding thread
                _encoderThread = new Thread(EncodingLoop)
                {
                    IsBackground = true,
                    Name = "H264-Encoder",
                    Priority = System.Threading.ThreadPriority.Highest
                };
                _encoderThread.Start();

                Log.Info("H264", $"Encoder started successfully: {_bestEncoder.Name}");
                Log.Info("H264", $"Format: {_width}x{_height} @ {_frameRate}fps, {_bitrate}bps");
                Log.Info("H264", $"Color format: {_selectedColorFormat}");
                
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("H264", $"Failed to start encoder: {ex.Message}");
                Log.Error("H264", $"Stack trace: {ex.StackTrace}");
                
                try
                {
                    _encoder?.Stop();
                    _encoder?.Release();
                }
                catch (Exception releaseEx)
                {
                    Log.Error("H264", $"Error releasing encoder: {releaseEx.Message}");
                }
                
                _encoder = null;
                _isRunning = false;
                return false;
            }
        }
    }

    /// <summary>
    /// Signals the encoder to produce an IDR (keyframe) on the next output frame.
    /// Call this when a new client connects so their decoder can start immediately
    /// instead of waiting up to one full I-frame interval (1 second at current settings).
    /// </summary>
    public void RequestKeyFrame()
    {
        lock (_lock)
        {
            if (!_isRunning || _encoder == null || _disposed) return;
            try
            {
                var bundle = new Bundle();
                bundle.PutInt(MediaCodec.ParameterKeyRequestSyncFrame, 0);
                _encoder.SetParameters(bundle);
                Log.Debug("H264MTK", "IDR keyframe requested for new client");
            }
            catch (Exception ex)
            {
                Log.Warn("H264MTK", $"RequestKeyFrame failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Updates the encoder bitrate dynamically without restarting.
    /// </summary>
    /// <param name="newBitrate">The new bitrate in bits per second.</param>
    public void UpdateBitrate(int newBitrate)
    {
        lock (_lock)
        {
            if (!_isRunning || _encoder == null) return;

            try
            {
                // Create a Bundle with the new bitrate
                var bundle = new Bundle();
                bundle.PutInt(MediaCodec.ParameterKeyVideoBitrate, newBitrate);
                
                // Apply the new parameters to the encoder
                _encoder.SetParameters(bundle);
                
                // Store the new bitrate
                _bitrate = newBitrate;
                
                Log.Debug("H264MTK", $"Bitrate updated to: {newBitrate}bps");
            }
            catch (Exception ex)
            {
                Log.Error("H264MTK", $"Failed to update bitrate: {ex.Message}");
            }
        }
    }
    /// <summary>
    /// Queues a raw YUV frame for encoding with the caller-provided timestamp.
    /// The frame will be processed by the EncodingLoop thread, ensuring all
    /// MediaCodec access is serialized on a single thread.
    /// </summary>
    /// <param name="frameData">The raw YUV420 frame data.</param>
    /// <param name="timestamp">The presentation timestamp in microseconds.</param>
    public void QueueFrame(byte[] frameData, long timestamp, int sourceWidth = 0, int sourceHeight = 0)
    {
        if (!_isRunning) return;

        _frameChannel.Writer.TryWrite(new() { Data = frameData, Timestamp = timestamp, SourceWidth = sourceWidth, SourceHeight = sourceHeight });
    }

    /// <summary>
    /// Queues a raw YUV frame for encoding with a synthetic timestamp.
    /// Older frames are dropped if the queue backs up to prevent latency.
    /// </summary>
    /// <param name="frameData">The raw YUV420 frame data.</param>
    public void QueueFrame(byte[] frameData)
    {
        if (!_isRunning) return;

        int expectedSize = (_width * _height * 3) / 2;

        if (frameData.Length != expectedSize)
        {
            Log.Error("H264", $"Invalid frame size: {frameData.Length}, expected: {expectedSize} (encoder: {_width}x{_height})");
            return;
        }

        // Initialize timing on first frame
        if (_baseTimestamp < 0)
        {
            _stopwatch.Restart();
            _baseTimestamp = 0;
            _frameNumber = 0;
        }

        // Frame-rate synchronized timestamp calculation
        // Use ideal timestamp based on frame number to prevent drift
        long idealTimestamp = _baseTimestamp + (_frameNumber * _frameIntervalUs);

        // Get actual elapsed time for bounds checking
        long actualTimestamp = _stopwatch.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;

        // Hybrid approach: use ideal timestamp but bound by actual time
        // This prevents drift while handling encoding delays
        long timestamp = Math.Min(idealTimestamp, actualTimestamp + _frameIntervalUs);

        _frameNumber++;

        // Channel with DropOldest automatically handles frame dropping
        _frameChannel.Writer.TryWrite(new() { Data = frameData, Timestamp = timestamp });
    }

    /// <summary>
    /// Main encoding loop running on a dedicated thread. Drains encoder output
    /// buffers BEFORE feeding new input to prevent input buffer starvation when
    /// the encoder's internal queue is full. Exits on disposal, stop, or after
    /// 10 consecutive errors.
    /// </summary>
    private void EncodingLoop()
    {
        MediaCodec.BufferInfo? bufferInfo = null;
        byte[]? sps = null;
        byte[]? pps = null;
        bool gotFirstOutput = false;
        int consecutiveErrors = 0;
        const int maxConsecutiveErrors = 10;

        long now = Stopwatch.GetTimestamp();
        _lastOutputTicks = now;
        _lastInputTicks = 0;

        Log.Debug("H264MTK", "Encoding loop started");

        try
        {
            bufferInfo = new MediaCodec.BufferInfo();

            // Check both _isRunning and _disposed to ensure clean shutdown
            while (_isRunning && !_disposed)
            {
                try
                {
                    bool processedInput = false;
                    bool processedOutput = false;

                    // Check disposed before each operation
                    if (_disposed) break;

                    // Drain output FIRST to free encoder buffers before feeding new input.
                    // This prevents input buffer starvation when the encoder's internal queue is full.
                    processedOutput = DrainOutputBuffer(bufferInfo, ref sps, ref pps, ref gotFirstOutput);

                    if (processedOutput)
                    {
                        _lastOutputTicks = Stopwatch.GetTimestamp();
                    }

                    // Check disposed again before feeding input
                    if (_disposed) break;

                    // Try to read frame and feed to encoder
                    if (_frameChannel.Reader.TryRead(out var frame))
                    {
                        FeedInputBuffer(frame);
                        processedInput = true;
                        _lastInputTicks = Stopwatch.GetTimestamp();
                    }

                    // Output stall detection: input is being fed but no output for too long
                    // means the hardware encoder (MediaCodec) is internally deadlocked or never started producing
                    if (_lastInputTicks > 0)
                    {
                        long currentTicks = Stopwatch.GetTimestamp();
                        long sinceLastOutput = currentTicks - _lastOutputTicks;
                        long sinceLastInput = currentTicks - _lastInputTicks;

                        if (sinceLastOutput > StallThresholdTicks && sinceLastInput < StallThresholdTicks && gotFirstOutput)
                        {
                            Log.Error("H264MTK", $"Output stall detected: no output for {sinceLastOutput / Stopwatch.Frequency}s while input is active (gotFirstOutput={gotFirstOutput}) — stopping encoder");
                            _isRunning = false;
                            break;
                        }
                    }

                    // SpinWait auto-escalates: spin → yield → short sleep, giving sub-ms
                    // wake-up instead of Thread.Sleep(1) which sleeps 1-15ms on Android.
                    if (!processedInput && !processedOutput)
                    {
                        var sw = new SpinWait();
                        sw.SpinOnce();
                    }

                    // Reset error counter on successful iteration
                    consecutiveErrors = 0;
                }
                catch (System.Exception ex)
                {
                    // Don't log errors during disposal - they're expected
                    if (_disposed) break;

                    consecutiveErrors++;
                    Log.Error("H264MTK", $"Encoding loop error ({consecutiveErrors}/{maxConsecutiveErrors}): {ex.Message}");

                    // Break out of loop if too many consecutive errors - encoder likely in bad state
                    if (consecutiveErrors >= maxConsecutiveErrors)
                    {
                        Log.Error("H264MTK", "Too many consecutive errors - stopping encoder loop");
                        _isRunning = false;
                        break;
                    }

                    // Small delay before retrying to prevent CPU spinning on repeated errors
                    Thread.Sleep(10);
                }
            }
        }
        finally
        {
            // Dispose Java object safely
            try { bufferInfo?.Dispose(); }
            catch { }
        }

        Log.Debug("H264MTK", "Encoding loop ended");
    }
    
    /// <summary>
    /// Feeds a frame into the encoder's input buffer.
    /// Handles color format conversion and stride padding as needed.
    /// </summary>
    /// <param name="frame">The frame data to encode.</param>
    private bool _loggedFirstFeed = false;
    private bool _loggedFirstWriteFrame = false;

    public void FeedInputBuffer(FrameData frame)
    {
        // Check disposed flag BEFORE any JNI calls to prevent SIGSEGV
        if (_disposed || _encoder == null) return;

        try
        {
            // Wait up to 1ms for an input buffer (low latency)
            var inputIndex = _encoder.DequeueInputBuffer(1000);

            if (inputIndex >= 0)
            {
                ByteBuffer? inputBuffer;

                if (Build.VERSION.SdkInt >= BuildVersionCodes.Lollipop)
                {
                    inputBuffer = _encoder.GetInputBuffer(inputIndex);
                }
                else
                {
#pragma warning disable CS0618
                    var buffers = _encoder.GetInputBuffers();
                    inputBuffer = buffers?[inputIndex];
#pragma warning restore CS0618
                }

                if (inputBuffer != null)
                {
                    inputBuffer.Clear();

                    // Calculate expected size for the encoder (width * height * 1.5 for YUV420)
                    int expectedSize = (_width * _height * 3) / 2;

                    byte[] frameData = frame.Data;

                    // One-time diagnostic log
                    if (!_loggedFirstFeed)
                    {
                        _loggedFirstFeed = true;
                        Log.Info("H264", $"DIAG feed: frameLen={frameData.Length}, expected={expectedSize}, encoder={_width}x{_height}, src={frame.SourceWidth}x{frame.SourceHeight}, bufCap={inputBuffer.Capacity()}, stride={_encoderStride}, slice={_encoderSliceHeight}, colorFmt={_selectedColorFormat}");
                    }

                    // Write frame directly to encoder buffer with correct stride/sliceHeight layout
                    // This avoids intermediate allocations and handles the MediaTek sliceHeight > height case
                    int dataSize = WriteFrameToEncoderBuffer(inputBuffer, frameData,
                        frame.SourceWidth, frame.SourceHeight);

                    _encoder.QueueInputBuffer(inputIndex, 0, dataSize, frame.Timestamp, 0);
                    _lastTimestamp = frame.Timestamp;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("H264MTK", $"Feed input error: {ex.Message}");
        }
    }
    /// <summary>
    /// Writes camera frame data directly to the encoder's input buffer.
    /// Handles all transformations in one pass:
    /// 1. Convert NV21 (camera) → NV12 (encoder) by swapping V/U bytes
    /// 2. Write with correct stride/sliceHeight layout for the encoder
    ///
    /// Important: MediaTek cameras may produce oversized buffers (e.g., 1843198 bytes for
    /// a 1280x720 frame) but the NV21 UV plane always starts at width * height — right after
    /// the declared Y rows. The extra bytes are buffer padding, NOT extra Y rows.
    /// Reading UV from the wrong offset (e.g., width * bufferHeight) causes green corruption.
    /// </summary>
    private int WriteFrameToEncoderBuffer(ByteBuffer inputBuffer, byte[] frameData,
        int srcWidth, int srcHeight)
    {
        int stride = _encoderStride;
        int sliceHeight = _encoderSliceHeight;

        // Source data layout (NV21):
        // Y plane: srcWidth * srcHeight bytes at offset 0
        // VU plane: srcWidth * (srcHeight/2) bytes at offset srcWidth * srcHeight
        // Note: MediaTek cameras may produce oversized buffers (e.g., 1843198 bytes for
        // 1280x720) but the UV plane always starts right after srcHeight rows of Y,
        // NOT after the full buffer. The extra bytes are buffer padding.
        int actualSrcHeight = srcHeight;
        int srcYStride = srcWidth;
        int srcUvStart = srcWidth * srcHeight;

        // Encoder buffer layout (NV12):
        // Y plane: stride * sliceHeight bytes at offset 0
        // UV plane: stride * (sliceHeight/2) bytes at offset stride * sliceHeight
        int dstYPlaneSize = stride * sliceHeight;
        int dstUvPlaneSize = stride * (sliceHeight / 2);
        int totalDstSize = dstYPlaneSize + dstUvPlaneSize;

        // Deduce sliceHeight from buffer capacity if query failed (defaults matched height)
        if (sliceHeight == _height && inputBuffer.Capacity() > totalDstSize + stride)
        {
            // Buffer is larger than expected — encoder likely has bigger sliceHeight
            // sliceHeight = bufferCapacity * 2 / (stride * 3)
            int deducedSlice = (inputBuffer.Capacity() * 2) / (stride * 3);
            if (deducedSlice > _height && (deducedSlice <= _height + 256 || deducedSlice <= actualSrcHeight + 16))
            {
                sliceHeight = deducedSlice;
                _encoderSliceHeight = sliceHeight;
                dstYPlaneSize = stride * sliceHeight;
                dstUvPlaneSize = stride * (sliceHeight / 2);
                totalDstSize = dstYPlaneSize + dstUvPlaneSize;
                Log.Info("H264", $"Deduced encoder sliceHeight={sliceHeight} from bufCap={inputBuffer.Capacity()} (stride={stride})");
            }
        }

        if (!_loggedFirstWriteFrame)
        {
            _loggedFirstWriteFrame = true;
            Log.Info("H264", $"WriteFrame: src={srcWidth}x{srcHeight}, encoder={_width}x{_height}, stride={stride}, slice={sliceHeight}, bufCap={inputBuffer.Capacity()}, srcUvStart={srcUvStart}, dstYPlane={dstYPlaneSize}, frameLen={frameData.Length}");
        }

        int copyWidth = Math.Min(srcWidth, _width);
        int copyHeight = Math.Min(actualSrcHeight, _height);

        // Build the output in a pooled byte array matching the encoder's expected layout
        int encoderDataSize = Math.Min(totalDstSize, inputBuffer.Capacity());
        byte[] encoderData = _bufferPool.Rent(encoderDataSize);
        Array.Clear(encoderData, 0, encoderDataSize);

        // Copy Y plane: crop top-left of source into encoder layout
        for (int row = 0; row < copyHeight; row++)
        {
            int srcOff = row * srcYStride;
            int dstOff = row * stride;
            if (srcOff + copyWidth > frameData.Length) break;
            if (dstOff + copyWidth > encoderDataSize) break;
            System.Buffer.BlockCopy(frameData, srcOff, encoderData, dstOff, copyWidth);
        }

        // Copy UV plane: NV21 (VU interleaved) → NV12 (UV interleaved) with U/V swap
        int copyUvHeight = Math.Min(actualSrcHeight / 2, _height / 2);
        int copyUvWidth = (copyWidth / 2) * 2; // Ensure even for chroma pairs
        for (int row = 0; row < copyUvHeight; row++)
        {
            int srcOff = srcUvStart + row * srcYStride;
            int dstOff = dstYPlaneSize + row * stride;
            if (srcOff + copyUvWidth > frameData.Length) break;
            if (dstOff + copyUvWidth > encoderDataSize) break;
            // Swap V,U → U,V while copying
            for (int i = 0; i < copyUvWidth - 1; i += 2)
            {
                encoderData[dstOff + i] = frameData[srcOff + i + 1];     // U
                encoderData[dstOff + i + 1] = frameData[srcOff + i];     // V
            }
        }

        int writeSize = Math.Min(encoderDataSize, inputBuffer.Capacity());
        inputBuffer.Put(encoderData, 0, writeSize);
        _bufferPool.Return(encoderData);
        return writeSize;
    }

    /// <summary>
    /// Crops and de-strides a YUV420/NV21 frame from source resolution to encoder resolution.
    /// Handles stride padding (row stride > image width) by doing row-by-row copies.
    /// The NV21 UV plane is always at offset width * height (declared dimensions),
    /// even when the buffer is oversized (e.g., MediaTek padding).
    /// </summary>
    private byte[] CropAndDestrideFrame(byte[] frameData, int srcWidth, int srcHeight, int dstWidth, int dstHeight)
    {
        int dstSize = (dstWidth * dstHeight * 3) / 2;

        // If source dimensions are unknown, try to infer or fall back to simple handling
        if (srcWidth <= 0 || srcHeight <= 0)
        {
            if (frameData.Length >= dstSize)
            {
                byte[] truncated = new byte[dstSize];
                System.Buffer.BlockCopy(frameData, 0, truncated, 0, dstSize);
                return truncated;
            }
            byte[] padded = new byte[dstSize];
            System.Buffer.BlockCopy(frameData, 0, padded, 0, Math.Min(frameData.Length, dstSize));
            return padded;
        }

        // Detect stride padding (wider rows) from oversized buffers.
        // Note: MediaTek cameras may produce oversized buffers (e.g., 1843198 bytes for
        // 1280x720) but the NV21 UV plane always starts at width * height. The extra bytes
        // are buffer padding, NOT extra Y rows. Do not inflate actualSrcHeight.
        int srcStride = srcWidth;
        int actualSrcHeight = srcHeight;
        int expectedSize = (srcWidth * srcHeight * 3) / 2;

        if (frameData.Length > expectedSize)
        {
            // Check if extra data is from stride padding (wider rows)
            int totalRows = srcHeight + srcHeight / 2;
            int strideFromData = totalRows > 0 ? (frameData.Length + totalRows - 1) / totalRows : srcWidth;
            int sizeFromStride = (strideFromData * srcHeight * 3) / 2;
            bool strideFits = Math.Abs(sizeFromStride - frameData.Length) <= 16 && strideFromData > srcWidth;

            if (strideFits)
            {
                srcStride = strideFromData;
            }
            // Otherwise: oversized buffer with no stride padding — use declared dimensions
        }

        // Sanity check: stride should be >= source width
        if (srcStride < srcWidth)
            srcStride = srcWidth;

        // Log crop parameters once for debugging
        if (_lastLoggedSize != frameData.Length)
        {
            Log.Info("H264", $"CropAndDestride: src={srcWidth}x{srcHeight} actual={srcStride}x{actualSrcHeight}, dst={dstWidth}x{dstHeight}, data={frameData.Length}");
        }

        // If source and dest are same resolution and no stride padding, return as-is
        if (srcStride == dstWidth && actualSrcHeight == dstHeight && frameData.Length == dstSize)
            return frameData;

        // Fast path: no stride padding and width matches — contiguous crop
        if (srcStride == dstWidth && srcWidth == dstWidth && actualSrcHeight >= dstHeight)
        {
            byte[] result = new byte[dstSize];
            // Copy Y plane: first dstHeight rows contiguously
            int yBytes = dstWidth * dstHeight;
            System.Buffer.BlockCopy(frameData, 0, result, 0, yBytes);
            // Copy UV plane: starts at srcWidth * srcHeight (declared height, not buffer height)
            int srcUvStart = srcWidth * srcHeight;
            int uvBytes = dstWidth * (dstHeight / 2);
            if (srcUvStart + uvBytes <= frameData.Length)
            {
                System.Buffer.BlockCopy(frameData, srcUvStart, result, yBytes, uvBytes);
            }
            return result;
        }

        // General path: row-by-row copy for stride and/or width mismatch
        {
            byte[] result = new byte[dstSize];
            int copyWidth = Math.Min(srcWidth, dstWidth);
            int copyHeight = Math.Min(actualSrcHeight, dstHeight);

            // Copy Y plane: row by row, taking top-left crop
            int srcOffset = 0;
            int dstOffset = 0;
            for (int row = 0; row < copyHeight; row++)
            {
                if (srcOffset + copyWidth > frameData.Length) break;
                System.Buffer.BlockCopy(frameData, srcOffset, result, dstOffset, copyWidth);
                srcOffset += srcStride;
                dstOffset += dstWidth;
            }

            // Copy VU plane (NV21 interleaved): row by row, taking top-left crop
            // VU plane starts at srcStride * srcHeight (declared height, not buffer height)
            int srcUvStart = srcStride * srcHeight;
            int dstUvStart = dstWidth * dstHeight;
            int copyUvHeight = Math.Min(srcHeight / 2, dstHeight / 2);
            int copyUvWidth = (copyWidth / 2) * 2;

            srcOffset = srcUvStart;
            dstOffset = dstUvStart;
            for (int row = 0; row < copyUvHeight; row++)
            {
                if (srcOffset + copyUvWidth > frameData.Length) break;
                System.Buffer.BlockCopy(frameData, srcOffset, result, dstOffset, copyUvWidth);
                srcOffset += srcStride;
                dstOffset += dstWidth;
            }

            return result;
        }
    }

    private byte[] ConvertNV21ToNV12Pooled(byte[] nv21)
    {
        int ySize = _width * _height;
        int totalSize = ySize + (ySize / 2);
        
        var nv12 = _bufferPool.Rent(totalSize);
        
        try
        {
            // Y plane copy
            System.Buffer.BlockCopy(nv21, 0, nv12, 0, ySize);
            
            // UV swap - vectorized if possible
            unsafe
            {
                fixed (byte* srcPtr = &nv21[ySize], dstPtr = &nv12[ySize])
                {
                    int uvLength = totalSize - ySize;
                    for (int i = 0; i < uvLength - 1; i += 2)
                    {
                        dstPtr[i] = srcPtr[i + 1];
                        dstPtr[i + 1] = srcPtr[i];
                    }
                }
            }
            var result = new byte[totalSize];
            Array.Copy(nv12, 0, result, 0, totalSize);
            _bufferPool.Return(nv12);
            return result;
        }
        catch
        {
            _bufferPool.Return(nv12);
            throw;
        }
    }
    private bool DrainOutputBuffer(MediaCodec.BufferInfo bufferInfo, ref byte[]? sps, ref byte[]? pps, ref bool gotFirstOutput)
    {
        // Check disposed flag BEFORE any JNI calls to prevent SIGSEGV
        if (_disposed || _encoder == null) return false;

        try
        {
            // Wait up to 1ms for output (low latency)
            var outputIndex = _encoder.DequeueOutputBuffer(bufferInfo, 1000);
            
            if (outputIndex >= 0)
            {
                gotFirstOutput = true;
                ByteBuffer? outputBuffer;
                
                if (Build.VERSION.SdkInt >= BuildVersionCodes.Lollipop)
                {
                    outputBuffer = _encoder.GetOutputBuffer(outputIndex);
                }
                else
                {
#pragma warning disable CS0618
                    var buffers = _encoder.GetOutputBuffers();
                    outputBuffer = buffers?[outputIndex];
#pragma warning restore CS0618
                }
                
                if (outputBuffer != null && bufferInfo.Size > 0)
                {
                    var data = new byte[bufferInfo.Size];
                    outputBuffer.Position(bufferInfo.Offset);
                    outputBuffer.Limit(bufferInfo.Offset + bufferInfo.Size);
                    outputBuffer.Get(data);

                    // Check if this is config data
                    if ((bufferInfo.Flags & MediaCodecBufferFlags.CodecConfig) != 0)
                    {
                        ParseConfigFrame(data, ref sps, ref pps);
                        Log.Debug("H264MTK", "Got config frame");
                    }
                    else
                    {
                        // Regular frame - extract all NAL units properly
                        var nalUnits = new List<byte[]>();

                        // MediaTek might not include start codes, so add them
                        if (!HasStartCode(data))
                        {
                            var withStartCode = new byte[data.Length + 4];
                            withStartCode[0] = 0;
                            withStartCode[1] = 0;
                            withStartCode[2] = 0;
                            withStartCode[3] = 1;
                            Array.Copy(data, 0, withStartCode, 4, data.Length);
                            nalUnits.Add(withStartCode);
                        }
                        else
                        {
                            // Extract all NAL units (encoder may output multiple NALs in one buffer)
                            nalUnits = ExtractNalUnits(data);
                        }

                        var frameEvent = new H264FrameEventArgs
                        {
                            NalUnits = nalUnits,
                            IsKeyFrame = (bufferInfo.Flags & MediaCodecBufferFlags.KeyFrame) != 0,
                            Timestamp = bufferInfo.PresentationTimeUs,
                            Sps = sps,
                            Pps = pps
                        };

                        FrameEncoded?.Invoke(this, frameEvent);
                    }
                }
                
                _encoder.ReleaseOutputBuffer(outputIndex, false);
                return true;
            }
            else if (outputIndex == (int)MediaCodecInfoState.OutputFormatChanged)
            {
                var format = _encoder.OutputFormat;
                Log.Debug("H264MTK", $"Output format changed: {format}");
                
                // Extract SPS/PPS from format
                ExtractParameterSets(format, ref sps, ref pps);
                return true;
            }
            else if (outputIndex == (int)MediaCodecInfoState.OutputBuffersChanged)
            {
                Log.Debug("H264MTK", "Output buffers changed");
                return true;
            }
            
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("H264MTK", $"Drain output error: {ex.Message}");
            return false;
        }
    }
    
    private bool HasStartCode(byte[] data)
    {
        if (data.Length < 4) return false;
        return (data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 1) ||
               (data[0] == 0 && data[1] == 0 && data[2] == 1);
    }

    private void ParseConfigFrame(byte[] data, ref byte[]? sps, ref byte[]? pps)
    {
        // Parse the config frame for SPS/PPS
        var nalUnits = ExtractNalUnits(data);
        foreach (var nal in nalUnits)
        {
            // Determine start code length (3 or 4 bytes)
            int offset = 0;
            if (nal.Length >= 4 && nal[0] == 0 && nal[1] == 0 && nal[2] == 0 && nal[3] == 1)
                offset = 4;
            else if (nal.Length >= 3 && nal[0] == 0 && nal[1] == 0 && nal[2] == 1)
                offset = 3;

            if (offset > 0 && nal.Length > offset)
            {
                var nalType = nal[offset] & 0x1F;
                if (nalType == 7) sps = nal;
                else if (nalType == 8) pps = nal;
            }
        }
    }

    private List<byte[]> ExtractNalUnits(byte[] data)
    {
        var nalUnits = new List<byte[]>();

        if (data.Length < 4)
        {
            if (data.Length > 0) nalUnits.Add(data);
            return nalUnits;
        }

        int i = 0;

        // Find first start code
        while (i < data.Length - 3)
        {
            bool is4Byte = data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1;
            bool is3Byte = data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1;

            if (is4Byte || is3Byte)
            {
                int startCodeLen = is4Byte ? 4 : 3;
                int nalStart = i;
                int nalEnd = data.Length;

                // Find next start code
                for (int j = i + startCodeLen; j < data.Length - 2; j++)
                {
                    bool next4Byte = (j + 3 < data.Length) &&
                                     data[j] == 0 && data[j + 1] == 0 && data[j + 2] == 0 && data[j + 3] == 1;
                    bool next3Byte = data[j] == 0 && data[j + 1] == 0 && data[j + 2] == 1;

                    if (next4Byte || next3Byte)
                    {
                        nalEnd = j;
                        break;
                    }
                }

                int nalLength = nalEnd - nalStart;
                if (nalLength > 0)
                {
                    var nalUnit = new byte[nalLength];
                    System.Buffer.BlockCopy(data, nalStart, nalUnit, 0, nalLength);
                    nalUnits.Add(nalUnit);
                }

                i = nalEnd;
            }
            else
            {
                i++;
            }
        }

        // If no NAL units found, return the entire data as single NAL
        if (nalUnits.Count == 0 && data.Length > 0)
        {
            nalUnits.Add(data);
        }

        return nalUnits;
    }
    
    private void ExtractParameterSets(MediaFormat format, ref byte[]? sps, ref byte[]? pps)
    {
        try
        {
            // Try csd-0 (may contain SPS only, or SPS+PPS concatenated on some devices)
            if (format.ContainsKey("csd-0"))
            {
                var csd0Buffer = format.GetByteBuffer("csd-0");
                if (csd0Buffer != null)
                {
                    var csd0 = new byte[csd0Buffer.Remaining()];
                    csd0Buffer.Get(csd0);
                    csd0Buffer.Rewind();

                    // Split in case csd-0 contains both SPS and PPS
                    var nalUnits = ExtractNalUnits(csd0);
                    foreach (var nal in nalUnits)
                    {
                        int offset = 0;
                        if (nal.Length >= 4 && nal[0] == 0 && nal[1] == 0 && nal[2] == 0 && nal[3] == 1)
                            offset = 4;
                        else if (nal.Length >= 3 && nal[0] == 0 && nal[1] == 0 && nal[2] == 1)
                            offset = 3;

                        if (offset > 0 && nal.Length > offset)
                        {
                            int nalType = nal[offset] & 0x1F;
                            if (nalType == 7) sps = nal;
                            else if (nalType == 8) pps = nal;
                        }
                    }
                    Log.Debug("H264Encoder", $"Got SPS/PPS from csd-0: {csd0.Length} bytes, {nalUnits.Count} NAL(s)");
                }
            }

            // Try csd-1 (dedicated PPS buffer, if present)
            if (format.ContainsKey("csd-1"))
            {
                var ppsBuffer = format.GetByteBuffer("csd-1");
                if (ppsBuffer != null)
                {
                    pps = new byte[ppsBuffer.Remaining()];
                    ppsBuffer.Get(pps);
                    ppsBuffer.Rewind();
                    Log.Debug("H264Encoder", $"Got PPS from csd-1: {pps.Length} bytes");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("H264Encoder", $"Error extracting parameter sets: {ex.Message}");
        }
    }
    
    private byte[] ConvertNV21ToNV12(byte[] nv21)
    {
        // NV21 (YVU) to NV12 (YUV) conversion
        // Y plane is the same, just swap U and V in the interleaved plane
        
        int ySize = _width * _height;
        int uvSize = ySize / 2;
        
        if (nv21.Length < ySize + uvSize)
        {
            Log.Error("H264MTK", $"Invalid frame size: {nv21.Length}, expected: {ySize + uvSize}");
            return nv21; // Return as-is to avoid crash
        }
        
        byte[] nv12 = new byte[nv21.Length];
        
        // Copy Y plane
        Array.Copy(nv21, 0, nv12, 0, ySize);
        
        // Swap U and V
        for (int i = ySize; i < nv21.Length - 1; i += 2)
        {
            nv12[i] = nv21[i + 1];     // U
            nv12[i + 1] = nv21[i];     // V
        }
        
        return nv12;
    }
    
    /// <summary>
    /// Stops the encoder and releases all resources.
    /// Sets _disposed flag FIRST to stop encoder loop before any JNI cleanup.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            // Set disposed flag FIRST to stop encoder loop from accessing Java objects
            _disposed = true;
            _isRunning = false;

            // Complete the frame channel to unblock any waiting reads
            _frameChannel.Writer.TryComplete();

            // Wait for encoder thread to fully stop BEFORE touching MediaCodec
            // This prevents SIGSEGV from encoder thread accessing disposed objects
            if (_encoderThread != null && _encoderThread.IsAlive)
            {
                if (!_encoderThread.Join(500))
                {
                    Log.Warn("H264MTK", "Encoder thread did not stop within 500ms timeout");
                }
            }

            // Now safe to stop and release MediaCodec - encoder thread has exited
            try
            {
                _encoder?.Stop();

                // Release with timeout to prevent ANR on some devices (especially MediaTek)
                var releaseTask = Task.Run(() =>
                {
                    try { _encoder?.Release(); }
                    catch { }
                });

                if (!releaseTask.Wait(TimeSpan.FromSeconds(0.5)))
                {
                    Log.Warn("H264MTK", "Encoder.Release() timed out (500ms) - may cause resource leak");
                }
            }
            catch (System.Exception ex)
            {
                Log.Error("H264MTK", $"Error stopping encoder: {ex.Message}");
            }

            _encoder = null;
            Log.Info("H264MTK", "Encoder stopped and disposed");
        }
    }
    
    /// <summary>
    /// Releases all resources used by the encoder.
    /// </summary>
    public void Dispose()
    {
        Stop();
        //_frameQueue?.Dispose();
    }
}