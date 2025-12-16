using Android.Media;
using Android.OS;
using Java.Nio;
using System.Collections.Concurrent;
using Android.Util;
using BaluMediaServer.Models;
using System.Diagnostics;
using System.Buffers;

namespace BaluMediaServer.Services;

/// <summary>
/// H.264 hardware encoder optimized for MediaTek chipsets.
/// Provides low-latency encoding with MediaTek-specific optimizations.
/// </summary>
public class MediaTekH264Encoder : IDisposable
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
    private readonly Stopwatch _stopwatch = new();

    /// <summary>
    /// Event raised when a frame has been encoded and is ready for streaming.
    /// </summary>
    public event EventHandler<H264FrameEventArgs>? FrameEncoded;

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
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaTekH264Encoder"/> class.
    /// </summary>
    /// <param name="width">The video width in pixels.</param>
    /// <param name="height">The video height in pixels.</param>
    /// <param name="bitrate">The target bitrate in bits per second. Default is 2,000,000.</param>
    /// <param name="frameRate">The target frame rate. Default is 30.</param>
    public MediaTekH264Encoder(int width, int height, int bitrate = 2000000, int frameRate = 30)
    {
        _width = width;
        _height = height;
        _bitrate = bitrate;
        _frameRate = frameRate;
    }
    
    /// <summary>
    /// Starts the H.264 encoder with MediaTek-specific optimizations.
    /// </summary>
    /// <returns><c>true</c> if the encoder started successfully; otherwise, <c>false</c>.</returns>
    public bool Start()
    {
        lock (_lock)
        {
            if (_isRunning) return true;

            try
            {
                // Create format with standard color format
                var format = MediaFormat.CreateVideoFormat(MediaFormat.MimetypeVideoAvc, _width, _height);
                
                // Use NV12 format which is standard and your camera likely outputs NV21 (easy to convert)
                format.SetInteger(MediaFormat.KeyColorFormat, COLOR_FormatYUV420SemiPlanar);
                format.SetInteger(MediaFormat.KeyBitRate, _bitrate);
                format.SetInteger(MediaFormat.KeyFrameRate, _frameRate);
                format.SetInteger(MediaFormat.KeyIFrameInterval, 2);
                format.SetInteger(MediaFormat.KeyMaxInputSize, 0); // No buffering
#pragma warning disable CA1416 // Validate platform compatibility
                format.SetInteger(MediaFormat.KeyLatency, 0); // Minimum latency
#pragma warning restore CA1416 // Validate platform compatibility
                format.SetInteger(MediaFormat.KeyPriority, 0); // Real-time priority
                format.SetInteger("vendor.mtk-ext-enc-low-latency.enable", 1);
                //format.SetInteger(MediaFormat.KeyMaxBFrames, 0); // Disable B-frames
                format.SetInteger(MediaFormat.KeyRepeatPreviousFrameAfter, 33333); // Handle drops
                format.SetInteger("vendor.mtk-ext-enc-nonrefp.enable", 1); // MediaTek optimization
                if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
                {
                    format.SetInteger(MediaFormat.KeyOperatingRate, short.MaxValue); // Max operating rate
                    format.SetInteger(MediaFormat.KeyIntraRefreshPeriod, 10); // Intra refresh instead of keyframes
                }
                // Try to create encoder - prefer hardware encoder
                string[] encoderPreference = {
                    "c2.mtk.avc.encoder",           // MediaTek hardware
                    "OMX.MTK.VIDEO.ENCODER.AVC",    // MediaTek OMX
                    "c2.android.avc.encoder",       // Android hardware
                    "OMX.google.h264.encoder"       // Google software (fallback)
                };
                
                MediaCodec? encoder = null;
                string selectedEncoder = "";
                
                foreach (var encoderName in encoderPreference)
                {
                    try
                    {
                        encoder = MediaCodec.CreateByCodecName(encoderName);
                        encoder.Configure(format, null, null, MediaCodecConfigFlags.Encode);
                        selectedEncoder = encoderName;
                        Log.Debug("H264MTK", $"Successfully configured encoder: {encoderName}");
                        break;
                    }
                    catch (Exception ex)
                    {
                        encoder?.Release();
                        encoder = null;
                        Log.Debug("H264MTK", $"Failed to configure {encoderName}: {ex.Message}");
                    }
                }
                
                if (encoder == null)
                {
                    throw new Exception("Could not configure any H264 encoder");
                }
                
                _encoder = encoder;
                _encoder.Start();
                
                _isRunning = true;

                // Start encoding thread
                //Task.Run(EncodingLoop);
                _encoderThread = new Thread(EncodingLoop)
                {
                    IsBackground = true,
                    Name = "H264MTKEncoder",
                    Priority = System.Threading.ThreadPriority.Highest
                };
                _encoderThread.Start();
                
                Log.Debug("H264MTK", $"Encoder started: {_width}x{_height} @ {_bitrate}bps");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("H264MTK", $"Failed to start encoder: {ex.Message}");
                _encoder?.Release();
                _encoder = null;
                return false;
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
    /// Queues a raw YUV frame for encoding.
    /// Drops older frames if queue backs up to minimize latency.
    /// </summary>
    /// <param name="frameData">The raw YUV420 frame data.</param>
    public void QueueFrame(byte[] frameData)
    {
        if (!_isRunning) return;

        // Use actual time for timestamps to prevent stuttering
        if (!_stopwatch.IsRunning)
        {
            _stopwatch.Start();
        }
        
        var timestamp = _stopwatch.ElapsedTicks * 1000000L / Stopwatch.Frequency;
        
        // Drop frames if queue is backing up (keep only 2 frames max)
        while (_frameQueue.Count > 0)
        {
            if (_frameQueue.TryDequeue(out _))
            {
                Log.Debug("H264MTK", "Dropped old frame to prevent latency");
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
    
    /// <summary>
    /// Feeds a frame into the encoder's input buffer.
    /// Converts NV21 to NV12 format as required by the encoder.
    /// </summary>
    /// <param name="frame">The frame data to encode.</param>
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
            
            return nv12;
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
    
    /// <summary>
    /// Stops the encoder and releases all resources.
    /// </summary>
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
    
    /// <summary>
    /// Releases all resources used by the encoder.
    /// </summary>
    public void Dispose()
    {
        Stop();
        //_frameQueue?.Dispose();
    }
}