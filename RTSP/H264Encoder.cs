using Android.Media;
using Android.OS;
using Java.Nio;
using System.Collections.Concurrent;
using Android.Util;
using BaluMediaServer.Models;
using System.Diagnostics;
using System.Buffers;

namespace BaluMediaServer.Services;

public class H264Encoder : IDisposable
{
    private MediaCodec? _encoder;
    private readonly int _width;
    private readonly int _height;
    private int _bitrate;
    private readonly int _frameRate;
    private bool _isRunning;
    private Thread? _encoderThread;
    private readonly ConcurrentQueue<FrameData> _frameQueue = new();
    private readonly ArrayPool<byte> _bufferPool = ArrayPool<byte>.Create();

    private readonly object _lock = new();
    
    // Color formats supported by your device
    private const int COLOR_FormatYUV420Planar = 19;
    private const int COLOR_FormatYUV420SemiPlanar = 21;  // NV12
    private const int COLOR_FormatYUV420PackedSemiPlanar = 39;
    private const int COLOR_Format32bitARGB8888 = 2130708361;
    private const int COLOR_FormatYUV420Flexible = 2135033992;
    private long _lastTimestamp = 0;
    private readonly EncoderInfo _bestEncoder = new();
    private readonly Stopwatch _stopwatch = new();
    public event EventHandler<H264FrameEventArgs>? FrameEncoded;
    
    public class FrameData
    {
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public long Timestamp { get; set; }
    }

    public H264Encoder(int width, int height, int bitrate = 2000000, int frameRate = 30)
    {
        _width = width;
        _height = height;
        _bitrate = bitrate;
        _frameRate = frameRate;
        var codecList = new MediaCodecList(new());
        var codecInfos = codecList.GetCodecInfos();

        _bestEncoder = SelectBestEncoder(codecInfos!);
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
    
    public bool Start()
    {
        lock (_lock)
        {
            if (_isRunning) return true;

            try
            {
                // Create format with encoder's supported color format
                var format = MediaFormat.CreateVideoFormat(MediaFormat.MimetypeVideoAvc, _width, _height);

                // Use the best color format from the selected encoder
                int colorFormat = COLOR_FormatYUV420SemiPlanar; // Default
                if (_bestEncoder.ColorFormats.Contains(COLOR_FormatYUV420SemiPlanar))
                    colorFormat = COLOR_FormatYUV420SemiPlanar; // NV12
                else if (_bestEncoder.ColorFormats.Contains(COLOR_FormatYUV420Flexible))
                    colorFormat = COLOR_FormatYUV420Flexible;

                format.SetInteger(MediaFormat.KeyColorFormat, colorFormat);
                format.SetInteger(MediaFormat.KeyBitRate, _bitrate);
                format.SetInteger(MediaFormat.KeyFrameRate, _frameRate);
                format.SetInteger(MediaFormat.KeyIFrameInterval, 2);
                
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

                // Set bitrate mode after configuration if supported
                if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
                {
                    var bundle = new Bundle();
                    bundle.PutInt(MediaCodec.ParameterKeyVideoBitrate, 
                        (int)BitrateMode.CbrFd);
                    encoder.SetParameters(bundle);
                }

                // Now start the encoder
                encoder.Start();
                
                _encoder = encoder;
                _isRunning = true;

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
                Log.Info("H264", $"Color format: {colorFormat}");
                
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
    public void QueueFrame(byte[] frameData)
    {
        if (!_isRunning) return;
        
        int expectedSize = (_width * _height * 3) / 2;

        if (frameData.Length != expectedSize)
        {
            Log.Error("H264", $"Invalid frame size: {frameData.Length}, expected: {expectedSize}");
            return;
        }
        // Use actual time for timestamps to prevent stuttering
        if (!_stopwatch.IsRunning)
        {
            _stopwatch.Start();
        }
        
        var timestamp = _stopwatch.ElapsedTicks * 1000000L / Stopwatch.Frequency;
        
        // Drop frames if queue is backing up (keep only 2 frames max)
        while (_frameQueue.Count > 2)
        {
            if (_frameQueue.TryDequeue(out _))
            {
                Log.Debug("H264", "Dropped old frame to prevent latency");
            }
        }
        
        _frameQueue.Enqueue(new() { Data = frameData, Timestamp = timestamp });
    }
    
    private void EncodingLoop()
    {
        var bufferInfo = new MediaCodec.BufferInfo();
        byte[]? sps = null;
        byte[]? pps = null;
        bool gotFirstOutput = false;
        
        Log.Debug("H264MTK", "Encoding loop started");
        
        while (_isRunning)
        {
            try
            {
                bool processedInput = false;
                bool processedOutput = false;
                
                // Process input if available
                if (_frameQueue.TryDequeue(out var frame))
                {
                    FeedInputBuffer(frame);
                    processedInput = true;
                }
                
                // Always try to drain output
                processedOutput = DrainOutputBuffer(bufferInfo, ref sps, ref pps, ref gotFirstOutput);
                
                // Only sleep if we didn't process anything
                if (!processedInput && !processedOutput)
                {
                    //Thread.Sleep(1);
                }
            }
            catch (Exception ex)
            {
                Log.Error("H264MTK", $"Encoding loop error: {ex.Message}");
            }
        }
        
        Log.Debug("H264MTK", "Encoding loop ended");
    }
    
    public void FeedInputBuffer(FrameData frame)
    {
        if (_encoder == null) return;
        
        try
        {
            // Single attempt with no timeout for lower latency
            var inputIndex = _encoder.DequeueInputBuffer(0);
            
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
                    
                    // Convert NV21 to NV12 since we're using YUV420SemiPlanar
                    var nv12Data = ConvertNV21ToNV12Pooled(frame.Data);
                    var dataSize = Math.Min(nv12Data.Length, inputBuffer.Capacity());
                    inputBuffer.Put(nv12Data, 0, dataSize);
                    
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
        if (_encoder == null) return false;
        
        try
        {
            var outputIndex = _encoder.DequeueOutputBuffer(bufferInfo, 0);
            
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
                        // Regular frame
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
                            nalUnits.Add(data);
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
            if (nal.Length >= 5)
            {
                var nalType = nal[4] & 0x1F;
                if (nalType == 7) sps = nal;
                else if (nalType == 8) pps = nal;
            }
        }
    }
    
    private List<byte[]> ExtractNalUnits(byte[] data)
    {
        var nalUnits = new List<byte[]>();
        int i = 0;
        
        while (i < data.Length - 3)
        {
            if ((i + 3 < data.Length && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1) ||
                (i + 4 < data.Length && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1))
            {
                int startCodeLen = (data[i + 2] == 1) ? 3 : 4;
                int nalStart = i;
                int nalEnd = data.Length;
                
                // Find next start code
                for (int j = i + startCodeLen; j < data.Length - 3; j++)
                {
                    if ((data[j] == 0 && data[j + 1] == 0 && data[j + 2] == 1) ||
                        (j + 3 < data.Length && data[j] == 0 && data[j + 1] == 0 && data[j + 2] == 0 && data[j + 3] == 1))
                    {
                        nalEnd = j;
                        break;
                    }
                }
                
                var nalUnit = new byte[nalEnd - nalStart];
                Array.Copy(data, nalStart, nalUnit, 0, nalEnd - nalStart);
                nalUnits.Add(nalUnit);
                
                i = nalEnd;
            }
            else
            {
                i++;
            }
        }
        
        // If no NAL units found, treat entire data as one NAL
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
            // Try csd-0 and csd-1
            if (format.ContainsKey("csd-0"))
            {
                var spsBuffer = format.GetByteBuffer("csd-0");
                if (spsBuffer != null)
                {
                    sps = new byte[spsBuffer.Remaining()];
                    spsBuffer.Get(sps);
                    spsBuffer.Rewind();
                    Log.Debug("H264MTK", $"Got SPS from format: {sps.Length} bytes");
                }
            }
            
            if (format.ContainsKey("csd-1"))
            {
                var ppsBuffer = format.GetByteBuffer("csd-1");
                if (ppsBuffer != null)
                {
                    pps = new byte[ppsBuffer.Remaining()];
                    ppsBuffer.Get(pps);
                    ppsBuffer.Rewind();
                    Log.Debug("H264MTK", $"Got PPS from format: {pps.Length} bytes");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("H264MTK", $"Error extracting parameter sets: {ex.Message}");
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
    
    public void Stop()
    {
        lock (_lock)
        {
            _isRunning = false;
            //_frameQueue.CompleteAdding();
            
            _encoderThread?.Join(1000);
            
            try
            {
                _encoder?.Stop();
                _encoder?.Release();
            }
            catch (Exception ex)
            {
                Log.Error("H264MTK", $"Error stopping encoder: {ex.Message}");
            }
            
            _encoder = null;
        }
    }
    
    public void Dispose()
    {
        Stop();
        //_frameQueue?.Dispose();
    }
}