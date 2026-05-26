using System.Diagnostics;
using Android.Content.PM;
using Android.Media;
using BaluMediaServer.Interfaces;
using BaluMediaServer.Models;

namespace BaluMediaServer.Platforms.Android.Services;

/// <summary>
/// Microphone capture service producing raw PCM-16 frames sized for AAC-LC encoding
/// (1024 samples per frame). The capture loop runs on a dedicated background thread
/// so the audio pipeline is decoupled from the camera pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Requires the host application to declare <c>android.permission.RECORD_AUDIO</c>
/// in its <c>AndroidManifest.xml</c> and to have been granted the runtime permission.
/// If the permission is missing, <see cref="StartCapture"/> raises
/// <see cref="ErrorOccurred"/> and returns without starting.
/// </para>
/// <para>
/// Frames are emitted via <see cref="FrameReceived"/> at roughly <c>sampleRate / 1024</c>
/// times per second — about 43 fps at 44.1 kHz, 47 fps at 48 kHz. Each event carries
/// exactly one AAC access unit's worth of PCM samples.
/// </para>
/// </remarks>
public sealed class AudioCaptureService : IAudioCaptureService
{
    /// <summary>AAC-LC access-unit size in samples per channel.</summary>
    public const int AacSamplesPerFrame = 1024;

    private readonly object _lock = new();
    private AudioRecord? _audioRecord;
    private Thread? _captureThread;
    private CancellationTokenSource? _cts;
    private volatile bool _isCapturing;
    private volatile bool _disposed;
    private int _sampleRateHz;
    private int _channels;
    private long _baseTimestampUs;
    private readonly Stopwatch _stopwatch = new();

    /// <inheritdoc/>
    public event EventHandler<AudioFrameEventArgs>? FrameReceived;

    /// <inheritdoc/>
    public event EventHandler<string>? ErrorOccurred;

    /// <inheritdoc/>
    public bool IsCapturing => _isCapturing;

    /// <inheritdoc/>
    public void StartCapture(int sampleRateHz, int channels)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AudioCaptureService));
        if (channels < 1) channels = 1;
        if (channels > 2) channels = 2;

        lock (_lock)
        {
            if (_isCapturing)
            {
                BaluLogger.Warn("[AudioCapture]", "StartCapture called while already capturing — ignoring");
                return;
            }

            if (!HasRecordAudioPermission())
            {
                SafeRaiseError("RECORD_AUDIO permission not granted — audio capture cannot start");
                return;
            }

            try
            {
                _sampleRateHz = sampleRateHz;
                _channels = channels;

                var channelConfig = channels == 2 ? ChannelIn.Stereo : ChannelIn.Mono;
                const Encoding encoding = Encoding.Pcm16bit;

                int minBuffer = AudioRecord.GetMinBufferSize(sampleRateHz, channelConfig, encoding);
                if (minBuffer <= 0)
                {
                    SafeRaiseError($"AudioRecord.GetMinBufferSize returned {minBuffer} — invalid sample rate/channel combo");
                    return;
                }

                // Size the AudioRecord internal buffer for at least four AAC frames of
                // headroom. PCM-16: bytesPerFrame = samples * channels * 2.
                int aacFrameBytes = AacSamplesPerFrame * channels * 2;
                int desired = Math.Max(minBuffer * 2, aacFrameBytes * 4);

                _audioRecord = new AudioRecord(
                    AudioSource.Mic,
                    sampleRateHz,
                    channelConfig,
                    encoding,
                    desired);

                if (_audioRecord.State != global::Android.Media.State.Initialized)
                {
                    SafeRaiseError($"AudioRecord failed to initialize (state={_audioRecord.State})");
                    try { _audioRecord.Release(); } catch { }
                    _audioRecord = null;
                    return;
                }

                _stopwatch.Restart();
                _baseTimestampUs = 0;
                _cts = new CancellationTokenSource();
                _audioRecord.StartRecording();
                _isCapturing = true;

                _captureThread = new Thread(CaptureLoop)
                {
                    IsBackground = true,
                    Name = "AudioCapture",
                    Priority = ThreadPriority.AboveNormal,
                };
                _captureThread.Start();

                BaluLogger.Info("[AudioCapture]",
                    $"Started: {sampleRateHz} Hz, {channels}ch, internalBuf={desired} bytes, frameBytes={aacFrameBytes}");
            }
            catch (Exception ex)
            {
                BaluLogger.Error("[AudioCapture]", $"StartCapture failed: {ex.Message}");
                SafeRaiseError($"StartCapture failed: {ex.Message}");
                try { _audioRecord?.Release(); } catch { }
                _audioRecord = null;
                _isCapturing = false;
            }
        }
    }

    /// <inheritdoc/>
    public void StopCapture()
    {
        lock (_lock)
        {
            if (!_isCapturing && _audioRecord == null) return;

            _isCapturing = false;
            try { _cts?.Cancel(); } catch { }

            var thread = _captureThread;
            _captureThread = null;
            if (thread != null && thread.IsAlive)
            {
                if (!thread.Join(TimeSpan.FromSeconds(2)))
                {
                    BaluLogger.Warn("[AudioCapture]", "Capture thread did not join within 2s");
                }
            }

            try { _audioRecord?.Stop(); } catch (Exception ex) { BaluLogger.Warn("[AudioCapture]", $"Stop error: {ex.Message}"); }
            try { _audioRecord?.Release(); } catch { }
            _audioRecord = null;

            try { _cts?.Dispose(); } catch { }
            _cts = null;

            BaluLogger.Info("[AudioCapture]", "Stopped");
        }
    }

    private void CaptureLoop()
    {
        int frameBytes = AacSamplesPerFrame * _channels * 2;
        var buffer = new byte[frameBytes];
        long ticksPerUs = Stopwatch.Frequency / 1_000_000L;
        if (ticksPerUs <= 0) ticksPerUs = 1;
        long usPerSample = 1_000_000L / _sampleRateHz;
        long sampleCursor = 0;
        bool loggedFirstFrame = false;
        var token = _cts?.Token ?? CancellationToken.None;

        try
        {
            while (_isCapturing && !token.IsCancellationRequested && !_disposed)
            {
                var rec = _audioRecord;
                if (rec == null) break;

                int read;
                try
                {
                    read = rec.Read(buffer, 0, frameBytes);
                }
                catch (Exception ex)
                {
                    BaluLogger.Error("[AudioCapture]", $"AudioRecord.Read threw: {ex.Message}");
                    SafeRaiseError($"AudioRecord.Read threw: {ex.Message}");
                    break;
                }

                if (read <= 0)
                {
                    // ERROR_INVALID_OPERATION (-3) or ERROR_BAD_VALUE (-2) — fatal.
                    // 0 just means no samples this iteration (rare; back off briefly).
                    if (read < 0)
                    {
                        BaluLogger.Error("[AudioCapture]", $"AudioRecord.Read returned {read} — stopping");
                        SafeRaiseError($"AudioRecord read error code {read}");
                        break;
                    }
                    Thread.Sleep(1);
                    continue;
                }

                // If short read, pad zeros so the encoder always sees a full AAC frame.
                if (read < frameBytes)
                {
                    Array.Clear(buffer, read, frameBytes - read);
                }

                // Wall-clock timestamp anchored at first frame. Using the sample cursor
                // (not Stopwatch) keeps the PTS monotonic and free of capture jitter,
                // which avoids the MediaTek PTS-unit corruption observed for video.
                long pts = _baseTimestampUs + sampleCursor * usPerSample;
                sampleCursor += AacSamplesPerFrame;

                // Copy into a fresh array — the local `buffer` is reused next iteration.
                var frameData = new byte[frameBytes];
                Buffer.BlockCopy(buffer, 0, frameData, 0, frameBytes);

                var args = new AudioFrameEventArgs
                {
                    Data = frameData,
                    SampleCount = AacSamplesPerFrame,
                    SampleRateHz = _sampleRateHz,
                    Channels = _channels,
                    Timestamp = pts,
                };

                if (!loggedFirstFrame)
                {
                    loggedFirstFrame = true;
                    BaluLogger.Info("[AudioCapture]",
                        $"First frame: {AacSamplesPerFrame} samples, {frameBytes} bytes, pts={pts}us");
                }

                try
                {
                    FrameReceived?.Invoke(this, args);
                }
                catch (Exception ex)
                {
                    BaluLogger.Error("[AudioCapture]", $"FrameReceived subscriber error: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                BaluLogger.Error("[AudioCapture]", $"Capture loop fatal: {ex.Message}");
                SafeRaiseError($"Capture loop fatal: {ex.Message}");
            }
        }
        finally
        {
            BaluLogger.Debug("[AudioCapture]", "Capture loop exited");
        }
    }

    private static bool HasRecordAudioPermission()
    {
        try
        {
            var ctx = global::Android.App.Application.Context;
            return ctx.CheckSelfPermission(global::Android.Manifest.Permission.RecordAudio) == Permission.Granted;
        }
        catch
        {
            return false;
        }
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
        try { StopCapture(); } catch { }
    }
}
