using Android.Media;
using Android.OS;
using Android.Util;
using Android.Views;
using BaluMediaServer.Models;
using Java.Nio;
using System.Collections.Concurrent;

namespace BaluMediaServer.Services;

public class H264Encoder : IDisposable
{
    private MediaCodec? _encoder;
    private readonly int _width;
    private readonly int _height;
    private readonly int _bitrate;
    private readonly int _frameRate;
    private bool _isRunning;
    private Thread? _processThread;
    private readonly ConcurrentQueue<byte[]> _inputQueue = new();
    private readonly object _encoderLock = new();
    private long _frameCounter = 0;
    private const int COLOR_FormatYUV420Planar = 19;
    private const int COLOR_FormatYUV420SemiPlanar = 21;  // NV12
    private const int COLOR_FormatYUV420PackedSemiPlanar = 39;
    private const int COLOR_Format32bitARGB8888 = 2130708361;
    private const int COLOR_FormatYUV420Flexible = 2135033992;
    
    public event EventHandler<H264FrameEventArgs>? FrameEncoded;
    
    public H264Encoder(int width, int height, int bitrate = 2000000, int frameRate = 30)
    {
        _width = width;
        _height = height;
        _bitrate = bitrate;
        _frameRate = frameRate;
    }
    
    public void Start()
    {
        lock (_encoderLock)
        {
            if (_isRunning) return;
            
            try
            {
                var codecList = new MediaCodecList(MediaCodecListKind.RegularCodecs);
                var codecInfos = codecList.GetCodecInfos();

                // Just debugging
                /*
                foreach (var info in codecInfos!)
                {
                    var types = info.GetSupportedTypes();
                    if (info.IsEncoder && types.Contains(MediaFormat.MimetypeVideoAvc))
                    {
                        Log.Debug("H264MTK", $"Found encoder: {info.Name}");
                        var caps = info.GetCapabilitiesForType(MediaFormat.MimetypeVideoAvc);
                        if (caps?.ColorFormats != null)
                        {
                            foreach (var format1 in caps.ColorFormats)
                            {
                                Log.Debug("H264MTK", $"  Supports color format: {format1}");
                            }
                        }
                    }
                }*/
                
                // Create format with MediaTek specific settings
                var format = MediaFormat.CreateVideoFormat(MediaFormat.MimetypeVideoAvc, _width, _height);
                
                // Use NV12 format which is standard and your camera likely outputs NV21 (easy to convert)
                format.SetInteger(MediaFormat.KeyColorFormat, COLOR_FormatYUV420SemiPlanar);
                format.SetInteger(MediaFormat.KeyBitRate, _bitrate);
                format.SetInteger(MediaFormat.KeyFrameRate, _frameRate);
                format.SetInteger(MediaFormat.KeyIFrameInterval, 2);
                
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
                
                _processThread = new Thread(ProcessLoop) 
                { 
                    IsBackground = true,
                    Name = "SimpleH264Thread"
                };
                _processThread.Start();
                
                Log.Debug("SimpleH264", "Encoder started");
            }
            catch (Exception ex)
            {
                Log.Error("SimpleH264", $"Failed to start: {ex.Message}");
                throw;
            }
        }
    }
    
    public void QueueFrame(byte[] nv21Data)
    {
        if (_isRunning)
        {
            // Drop frame if queue is too full
            if (_inputQueue.Count < 5)
            {
                _inputQueue.Enqueue(nv21Data.ToArray()); // Make a copy
            }
        }
    }
    
    private void ProcessLoop()
    {
        var bufferInfo = new MediaCodec.BufferInfo();
        byte[]? lastSps = null;
        byte[]? lastPps = null;
        
        while (_isRunning)
        {
            try
            {
                // Process input
                if (_inputQueue.TryDequeue(out var inputData))
                {
                    ProcessInput(inputData);
                }
                
                // Process output
                ProcessOutput(bufferInfo, ref lastSps, ref lastPps);
            }
            catch (Exception ex)
            {
                Log.Error("SimpleH264", $"Process loop error: {ex.Message}");
            }
        }
    }
    
    private void ProcessInput(byte[] nv21Data)
    {
        if (_encoder == null) return;
        
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
                inputBuffer = _encoder.GetInputBuffers()?[inputIndex];
#pragma warning restore CS0618
            }
            
            if (inputBuffer != null)
            {
                // Simple copy - let encoder handle format conversion
                inputBuffer.Clear();
                inputBuffer.Put(nv21Data, 0, Math.Min(nv21Data.Length, inputBuffer.Capacity()));
                
                var timestamp = _frameCounter++ * 1000000L / _frameRate;
                _encoder.QueueInputBuffer(inputIndex, 0, nv21Data.Length, timestamp, 0);
            }
        }
    }
    
    private void ProcessOutput(MediaCodec.BufferInfo bufferInfo, ref byte[]? lastSps, ref byte[]? lastPps)
    {
        if (_encoder == null)
        {
            Log.Error("[CRITICAL]", "Encoder is null");
            return;
        }
        var outputIndex = _encoder.DequeueOutputBuffer(bufferInfo, 0);
        
        if (outputIndex >= 0)
        {
            ByteBuffer? outputBuffer;
            
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Lollipop)
            {
                outputBuffer = _encoder.GetOutputBuffer(outputIndex);
            }
            else
            {
#pragma warning disable CS0618
                outputBuffer = _encoder.GetOutputBuffers()?[outputIndex];
#pragma warning restore CS0618
            }
            
            if (outputBuffer != null && bufferInfo.Size > 0)
            {
                var data = new byte[bufferInfo.Size];
                outputBuffer.Position(bufferInfo.Offset);
                outputBuffer.Limit(bufferInfo.Offset + bufferInfo.Size);
                outputBuffer.Get(data);
                
                // Check for config data
                if ((bufferInfo.Flags & MediaCodecBufferFlags.CodecConfig) != 0)
                {
                    ParseConfigData(data, ref lastSps, ref lastPps);
                }
                else
                {
                    // Emit frame
                    var frameData = new H264FrameEventArgs
                    {
                        NalUnits = new List<byte[]> { data },
                        IsKeyFrame = (bufferInfo.Flags & MediaCodecBufferFlags.KeyFrame) != 0,
                        Timestamp = bufferInfo.PresentationTimeUs,
                        Sps = lastSps,
                        Pps = lastPps
                    };
                    
                    FrameEncoded?.Invoke(this, frameData);
                }
                
                _encoder.ReleaseOutputBuffer(outputIndex, false);
            }
        }
        else if (outputIndex == (int)MediaCodecInfoState.OutputFormatChanged)
        {
            var format = _encoder.OutputFormat;
            Log.Debug("SimpleH264", $"Format changed: {format}");
            
            // Try to get SPS/PPS from format
            ExtractSpsPpsFromFormat(format, ref lastSps, ref lastPps);
        }
    }
    
    private void ParseConfigData(byte[] data, ref byte[]? sps, ref byte[]? pps)
    {
        // Simple parser - look for NAL start codes
        int i = 0;
        while (i < data.Length - 4)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1)
            {
                int nalStart = i;
                int nalEnd = data.Length;
                
                // Find next start code
                for (int j = i + 4; j < data.Length - 4; j++)
                {
                    if (data[j] == 0 && data[j + 1] == 0 && data[j + 2] == 0 && data[j + 3] == 1)
                    {
                        nalEnd = j;
                        break;
                    }
                }
                
                var nalType = data[i + 4] & 0x1F;
                var nalData = new byte[nalEnd - nalStart];
                Array.Copy(data, nalStart, nalData, 0, nalEnd - nalStart);
                
                if (nalType == 7) sps = nalData;
                else if (nalType == 8) pps = nalData;
                
                i = nalEnd;
            }
            else
            {
                i++;
            }
        }
    }
    
    private void ExtractSpsPpsFromFormat(MediaFormat format, ref byte[]? sps, ref byte[]? pps)
    {
        try
        {
            if (format.ContainsKey("csd-0"))
            {
                var spsBuffer = format.GetByteBuffer("csd-0");
                if (spsBuffer != null)
                {
                    sps = new byte[spsBuffer.Remaining()];
                    spsBuffer.Get(sps);
                    spsBuffer.Rewind();
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
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("SimpleH264", $"Error extracting SPS/PPS: {ex.Message}");
        }
    }
    
    public void Stop()
    {
        lock (_encoderLock)
        {
            _isRunning = false;
            
            _processThread?.Join(1000);
            
            try
            {
                _encoder?.Stop();
                _encoder?.Release();
            }
            catch { }
            
            _encoder = null;
        }
    }
    
    public void Dispose()
    {
        Stop();
    }
}
