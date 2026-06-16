using Android.Media;
using Android.OS;
using Android.Runtime;
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
    private int _encodeHeight;  // Encoding height = _height + one extra macroblock row (MediaTek crop workaround)
    private int _bitrate;
    private readonly int _frameRate;
    private volatile bool _isRunning;

    /// <summary>
    /// Gets whether the encoder is currently running.
    /// </summary>
    public bool IsRunning => _isRunning;
    private Thread? _encoderThread;
    private readonly Channel<FrameData> _frameChannel;
    private readonly ArrayPool<byte> _bufferPool = ArrayPool<byte>.Create();

    private readonly object _lock = new();
    private volatile bool _disposed;  // Prevents JNI access after disposal

    /// <summary>
    /// Optional text overlay stamped into each frame's Y plane before encoding.
    /// Set after construction; reads are lock-free (reference assignment is atomic on ARM).
    /// </summary>
    public Services.FrameOverlay? Overlay { get; set; }

    /// <summary>
    /// When <c>true</c>, the H.264 Main profile (CABAC) is used if the hardware encoder
    /// supports it; otherwise Baseline (CAVLC). Default is <c>false</c> (Baseline): on
    /// MediaTek VENC, CABAC encode time scales with the entropy-coded bit count, so at high
    /// resolutions Main throttles the frame rate exactly when motion inflates frame sizes.
    /// Wired from <c>ServerConfiguration.PreferMainProfile</c>. Applies to encoders started
    /// after the value changes (not to an already-running encoder).
    /// </summary>
    public static volatile bool PreferMainProfile = false;

    /// <summary>
    /// H.264 keyframe (IDR) interval in seconds, used as a scheduled backstop. Default 5.
    /// The primary reference-chain recovery is the drop-triggered IDR in the streaming loop;
    /// this bounds worst-case corruption if a drop is somehow missed. Lower = faster recovery
    /// but more IDR encode stalls (each ~120-220ms on MT6768); higher = smoother but slower
    /// recovery. Wired from <c>ServerConfiguration.KeyFrameIntervalSeconds</c>. Applies to
    /// encoders started after the value changes.
    /// </summary>
    public static volatile int KeyFrameIntervalSeconds = 5;

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

    // Output-rate diagnostic: counts encoded frames over a rolling window and logs the
    // achieved FPS plus average frame size, so a frame-rate drop can be attributed to the
    // encoder (low FPS here) vs. the network/send path (full FPS here but stutter at client).
    private int _outFrameCount;
    private long _outBytesInWindow;
    private long _outFpsWindowStart;

    // Monotonic output-order frame counter (see H264FrameEventArgs.FrameNumber). Only touched
    // on the single encoding thread, so no synchronization needed.
    private long _outputFrameNumber;
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
    /// Gets the number of luma rows to crop from the bottom in the SPS crop rect.
    /// Non-zero when <see cref="_encodeHeight"/> > <see cref="_height"/> (MediaTek crop workaround).
    /// </summary>
    public int CropBottomOffset => _encodeHeight > _height ? (_encodeHeight - _height) / 2 : 0;

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

        /// <summary>
        /// Stopwatch ticks when this frame was queued to the encoder input channel.
        /// Used for encoder pipeline latency diagnostics.
        /// </summary>
        public long QueuedAt { get; set; }
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
        _frameIntervalUs = 1_000_000L / frameRate; // e.g., 33333us for 30fps

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
                BaluLogger.Info("H264MTK", $"Encoder selection cached: {_cachedBestEncoder?.Name ?? "none"}");
            }
            return _cachedBestEncoder!;
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
                    BaluLogger.Debug("EncoderSelector", $"Evaluated {encoderInfo.Name}: Score={encoderInfo.Score}");
                }
            }
        }

        // Sort by score (highest first)
        encoders.Sort((a, b) => b.Score.CompareTo(a.Score));

        // Log the ranking
        BaluLogger.Debug("EncoderSelector", "Encoder ranking:");
        
        foreach (var encoder in encoders)
        {
            BaluLogger.Debug("EncoderSelector", $"  {encoder.Name}: {encoder.Score} points");
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
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
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
                if (OperatingSystem.IsAndroidVersionAtLeast(31))
                {
                    if (encoderCaps.IsBitrateModeSupported(BitrateMode.CbrFd))
                    {
                        encoderInfo.SupportsBitrateMode = true;
                        encoderInfo.Score += 5;
                    }
                }

                // Check for quality levels support
                if (OperatingSystem.IsAndroidVersionAtLeast(28) && encoderCaps.QualityRange != null)
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

                encoderInfo.SupportsMainProfile = supportsMain;
            }

            return encoderInfo;
        }
        catch (Exception e)
        {
            BaluLogger.Error("EncoderSelector", $"Error evaluating encoder {codecInfo.Name}: {e.Message}");
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
        // Probe the aligned encode height (see EncodeHeightFor), clamped to the encoder max so the
        // top resolution (2K) isn't rejected, so non-16-aligned heights like 1080p aren't falsely
        // rejected and downgraded before _encodeHeight is even computed.
        return _bestEncoder.Capabilities.VideoCapabilities.IsSizeSupported(width, ClampedEncodeHeight(height, _bestEncoder.MaxSupportedHeight));
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
            if (w <= width && h <= height && videoCaps.IsSizeSupported(w, ClampedEncodeHeight(h, _bestEncoder?.MaxSupportedHeight ?? 0)))
                return (w, h);
        }
        return (640, 480); // Safe fallback
    }

    /// <summary>
    /// The height the encoder is actually configured at for a given display height: rounded up to
    /// a 16px macroblock boundary plus one extra row pair (the MediaTek crop workaround applied in
    /// <see cref="Start"/>). Capability checks must probe this height, not the raw display height,
    /// or non-16-aligned heights such as 1080p are wrongly reported as unsupported.
    /// </summary>
    private static int EncodeHeightFor(int displayHeight) => ((displayHeight / 16) + 2) * 16;

    /// <summary>
    /// <see cref="EncodeHeightFor"/> clamped so it never exceeds the encoder's maximum supported
    /// height (aligned down to a 16px macroblock boundary). The crop workaround adds two macroblock
    /// rows; at the encoder's top resolution that overshoots the hardware cap — e.g. 2560x1440 would
    /// become 2560x1472, and configure() fails with -EINVAL. Clamping lets the maximum resolution
    /// (2K) configure; the only cost is that the padding/crop row is dropped at that one size, so the
    /// MediaTek bottom-row chroma artifact may be visible there. Pass maxSupportedHeight = 0 to skip
    /// the clamp (capabilities unknown).
    /// </summary>
    private static int ClampedEncodeHeight(int displayHeight, int maxSupportedHeight)
    {
        int desired = EncodeHeightFor(displayHeight);
        int maxAligned = (maxSupportedHeight / 16) * 16;
        return maxAligned > 0 ? Math.Min(desired, maxAligned) : desired;
    }

    /// <summary>
    /// Probes the hardware encoder to find the nearest supported resolution without
    /// actually creating or starting an encoder. Use this to configure the camera at
    /// a resolution the encoder can handle, avoiding a costly camera restart.
    /// Uses the cached encoder selection — does not re-scan the codec list.
    /// </summary>
    public static (int width, int height) ProbeSupportedResolution(int requestedWidth, int requestedHeight)
    {
        try
        {
            // Reuse the cached encoder selection — scanning all codecs again wastes 200-700ms.
            var best = GetCachedBestEncoder();
            if (best?.Capabilities?.VideoCapabilities == null)
                return (requestedWidth, requestedHeight);

            var videoCaps = best.Capabilities.VideoCapabilities;

            // IMPORTANT: probe the height Start() will actually configure, not the raw height.
            // Start() rounds the encode height up to a macroblock boundary (see _encodeHeight:
            // ((h / 16) + 2) * 16) for the MediaTek crop workaround, then crops on output. Testing
            // the raw height here falsely rejects sizes whose height isn't a multiple of 16 — e.g.
            // 1080p (1080 % 16 == 8), which was silently downgraded to 720p. Conversely it also
            // catches sizes whose aligned height exceeds the encoder max (e.g. QHD 1440 → 1472 > the
            // 1440 cap), so those correctly fall through to a size that will actually configure.
            if (videoCaps.IsSizeSupported(requestedWidth, ClampedEncodeHeight(requestedHeight, best.MaxSupportedHeight)))
                return (requestedWidth, requestedHeight);

            // Try common resolutions in descending order
            var resolutions = new[] {
                (3840, 2160), (2560, 1440), (1920, 1080), (1280, 720),
                (800, 600), (640, 480), (320, 240)
            };

            foreach (var (w, h) in resolutions)
            {
                if (w <= requestedWidth && h <= requestedHeight && videoCaps.IsSizeSupported(w, ClampedEncodeHeight(h, best.MaxSupportedHeight)))
                {
                    BaluLogger.Info("H264", $"ProbeSupportedResolution: {requestedWidth}x{requestedHeight} not supported, using {w}x{h}");
                    return (w, h);
                }
            }

            return (640, 480);
        }
        catch (Exception ex)
        {
            BaluLogger.Error("H264", $"ProbeSupportedResolution error: {ex.Message}");
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
                    BaluLogger.Warn("H264", $"Resolution {_width}x{_height} not supported, falling back to {newW}x{newH}");
                    _width = newW;
                    _height = newH;
                }

                // Encode at one extra 16-pixel macroblock row above the display height.
                // MediaTek C2MtkVenc corrupts the chroma of the last macroblock row in the
                // encoded bitstream regardless of input data. By pushing that row into padding
                // and setting SPS frame_crop_bottom_offset, the decoder hides it — all real
                // content (rows 0.._height-1) is preserved and displayed correctly.
                // e.g. 480 → 512 (2 extra macroblock rows); clamped to the encoder max so the top
                // resolution (2K, 2560x1440) configures instead of overshooting to 1472 and failing.
                _encodeHeight = ClampedEncodeHeight(_height, _bestEncoder?.MaxSupportedHeight ?? 0);

                // Create format with encoder's supported color format
                var format = MediaFormat.CreateVideoFormat(MediaFormat.MimetypeVideoAvc, _width, _encodeHeight);

                // Prefer NV12 (SemiPlanar) — it has a well-defined buffer layout for raw
                // ByteBuffer writes. COLOR_FormatYUV420Flexible has undefined layout for
                // raw writes and causes green corruption at higher resolutions.
                if (_bestEncoder.ColorFormats.Contains(COLOR_FormatYUV420SemiPlanar))
                {
                    _selectedColorFormat = COLOR_FormatYUV420SemiPlanar; // NV12
                    BaluLogger.Debug("H264", "Using COLOR_FormatYUV420SemiPlanar (NV12)");
                }
                else if (_bestEncoder.ColorFormats.Contains(COLOR_FormatYUV420Flexible))
                {
                    _selectedColorFormat = COLOR_FormatYUV420Flexible;
                    BaluLogger.Warn("H264", "Using COLOR_FormatYUV420Flexible (NV12 unavailable)");
                }
                else
                {
                    _selectedColorFormat = COLOR_FormatYUV420SemiPlanar; // Default fallback
                    BaluLogger.Debug("H264", "Using default COLOR_FormatYUV420SemiPlanar");
                }

                format.SetInteger(MediaFormat.KeyColorFormat, _selectedColorFormat);
                format.SetInteger(MediaFormat.KeyBitRate, _bitrate);
                format.SetInteger(MediaFormat.KeyFrameRate, _frameRate);
                // Use SetInteger (not SetFloat) — MediaTek MT6768 misinterprets sub-second float
                // values as 0, causing EVERY frame to be an IDR keyframe, which exhausts the
                // encoder's internal buffers and causes it to stall after ~1000 frames.
                // IDR frames cause 120-220ms encoding stalls on MT6768, so we don't want a tiny
                // GOP — but a 30s interval (the old value) means any dropped P-frame corrupts the
                // stream for up to 30s. KeyFrameIntervalSeconds (default 5s) is a backstop: the
                // primary recovery is drop-triggered RequestKeyFrame() in the streaming loop, and
                // new clients still get an immediate IDR via RequestKeyFrame(). At 5s the scheduled
                // IDR overhead is ~3% of encode time, negligible for frame rate.
                format.SetInteger(MediaFormat.KeyIFrameInterval, KeyFrameIntervalSeconds);
                
                // Set profile and level for better compatibility.
                // Baseline (CAVLC) is the default: on MediaTek VENC the per-frame encode time
                // scales almost linearly with the entropy-coded bit count under Main/CABAC
                // (measured on MT6768 at 2K: ~98KB frame → 73ms, ~139KB frame → 106ms), so at
                // high resolutions CABAC throttles throughput exactly when motion inflates the
                // frames — frame rate sags during motion. CAVLC trades ~10-15% compression
                // efficiency for substantially faster entropy coding.
                // Main can be opted in via PreferMainProfile (ServerConfiguration.PreferMainProfile)
                // for low resolutions or SoCs with fast CABAC hardware. Main shares Baseline's SPS
                // layout up to the crop fields, so PatchSpsForCrop works with both (it only bails
                // on High/extended profiles).
                bool useMain = PreferMainProfile && _bestEncoder?.SupportsMainProfile == true;
                format.SetInteger(MediaFormat.KeyProfile,
                    (int)(useMain ? MediaCodecProfileType.Avcprofilemain : MediaCodecProfileType.Avcprofilebaseline));
                format.SetInteger(MediaFormat.KeyLevel, 0x100);
                BaluLogger.Info("H264", $"Using H.264 {(useMain ? "Main" : "Baseline")} profile");

                // Low latency configuration
                if (Build.VERSION.SdkInt >= BuildVersionCodes.M) // API 23+
                {
                    format.SetInteger(MediaFormat.KeyPriority, 0); // Real-time priority (added in API 23)
                    format.SetInteger(MediaFormat.KeyOperatingRate, short.MaxValue);

                }

                // NOTE: KeyLatency=0 (zero-frame pipeline depth) was removed deliberately.
                // It forces a single frame in flight, so throughput collapses to
                // 1 / hw_encode_time — ~11fps at 2560x1440 on MT6768 (~90ms/frame) even
                // though the camera delivers 30fps. Harmless at VGA (encode is a few ms),
                // catastrophic at high resolutions. Letting MediaCodec keep its natural
                // pipeline depth (a few frames) restores full throughput at the cost of
                // 1-2 frame intervals of extra latency.

                if (OperatingSystem.IsAndroidVersionAtLeast(30)) // API 30+
                {
                    // KeyLowLatency=1 disabled on MT6768 — empirically suppresses the rate
                    // controller. With it on, baseline P-frames stay ~50 KB and the stream
                    // sits at ~10-12 Mbps regardless of KeyBitRate=2_000_000.
                    // format.SetInteger(MediaFormat.KeyLowLatency, 1);
                }

                if (_bestEncoder.Name.Contains("MTK"))
                {
                    try
                    {
                        // NOTE: All MTK low-latency vendor extensions disabled on MT6768.
                        //  - nonrefp.enable=1     → made every frame an IDR (12.7 Mbps actual).
                        //  - low-latency.enable=1 → defeats the rate controller; output stays
                        //    keyframe-sized (~50 KB) even after the all-IDR fix, holding the
                        //    stream at ~10 Mbps and producing 50 KB LOS allocations every frame.
                        // KeyPriority=0 + KeyOperatingRate stay set above; those don't trigger
                        // the rate-control suppression.
                        // format.SetInteger("vendor.mtk-ext-enc-low-latency.enable", 1);
                        // format.SetInteger("vendor.mtk-ext-enc-nonrefp.enable", 1);
                    }
                    catch { }
                }

                // Create encoder
                MediaCodec? encoder = MediaCodec.CreateByCodecName(_bestEncoder.Name);
                if (encoder == null)
                {
                    throw new Exception($"Failed to create encoder: {_bestEncoder.Name}");
                }

                // Intra-refresh: rolling per-frame intra-MB refresh lets a decoder converge
                // within one refresh cycle (~1s here) WITHOUT waiting for the next full IDR.
                // The 5s KeyIFrameInterval was the dominant cause of multi-second join/recovery
                // latency — VLC/ffplay won't render until a recoverable point and effectively
                // buffer toward a keyframe. With intra-refresh the stream is continuously
                // recoverable, so steady-state latency drops and the big periodic IDR bursts
                // (which also bloat the TCP send buffer) are no longer the limiter. This is the
                // standard MediaFormat key (API 28+), NOT a vendor extension, so unlike
                // vendor.mtk-ext-* it does not suppress the MT6768 rate controller. The periodic
                // IDR (KeyIFrameInterval) stays as a backstop and new clients still get an
                // immediate IDR via RequestKeyFrame() on PLAY. Only enabled when the codec
                // advertises FeatureIntraRefresh, so it can never fail Configure() below.
                if (OperatingSystem.IsAndroidVersionAtLeast(28))
                {
                    try
                    {
                        var caps = encoder.CodecInfo?.GetCapabilitiesForType(MediaFormat.MimetypeVideoAvc);
                        // "intra-refresh" is the Java MediaCodecInfo.CodecCapabilities.FEATURE_IntraRefresh
                        // value; passed as a literal because the .NET binding omits the named constant.
                        if (caps != null && caps.IsFeatureSupported("intra-refresh"))
                        {
                            // Refresh the whole frame over ~1s of frames: cheap on bitrate,
                            // ~1s worst-case convergence. Lower = faster recovery, more intra-MB cost.
                            int refreshPeriod = Math.Clamp(_frameRate, 10, 60);
                            format.SetInteger(MediaFormat.KeyIntraRefreshPeriod, refreshPeriod);
                            BaluLogger.Info("H264", $"Intra-refresh enabled (period={refreshPeriod} frames) for low-latency recovery");
                        }
                        else
                        {
                            BaluLogger.Debug("H264", "Intra-refresh unsupported by encoder; relying on IDR backstop only");
                        }
                    }
                    catch (Exception ex)
                    {
                        BaluLogger.Warn("H264", $"Intra-refresh setup skipped: {ex.Message}");
                    }
                }

                encoder.Configure(format, null, null, MediaCodecConfigFlags.Encode);

                // Now start the encoder
                encoder.Start();

                // Reinforce bitrate after Start() — SetParameters requires a started codec.
                // The bitrate was already set in MediaFormat before Configure(), but some
                // SoCs (MediaTek) may ignore it; this dynamic update ensures compliance.
                if (OperatingSystem.IsAndroidVersionAtLeast(30))
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
                _encoderSliceHeight = _encodeHeight; // Default to configured encode height if query fails
                try
                {
                    var inputFormat = encoder.InputFormat;
                    if (inputFormat != null)
                    {
                        if (OperatingSystem.IsAndroidVersionAtLeast(29))
                        {
                            _encoderStride = inputFormat.GetInteger(MediaFormat.KeyStride, _width);
                            _encoderSliceHeight = inputFormat.GetInteger(MediaFormat.KeySliceHeight, _encodeHeight);
                            BaluLogger.Info("H264", $"Encoder input: stride={_encoderStride}, sliceHeight={_encoderSliceHeight} (video={_width}x{_height}, encode={_encodeHeight})");
                        }

                        // Some encoders return 0 meaning "same as configured"
                        if (_encoderStride <= 0) _encoderStride = _width;
                        if (_encoderSliceHeight <= 0) _encoderSliceHeight = _encodeHeight;
                    }
                }
                catch (Exception ex)
                {
                    BaluLogger.Warn("H264", $"Could not query input format: {ex.Message}");
                }

                // Start encoding thread
                _encoderThread = new Thread(EncodingLoop)
                {
                    IsBackground = true,
                    Name = "H264-Encoder",
                    Priority = System.Threading.ThreadPriority.Highest
                };
                _encoderThread.Start();

                BaluLogger.Info("H264", $"Encoder started successfully: {_bestEncoder.Name}");
                BaluLogger.Info("H264", $"Format: {_width}x{_height} (encode:{_encodeHeight}) @ {_frameRate}fps, {_bitrate}bps, cropBottom={CropBottomOffset}");
                BaluLogger.Info("H264", $"Color format: {_selectedColorFormat}");
                
                return true;
            }
            catch (Exception ex)
            {
                BaluLogger.Error("H264", $"Failed to start encoder: {ex.Message}");
                BaluLogger.Error("H264", $"Stack trace: {ex.StackTrace}");
                
                try
                {
                    _encoder?.Stop();
                    _encoder?.Release();
                }
                catch (Exception releaseEx)
                {
                    BaluLogger.Error("H264", $"Error releasing encoder: {releaseEx.Message}");
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
                BaluLogger.Debug("H264MTK", "IDR keyframe requested for new client");
            }
            catch (Exception ex)
            {
                BaluLogger.Warn("H264MTK", $"RequestKeyFrame failed: {ex.Message}");
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
                
                BaluLogger.Debug("H264MTK", $"Bitrate updated to: {newBitrate}bps");
            }
            catch (Exception ex)
            {
                BaluLogger.Error("H264MTK", $"Failed to update bitrate: {ex.Message}");
            }
        }
    }
    /// <summary>
    /// Queues a raw YUV frame for encoding with the caller-provided timestamp.
    /// The frame will be processed by the EncodingLoop thread, ensuring all
    /// MediaCodec access is serialized on a single thread.
    /// </summary>
    /// <param name="frameData">The raw YUV420 frame data.</param>
    /// <param name="timestamp">
    /// The presentation timestamp in <b>microseconds</b>. WARNING: camera2 sensor timestamps
    /// are nanoseconds — divide by 1000 first. Feeding nanoseconds makes consecutive frames
    /// appear 1000x further apart, which drives the MediaTek time-based GOP logic to force an
    /// IDR on every frame and breaks the rate controller (see H264EncoderManager.FeedFrame).
    /// </param>
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
            BaluLogger.Error("H264", $"Invalid frame size: {frameData.Length}, expected: {expectedSize} (encoder: {_width}x{_height})");
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
        _frameChannel.Writer.TryWrite(new() { Data = frameData, Timestamp = timestamp, QueuedAt = Stopwatch.GetTimestamp() });
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

        BaluLogger.Debug("H264MTK", "Encoding loop started");

        try
        {
            bufferInfo = new MediaCodec.BufferInfo();
            SpinWait spinWait = default; // persisted across iterations so escalation (spin→yield→sleep) works correctly

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
                            BaluLogger.Error("H264MTK", $"Output stall detected: no output for {sinceLastOutput / Stopwatch.Frequency}s while input is active (gotFirstOutput={gotFirstOutput}) — stopping encoder");
                            _isRunning = false;
                            break;
                        }
                    }

                    // SpinWait auto-escalates: spin → yield → short sleep, giving sub-ms
                    // wake-up instead of Thread.Sleep(1) which sleeps 1-15ms on Android.
                    // The SpinWait is declared outside the loop so its count accumulates and
                    // it properly escalates from spinning to yielding/sleeping when idle.
                    if (!processedInput && !processedOutput)
                    {
                        spinWait.SpinOnce();
                    }
                    else
                    {
                        spinWait.Reset(); // had work — reset so next idle period starts fresh
                    }

                    // Reset error counter on successful iteration
                    consecutiveErrors = 0;
                }
                catch (System.Exception ex)
                {
                    // Don't log errors during disposal - they're expected
                    if (_disposed) break;

                    consecutiveErrors++;
                    BaluLogger.Error("H264MTK", $"Encoding loop error ({consecutiveErrors}/{maxConsecutiveErrors}): {ex.Message}");

                    // Break out of loop if too many consecutive errors - encoder likely in bad state
                    if (consecutiveErrors >= maxConsecutiveErrors)
                    {
                        BaluLogger.Error("H264MTK", "Too many consecutive errors - stopping encoder loop");
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

        BaluLogger.Debug("H264MTK", "Encoding loop ended");
    }
    
    /// <summary>
    /// Feeds a frame into the encoder's input buffer.
    /// Handles color format conversion and stride padding as needed.
    /// </summary>
    private bool _loggedFirstFeed = false;
    private int _writeFrameLogCounter = 0;

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

                    // Stamp text overlay into Y plane before encoding (if set).
                    // Uses the source width as the Y-plane row stride (NV21: UV at width*height).
                    Overlay?.StampInto(frameData,
                        frame.SourceWidth  > 0 ? frame.SourceWidth  : _width,
                        frame.SourceHeight > 0 ? frame.SourceHeight : _height);

                    // One-time diagnostic log
                    if (!_loggedFirstFeed)
                    {
                        _loggedFirstFeed = true;
                        BaluLogger.Info("H264", $"DIAG feed: frameLen={frameData.Length}, expected={expectedSize}, encoder={_width}x{_height}, src={frame.SourceWidth}x{frame.SourceHeight}, bufCap={inputBuffer.Capacity()}, stride={_encoderStride}, slice={_encoderSliceHeight}, colorFmt={_selectedColorFormat}");
                    }

                    // Write frame directly to encoder buffer with correct stride/sliceHeight layout
                    // This avoids intermediate allocations and handles the MediaTek sliceHeight > height case
                    int dataSize = WriteFrameToEncoderBuffer(inputBuffer, frameData,
                        frame.SourceWidth, frame.SourceHeight);

                    _encoder.QueueInputBuffer(inputIndex, 0, dataSize, frame.Timestamp, 0);
                    _lastTimestamp = frame.Timestamp;
                    // Release the JNI GREF immediately after submitting. After QueueInputBuffer
                    // the Java ByteBuffer is marked inaccessible by the Android runtime, so this
                    // wrapper is already dead. Disposing it just drops the C# JNI handle —
                    // it does NOT touch native memory, which MediaCodec owns and manages.
                    inputBuffer.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            BaluLogger.Error("H264MTK", $"Feed input error: {ex.Message}");
        }
    }
    /// <summary>
    /// Writes camera frame data directly to the encoder's input buffer.
    /// Performs NV21 → NV12 conversion (V/U byte swap) and stride/sliceHeight layout
    /// in a single pass. Only zeroes unwritten padding bytes — the hot path (camera
    /// resolution == encoder resolution, no stride) writes every byte and calls no
    /// <see cref="Array.Clear"/> at all.
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

        // Deduce sliceHeight from buffer capacity if query failed (defaults matched encode height)
        if (sliceHeight == _encodeHeight && inputBuffer.Capacity() > totalDstSize + stride)
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
                BaluLogger.Info("H264", $"Deduced encoder sliceHeight={sliceHeight} from bufCap={inputBuffer.Capacity()} (stride={stride})");
            }
        }

        if (_writeFrameLogCounter++ % 300 == 0)
        {
            BaluLogger.Info("H264", $"WriteFrame: src={srcWidth}x{srcHeight}, enc={_width}x{_height}, stride={stride}, slice={sliceHeight}, bufCap={inputBuffer.Capacity()}, srcUvStart={srcUvStart}, dstYPlane={dstYPlaneSize}, frameLen={frameData.Length}");
        }

        int copyWidth = Math.Min(srcWidth, _width);
        int copyHeight = Math.Min(actualSrcHeight, _height);

        // Build the output in a pooled byte array matching the encoder's expected layout
        int encoderDataSize = Math.Min(totalDstSize, inputBuffer.Capacity());
        byte[] encoderData = _bufferPool.Rent(encoderDataSize);
        // Do NOT clear the entire buffer — only zero bytes that won't be overwritten.
        // Hot path (camera res == encoder res, stride == width): every byte is written → no clearing at all.

        // Copy Y plane: crop top-left of source into encoder layout
        for (int row = 0; row < copyHeight; row++)
        {
            int srcOff = row * srcYStride;
            int dstOff = row * stride;
            if (srcOff + copyWidth > frameData.Length) break;
            if (dstOff + copyWidth > encoderDataSize) break;
            System.Buffer.BlockCopy(frameData, srcOff, encoderData, dstOff, copyWidth);
            // Zero right-side column padding only when stride > copyWidth
            if (stride > copyWidth)
                Array.Clear(encoderData, dstOff + copyWidth, stride - copyWidth);
        }
        // Fill bottom Y padding rows: duplicate last real row into the extra-macroblock region
        // so the MediaTek encoder has valid input for the rows that will be hidden by the SPS crop.
        // Zero any additional rows beyond _encodeHeight (encoder alignment padding).
        if (copyHeight < sliceHeight)
        {
            int encRows = Math.Min(_encodeHeight, sliceHeight);
            if (copyHeight > 0 && encRows > copyHeight)
            {
                int lastRowOff = (copyHeight - 1) * stride;
                for (int r = copyHeight; r < encRows; r++)
                {
                    int dstOff = r * stride;
                    System.Buffer.BlockCopy(encoderData, lastRowOff, encoderData, dstOff, copyWidth);
                    if (stride > copyWidth) Array.Clear(encoderData, dstOff + copyWidth, stride - copyWidth);
                }
            }
            if (encRows < sliceHeight)
                Array.Clear(encoderData, encRows * stride, dstYPlaneSize - encRows * stride);
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
            // Swap V,U → U,V while copying. Each interleaved VU pair is one little-endian
            // ushort, so a vectorized 16-bit byte-reverse performs the whole row's swap with
            // SIMD instead of a per-byte managed loop (~1.8 MB/frame at 2K, which alone cost
            // ~15-20ms/frame on the encoder thread's single core).
            var srcRow = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(frameData.AsSpan(srcOff, copyUvWidth));
            var dstRow = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(encoderData.AsSpan(dstOff, copyUvWidth));
            System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(srcRow, dstRow);
            // Fill UV column padding with neutral chroma (0x80) — U=128, V=128 is gray.
            // Cleared-to-zero UV (U=0, V=0) decodes to green (R:0 G:135 B:0 in BT.601).
            if (stride > copyUvWidth)
                new Span<byte>(encoderData, dstOff + copyUvWidth, stride - copyUvWidth).Fill(0x80);
        }
        // Scan the last 16 NV12 UV rows for the deepest non-zero row and copy it
        // over any zeroed rows below it. Defense-in-depth: SanitizeNv21BottomUv already
        // patched the NV21 source, but the NV21→NV12 swap may expose new zeros.
        const int uvScanLimit = 16;
        int scanEnd = Math.Max(0, copyUvHeight - uvScanLimit);
        int lastGoodUvRow = -1;
        for (int r = copyUvHeight - 1; r >= scanEnd; r--)
        {
            int off = dstYPlaneSize + r * stride;
            if (off + 4 > encoderDataSize) continue;
            if (encoderData[off] != 0 || encoderData[off + 1] != 0
             || encoderData[off + 2] != 0 || encoderData[off + 3] != 0)
            { lastGoodUvRow = r; break; }
        }
        if (lastGoodUvRow >= 0 && lastGoodUvRow < copyUvHeight - 1)
        {
            int srcOff = dstYPlaneSize + lastGoodUvRow * stride;
            for (int r = lastGoodUvRow + 1; r < copyUvHeight; r++)
            {
                int dstOff = dstYPlaneSize + r * stride;
                if (dstOff + copyUvWidth > encoderDataSize) break;
                System.Buffer.BlockCopy(encoderData, srcOff, encoderData, dstOff, copyUvWidth);
            }
        }

        // Fill bottom UV padding rows: duplicate last real UV row into the extra-macroblock region.
        // Fill any additional rows beyond _encodeHeight/2 with neutral chroma (0x80 = gray).
        if (copyUvHeight < sliceHeight / 2)
        {
            int encUvRows = Math.Min(_encodeHeight / 2, sliceHeight / 2);
            if (copyUvHeight > 0 && encUvRows > copyUvHeight)
            {
                int lastUvOff = dstYPlaneSize + (copyUvHeight - 1) * stride;
                for (int r = copyUvHeight; r < encUvRows; r++)
                {
                    int dstOff = dstYPlaneSize + r * stride;
                    System.Buffer.BlockCopy(encoderData, lastUvOff, encoderData, dstOff, copyUvWidth);
                    if (stride > copyUvWidth)
                        new Span<byte>(encoderData, dstOff + copyUvWidth, stride - copyUvWidth).Fill(0x80);
                }
            }
            if (encUvRows < sliceHeight / 2)
            {
                int uvRemStart = dstYPlaneSize + encUvRows * stride;
                new Span<byte>(encoderData, uvRemStart, dstUvPlaneSize - encUvRows * stride).Fill(0x80);
            }
        }

        int writeSize = Math.Min(encoderDataSize, inputBuffer.Capacity());

        // Write through the direct buffer's native address with a single Marshal.Copy.
        // The Java binding for ByteBuffer.Put(byte[], int, int) marshals the managed array
        // into a brand-new Java byte[] (5.5 MB at 2K), then Java copies it again into the
        // codec buffer — two extra 5.5 MB copies plus 5.5 MB of Java GC garbage PER FRAME.
        // That overhead kept the H264-Encoder thread at ~85% of a core and capped the
        // pipeline at ~12fps at 2K. MediaCodec input buffers are always direct, but keep
        // the Put() fallback in case a vendor returns a non-direct buffer.
        IntPtr direct = JNIEnv.GetDirectBufferAddress(inputBuffer.Handle);
        if (direct != IntPtr.Zero)
        {
            System.Runtime.InteropServices.Marshal.Copy(encoderData, 0, direct, writeSize);
        }
        else
        {
            inputBuffer.Put(encoderData, 0, writeSize);
        }

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
            BaluLogger.Info("H264", $"CropAndDestride: src={srcWidth}x{srcHeight} actual={srcStride}x{actualSrcHeight}, dst={dstWidth}x{dstHeight}, data={frameData.Length}");
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

                try
                {
                    if (outputBuffer != null && bufferInfo.Size > 0)
                    {
                        var data = new byte[bufferInfo.Size];

                        // Read through the direct buffer's native address with one Marshal.Copy.
                        // The Java binding for ByteBuffer.Get(byte[]) allocates a Java-side array,
                        // copies into it, then copies back to managed — two extra copies plus Java
                        // GC garbage per encoded frame. Get() is kept as a fallback for non-direct
                        // buffers.
                        IntPtr direct = JNIEnv.GetDirectBufferAddress(outputBuffer.Handle);
                        if (direct != IntPtr.Zero)
                        {
                            System.Runtime.InteropServices.Marshal.Copy(direct + bufferInfo.Offset, data, 0, bufferInfo.Size);
                        }
                        else
                        {
                            outputBuffer.Position(bufferInfo.Offset);
                            outputBuffer.Limit(bufferInfo.Offset + bufferInfo.Size);
                            outputBuffer.Get(data);
                        }

                        // Check if this is config data
                        if ((bufferInfo.Flags & MediaCodecBufferFlags.CodecConfig) != 0)
                        {
                            ParseConfigFrame(data, ref sps, ref pps);
                            BaluLogger.Debug("H264MTK", "Got config frame");
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
                                Pps = pps,
                                EncodedAt = Stopwatch.GetTimestamp(),
                                FrameNumber = ++_outputFrameNumber
                            };

                            FrameEncoded?.Invoke(this, frameEvent);

                            // Output-rate diagnostic — log achieved FPS + avg frame size every ~5s.
                            long nowTicks = Stopwatch.GetTimestamp();
                            if (_outFpsWindowStart == 0) _outFpsWindowStart = nowTicks;
                            _outFrameCount++;
                            _outBytesInWindow += data.Length;
                            long windowTicks = nowTicks - _outFpsWindowStart;
                            if (windowTicks >= Stopwatch.Frequency * 5)
                            {
                                double seconds = (double)windowTicks / Stopwatch.Frequency;
                                double fps = _outFrameCount / seconds;
                                double avgKb = _outBytesInWindow / (double)_outFrameCount / 1024.0;
                                double mbps = _outBytesInWindow * 8.0 / seconds / 1_000_000.0;
                                BaluLogger.Info("H264", $"Output rate: {fps:F1} fps, avg {avgKb:F1} KB/frame, {mbps:F2} Mbps (target {_frameRate}fps @ {_bitrate}bps)");
                                _outFrameCount = 0;
                                _outBytesInWindow = 0;
                                _outFpsWindowStart = nowTicks;
                            }
                        }
                    }
                }
                finally
                {
                    // Release the slot first so the encoder can immediately reuse it,
                    // then drop the JNI GREF. After ReleaseOutputBuffer the Java ByteBuffer
                    // is inaccessible, so Dispose() only releases the C# JNI handle.
                    _encoder.ReleaseOutputBuffer(outputIndex, false);
                    outputBuffer?.Dispose();
                }
                return true;
            }
            else if (outputIndex == (int)MediaCodecInfoState.OutputFormatChanged)
            {
                var format = _encoder.OutputFormat;
                BaluLogger.Debug("H264MTK", $"Output format changed: {format}");
                
                // Extract SPS/PPS from format
                ExtractParameterSets(format, ref sps, ref pps);
                return true;
            }
            else if (outputIndex == (int)MediaCodecInfoState.OutputBuffersChanged)
            {
                BaluLogger.Debug("H264MTK", "Output buffers changed");
                return true;
            }
            
            return false;
        }
        catch (Exception ex)
        {
            BaluLogger.Error("H264MTK", $"Drain output error: {ex.Message}");
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
                if (nalType == 7) sps = PatchSpsForCrop(nal);
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
                            if (nalType == 7) sps = PatchSpsForCrop(nal);
                            else if (nalType == 8) pps = nal;
                        }
                    }
                    BaluLogger.Debug("H264Encoder", $"Got SPS/PPS from csd-0: {csd0.Length} bytes, {nalUnits.Count} NAL(s)");
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
                    BaluLogger.Debug("H264Encoder", $"Got PPS from csd-1: {pps.Length} bytes");
                }
            }
        }
        catch (Exception ex)
        {
            BaluLogger.Error("H264Encoder", $"Error extracting parameter sets: {ex.Message}");
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
            BaluLogger.Error("H264MTK", $"Invalid frame size: {nv21.Length}, expected: {ySize + uvSize}");
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
    
    // ── SPS crop patch helpers ─────────────────────────────────────────────────────
    // Patches a raw SPS NAL unit (with 3- or 4-byte start code) to add
    // frame_cropping_flag=1 and frame_crop_bottom_offset=CropBottomOffset.
    // This hides the extra macroblock row from decoders while preserving all
    // real content. Called once per SPS, not per frame.

    private byte[] PatchSpsForCrop(byte[] spsNal)
    {
        int cropOffset = CropBottomOffset;
        if (cropOffset <= 0) return spsNal;

        int scLen = 0;
        if (spsNal.Length >= 4 && spsNal[0] == 0 && spsNal[1] == 0 && spsNal[2] == 0 && spsNal[3] == 1)
            scLen = 4;
        else if (spsNal.Length >= 3 && spsNal[0] == 0 && spsNal[1] == 0 && spsNal[2] == 1)
            scLen = 3;
        if (scLen == 0 || scLen + 1 >= spsNal.Length) return spsNal;
        if ((spsNal[scLen] & 0x1F) != 7) return spsNal; // not SPS

        // Build bit list from RBSP body (after NAL header byte)
        var bits = new List<bool>((spsNal.Length - scLen - 1) * 8);
        for (int i = scLen + 1; i < spsNal.Length; i++)
        {
            byte b = spsNal[i];
            for (int k = 7; k >= 0; k--) bits.Add(((b >> k) & 1) == 1);
        }

        int pos = 0;
        try
        {
            int profileIdc = SpsReadBits(bits, ref pos, 8);
            SpsReadBits(bits, ref pos, 8);  // constraint_flags
            SpsReadBits(bits, ref pos, 8);  // level_idc
            SpsReadUe(bits, ref pos);       // seq_parameter_set_id

            // Bail on high profiles — their extensions require full parsing
            if (profileIdc == 100 || profileIdc == 110 || profileIdc == 122 || profileIdc == 244 ||
                profileIdc ==  44 || profileIdc ==  83 || profileIdc ==  86 || profileIdc == 118 ||
                profileIdc == 128 || profileIdc == 138 || profileIdc == 134 || profileIdc == 135)
                return spsNal;

            SpsReadUe(bits, ref pos);       // log2_max_frame_num_minus4
            int pocType = SpsReadUe(bits, ref pos);
            if (pocType == 0)
                SpsReadUe(bits, ref pos);   // log2_max_pic_order_cnt_lsb_minus4
            else if (pocType == 1)
            {
                pos++;                      // delta_pic_order_always_zero_flag
                SpsReadSe(bits, ref pos); SpsReadSe(bits, ref pos);
                int n = SpsReadUe(bits, ref pos);
                for (int i = 0; i < n; i++) SpsReadSe(bits, ref pos);
            }

            SpsReadUe(bits, ref pos);       // num_ref_frames
            pos++;                          // gaps_in_frame_num_value_allowed_flag
            SpsReadUe(bits, ref pos);       // pic_width_in_mbs_minus1
            SpsReadUe(bits, ref pos);       // pic_height_in_map_units_minus1

            bool frameMbsOnly = bits[pos++];
            if (!frameMbsOnly) pos++;       // mb_adaptive_frame_field_flag
            pos++;                          // direct_8x8_inference_flag

            if (bits[pos]) return spsNal;   // frame_cropping_flag already set — skip
            bits[pos] = true;               // set frame_cropping_flag = 1
            pos++;

            var ins = new List<bool>();
            SpsAppendUe(ins, 0);            // crop_left
            SpsAppendUe(ins, 0);            // crop_right
            SpsAppendUe(ins, 0);            // crop_top
            SpsAppendUe(ins, cropOffset);   // crop_bottom
            bits.InsertRange(pos, ins);
        }
        catch { return spsNal; }

        // Rebuild: start code + NAL header + patched RBSP
        int rbspBytes = (bits.Count + 7) / 8;
        var result = new byte[scLen + 1 + rbspBytes];
        for (int i = 0; i < scLen; i++) result[i] = spsNal[i];
        result[scLen] = spsNal[scLen]; // NAL header
        for (int i = 0; i < bits.Count; i++)
            if (bits[i]) result[scLen + 1 + i / 8] |= (byte)(1 << (7 - (i % 8)));

        BaluLogger.Info("H264", $"SPS patched: added crop_bottom={cropOffset} ({spsNal.Length} → {result.Length} bytes)");
        return result;
    }

    private static int SpsReadBits(List<bool> bits, ref int pos, int count)
    {
        int v = 0;
        for (int i = 0; i < count; i++) v = (v << 1) | (bits[pos++] ? 1 : 0);
        return v;
    }

    private static int SpsReadUe(List<bool> bits, ref int pos)
    {
        int m = 0;
        while (pos < bits.Count && !bits[pos++]) m++;
        int info = 0;
        for (int i = 0; i < m; i++) info = (info << 1) | (bits[pos++] ? 1 : 0);
        return (1 << m) - 1 + info;
    }

    private static void SpsReadSe(List<bool> bits, ref int pos) => SpsReadUe(bits, ref pos);

    private static void SpsAppendUe(List<bool> bits, int value)
    {
        int x = value + 1;
        int m = 0;
        while ((x >> (m + 1)) > 0) m++;
        for (int i = 0; i < m; i++) bits.Add(false);
        bits.Add(true);
        for (int i = m - 1; i >= 0; i--) bits.Add(((x >> i) & 1) == 1);
    }

    // ─────────────────────────────────────────────────────────────────────────────

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
                    BaluLogger.Warn("H264MTK", "Encoder thread did not stop within 500ms timeout");
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
                    BaluLogger.Warn("H264MTK", "Encoder.Release() timed out (500ms) - may cause resource leak");
                }
            }
            catch (System.Exception ex)
            {
                BaluLogger.Error("H264MTK", $"Error stopping encoder: {ex.Message}");
            }


            _encoder = null;
            BaluLogger.Info("H264MTK", "Encoder stopped and disposed");
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