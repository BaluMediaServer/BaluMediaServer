using System.Diagnostics;
using System.Threading.Channels;
using Android.Media;
using BaluMediaServer.Models;
using Java.Nio;

namespace BaluMediaServer.Services;

/// <summary>
/// AAC-LC hardware encoder wrapping <see cref="Android.Media.MediaCodec"/> with the
/// <c>audio/mp4a-latm</c> mime type. Produces raw AAC access units (no ADTS header),
/// suitable for direct RFC 3640 mpeg4-generic AAC-hbr RTP packetization.
/// </summary>
/// <remarks>
/// <para>
/// Lifecycle mirrors <see cref="H264Encoder"/>: <see cref="Start"/> spins up a
/// background encoder thread that drives both <see cref="FeedInputBuffer"/> and
/// <see cref="DrainOutputBuffer"/>, with PCM input arriving via <see cref="QueueFrame"/>.
/// </para>
/// <para>
/// The first <see cref="FrameEncoded"/> event after <see cref="Start"/> carries the
/// AudioSpecificConfig in <see cref="AacFrameEventArgs.AudioSpecificConfig"/> so the
/// SDP generator / RTP packetizer can synchronize their <c>config=</c> fmtp parameter
/// with what the hardware actually produced.
/// </para>
/// </remarks>
public sealed class AacEncoder : IDisposable
{
    private const string MimeType = "audio/mp4a-latm";

    private readonly object _lock = new();
    private MediaCodec? _encoder;
    private Thread? _encoderThread;
    private volatile bool _isRunning;
    private volatile bool _disposed;

    private int _sampleRateHz;
    private int _channels;
    private int _bitrate;

    private byte[]? _audioSpecificConfig;
    private bool _ascDelivered;

    private Channel<AudioFrameEventArgs> _frameChannel =
        Channel.CreateBounded<AudioFrameEventArgs>(new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>Raised on the encoder thread for each AAC access unit produced.</summary>
    public event EventHandler<AacFrameEventArgs>? FrameEncoded;

    /// <summary>Raised when the encoder runs into an unrecoverable error.</summary>
    public event EventHandler<string>? ErrorOccurred;

    /// <summary>True while the encoder is running and accepting input.</summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// AudioSpecificConfig bytes (csd-0) reported by MediaCodec, or null until the
    /// codec has emitted its config frame. Two bytes for AAC-LC at known sample rates.
    /// </summary>
    public byte[]? AudioSpecificConfig
    {
        get { lock (_lock) return _audioSpecificConfig; }
    }

    /// <summary>
    /// Starts the encoder. Safe to call once per instance; subsequent calls while
    /// running are ignored.
    /// </summary>
    /// <param name="sampleRateHz">Sample rate in Hz (e.g. 44100, 48000).</param>
    /// <param name="channels">Channel count: 1 (mono) or 2 (stereo).</param>
    /// <param name="bitrate">Target bitrate in bps. 64 kbps is a good speech default; 128 kbps for music.</param>
    /// <returns>True on success.</returns>
    public bool Start(int sampleRateHz, int channels, int bitrate = 64000)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AacEncoder));

        lock (_lock)
        {
            if (_isRunning)
            {
                BaluLogger.Warn("[AacEncoder]", "Start called while already running — ignoring");
                return true;
            }

            try
            {
                _sampleRateHz = sampleRateHz;
                _channels = channels < 1 ? 1 : channels > 2 ? 2 : channels;
                _bitrate = bitrate;
                _audioSpecificConfig = null;
                _ascDelivered = false;

                // Drain any frames lingering from a previous session.
                _frameChannel = Channel.CreateBounded<AudioFrameEventArgs>(
                    new BoundedChannelOptions(8)
                    {
                        FullMode = BoundedChannelFullMode.DropOldest,
                        SingleReader = true,
                        SingleWriter = false,
                    });

                var format = MediaFormat.CreateAudioFormat(MimeType, sampleRateHz, _channels);
                format.SetInteger(MediaFormat.KeyAacProfile, (int)MediaCodecProfileType.Aacobjectlc);
                format.SetInteger(MediaFormat.KeyBitRate, bitrate);
                // Reserve enough room for one full AAC frame plus overhead.
                format.SetInteger(MediaFormat.KeyMaxInputSize, AudioCapture_PerFrameByteSize());

                _encoder = MediaCodec.CreateEncoderByType(MimeType);
                if (_encoder == null)
                {
                    BaluLogger.Error("[AacEncoder]", $"CreateEncoderByType({MimeType}) returned null");
                    return false;
                }

                _encoder.Configure(format, null, null, MediaCodecConfigFlags.Encode);
                _encoder.Start();
                _isRunning = true;

                _encoderThread = new Thread(EncodingLoop)
                {
                    IsBackground = true,
                    Name = "AAC-Encoder",
                    Priority = ThreadPriority.AboveNormal,
                };
                _encoderThread.Start();

                BaluLogger.Info("[AacEncoder]",
                    $"Started: {sampleRateHz} Hz, {_channels}ch, {bitrate}bps");
                return true;
            }
            catch (Exception ex)
            {
                BaluLogger.Error("[AacEncoder]", $"Start failed: {ex.Message}");
                SafeRaiseError($"Start failed: {ex.Message}");
                try { _encoder?.Release(); } catch { }
                _encoder = null;
                _isRunning = false;
                return false;
            }
        }
    }

    /// <summary>
    /// Stops the encoder thread and releases the MediaCodec instance.
    /// </summary>
    public void Stop()
    {
        Thread? thread;
        MediaCodec? encoder;

        lock (_lock)
        {
            if (!_isRunning && _encoder == null) return;
            _isRunning = false;
            thread = _encoderThread;
            encoder = _encoder;
            _encoderThread = null;
            _encoder = null;
            _frameChannel.Writer.TryComplete();
        }

        if (thread != null && thread.IsAlive)
        {
            if (!thread.Join(TimeSpan.FromSeconds(2)))
            {
                BaluLogger.Warn("[AacEncoder]", "Encoder thread did not join within 2s");
            }
        }

        try { encoder?.Stop(); } catch (Exception ex) { BaluLogger.Warn("[AacEncoder]", $"Stop error: {ex.Message}"); }
        try { encoder?.Release(); } catch { }

        BaluLogger.Info("[AacEncoder]", "Stopped");
    }

    /// <summary>
    /// Queues a PCM frame for encoding. Drops the oldest queued frame when the input
    /// channel is full so latency stays bounded — same policy used by H264Encoder.
    /// </summary>
    public void QueueFrame(AudioFrameEventArgs frame)
    {
        if (!_isRunning || _disposed) return;
        _frameChannel.Writer.TryWrite(frame);
    }

    private void EncodingLoop()
    {
        using var bufferInfo = new MediaCodec.BufferInfo();
        var spinWait = new SpinWait();
        int consecutiveErrors = 0;
        const int maxConsecutiveErrors = 50;

        try
        {
            while (_isRunning && !_disposed)
            {
                try
                {
                    bool processedOutput = DrainOutputBuffer(bufferInfo);
                    if (_disposed) break;

                    bool processedInput = false;
                    if (_frameChannel.Reader.TryRead(out var frame))
                    {
                        FeedInputBuffer(frame);
                        processedInput = true;
                    }

                    if (!processedInput && !processedOutput) spinWait.SpinOnce();
                    else spinWait.Reset();

                    consecutiveErrors = 0;
                }
                catch (Exception ex)
                {
                    if (_disposed) break;
                    consecutiveErrors++;
                    BaluLogger.Error("[AacEncoder]",
                        $"Encoding loop error ({consecutiveErrors}/{maxConsecutiveErrors}): {ex.Message}");
                    if (consecutiveErrors >= maxConsecutiveErrors)
                    {
                        SafeRaiseError("Encoding loop stopped after too many consecutive errors");
                        _isRunning = false;
                        break;
                    }
                    Thread.Sleep(5);
                }
            }
        }
        finally
        {
            BaluLogger.Debug("[AacEncoder]", "Encoding loop exited");
        }
    }

    private void FeedInputBuffer(AudioFrameEventArgs frame)
    {
        var encoder = _encoder;
        if (encoder == null || _disposed) return;

        int index = encoder.DequeueInputBuffer(1000);
        if (index < 0) return;

        ByteBuffer? input = encoder.GetInputBuffer(index);
        if (input == null)
        {
            try { encoder.QueueInputBuffer(index, 0, 0, frame.Timestamp, 0); } catch { }
            return;
        }

        try
        {
            input.Clear();
            int size = Math.Min(frame.Data.Length, input.Capacity());
            input.Put(frame.Data, 0, size);
            encoder.QueueInputBuffer(index, 0, size, frame.Timestamp, 0);
        }
        finally
        {
            input.Dispose();
        }
    }

    private bool DrainOutputBuffer(MediaCodec.BufferInfo bufferInfo)
    {
        var encoder = _encoder;
        if (encoder == null || _disposed) return false;

        int index = encoder.DequeueOutputBuffer(bufferInfo, 1000);

        if (index >= 0)
        {
            ByteBuffer? output = encoder.GetOutputBuffer(index);
            try
            {
                if (output != null && bufferInfo.Size > 0)
                {
                    var data = new byte[bufferInfo.Size];
                    output.Position(bufferInfo.Offset);
                    output.Limit(bufferInfo.Offset + bufferInfo.Size);
                    output.Get(data);

                    if ((bufferInfo.Flags & MediaCodecBufferFlags.CodecConfig) != 0)
                    {
                        lock (_lock) _audioSpecificConfig = data;
                        BaluLogger.Info("[AacEncoder]",
                            $"Captured AudioSpecificConfig ({data.Length} bytes): {BitConverter.ToString(data).Replace("-", "")}");
                    }
                    else
                    {
                        byte[]? ascForEvent = null;
                        lock (_lock)
                        {
                            if (!_ascDelivered && _audioSpecificConfig != null)
                            {
                                ascForEvent = _audioSpecificConfig;
                                _ascDelivered = true;
                            }
                        }

                        var args = new AacFrameEventArgs
                        {
                            Data = data,
                            Timestamp = bufferInfo.PresentationTimeUs,
                            EncodedAt = Stopwatch.GetTimestamp(),
                            AudioSpecificConfig = ascForEvent,
                        };

                        try { FrameEncoded?.Invoke(this, args); }
                        catch (Exception ex) { BaluLogger.Error("[AacEncoder]", $"FrameEncoded subscriber error: {ex.Message}"); }
                    }
                }
            }
            finally
            {
                try { encoder.ReleaseOutputBuffer(index, false); } catch { }
                output?.Dispose();
            }
            return true;
        }

        if (index == (int)MediaCodecInfoState.OutputFormatChanged)
        {
            var fmt = encoder.OutputFormat;
            // csd-0 may be exposed only via the format here (no CodecConfig frame on
            // some platforms). Capture it if present.
            try
            {
                var csd0 = fmt?.GetByteBuffer("csd-0");
                if (csd0 != null)
                {
                    var bytes = new byte[csd0.Remaining()];
                    csd0.Get(bytes);
                    lock (_lock) _audioSpecificConfig = bytes;
                    BaluLogger.Info("[AacEncoder]",
                        $"AudioSpecificConfig from OutputFormat ({bytes.Length} bytes): {BitConverter.ToString(bytes).Replace("-", "")}");
                }
            }
            catch (Exception ex)
            {
                BaluLogger.Warn("[AacEncoder]", $"Could not read csd-0 from format: {ex.Message}");
            }
            return true;
        }

        return false;
    }

    private int AudioCapture_PerFrameByteSize()
    {
        // 1024 samples × channels × 2 bytes (PCM-16). Matches AudioCaptureService.AacSamplesPerFrame.
        return 1024 * _channels * 2;
    }

    private void SafeRaiseError(string message)
    {
        try { ErrorOccurred?.Invoke(this, message); }
        catch { }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Stop(); } catch { }
    }
}
