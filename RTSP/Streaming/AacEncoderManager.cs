using System.Buffers;
using System.Threading.Channels;
using BaluMediaServer.Models;
using BaluMediaServer.Services;

namespace BaluMediaServer.RTSP.Streaming;

/// <summary>
/// Owns the single shared <see cref="AacEncoder"/> and fans encoded AAC frames out to
/// every registered client's bounded channel. Mirrors <see cref="H264EncoderManager"/>
/// but without per-camera dimensions — the microphone produces one stream consumed by
/// all audio-enabled clients.
/// </summary>
public sealed class AacEncoderManager : IDisposable
{
    /// <summary>Bound for the per-client channel — same as H264 manager (1 frame).</summary>
    private const int MaxClientQueueSize = 4;

    private readonly object _encoderLock = new();
    private AacEncoder? _encoder;

    private readonly Dictionary<string, Channel<AacFrameEventArgs>> _clientChannels = new();
    private readonly object _clientChannelsLock = new();

    private byte[]? _audioSpecificConfig;

    /// <summary>Raised on every encoded AAC frame after fan-out.</summary>
    public event EventHandler<AacFrameEventArgs>? FrameEncoded;

    /// <summary>True while the encoder thread is producing frames.</summary>
    public bool IsRunning
    {
        get { lock (_encoderLock) return _encoder?.IsRunning == true; }
    }

    /// <summary>
    /// Cached AudioSpecificConfig from the encoder, or null until the encoder has emitted
    /// its first config frame. The SDP generator uses this to override the hardcoded
    /// <c>fmtp config=</c> value if the hardware reports something different.
    /// </summary>
    public byte[]? AudioSpecificConfig
    {
        get { lock (_encoderLock) return _audioSpecificConfig; }
    }

    /// <summary>
    /// Starts the encoder. Idempotent: subsequent calls with the same parameters while
    /// already running are no-ops.
    /// </summary>
    public bool Start(int sampleRateHz, int channels, int bitrate = 64000)
    {
        lock (_encoderLock)
        {
            if (_encoder != null && _encoder.IsRunning)
            {
                return true;
            }

            if (_encoder != null)
            {
                try { _encoder.Dispose(); } catch { }
                _encoder = null;
            }

            _audioSpecificConfig = null;
            _encoder = new AacEncoder();
            _encoder.FrameEncoded += OnEncoderFrameEncoded;
            return _encoder.Start(sampleRateHz, channels, bitrate);
        }
    }

    /// <summary>Stops and disposes the underlying encoder. Safe to call when not running.</summary>
    public void Stop()
    {
        AacEncoder? encoder;
        lock (_encoderLock)
        {
            encoder = _encoder;
            _encoder = null;
        }
        if (encoder == null) return;

        encoder.FrameEncoded -= OnEncoderFrameEncoded;
        try { encoder.Stop(); } catch { }
        try { encoder.Dispose(); } catch { }
    }

    /// <summary>Forwards a captured PCM frame into the encoder's input queue.</summary>
    public void QueueFrame(AudioFrameEventArgs frame)
    {
        AacEncoder? encoder;
        lock (_encoderLock) encoder = _encoder;
        encoder?.QueueFrame(frame);
    }

    /// <summary>
    /// Registers a per-client channel. Must be called before the client starts dequeuing
    /// frames so it doesn't miss the encoder's first packets.
    /// </summary>
    public void RegisterClientChannel(string clientId)
    {
        var channel = Channel.CreateBounded<AacFrameEventArgs>(
            new BoundedChannelOptions(MaxClientQueueSize)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

        lock (_clientChannelsLock) { _clientChannels[clientId] = channel; }
        BaluLogger.Debug("[AacEncoderManager]", $"Registered audio channel for client {clientId}");
    }

    /// <summary>Unregisters a client and completes its channel so any awaiting reader exits.</summary>
    public void UnregisterClientChannel(string clientId)
    {
        Channel<AacFrameEventArgs>? channel = null;
        lock (_clientChannelsLock)
        {
            _clientChannels.TryGetValue(clientId, out channel);
            _clientChannels.Remove(clientId);
        }
        channel?.Writer.TryComplete();
        BaluLogger.Debug("[AacEncoderManager]", $"Unregistered audio channel for client {clientId}");
    }

    /// <summary>
    /// Returns the raw channel reference. Cache at session start to skip the per-frame
    /// dictionary lock — same optimization the H.264 path uses.
    /// </summary>
    public Channel<AacFrameEventArgs>? GetClientChannelRef(string clientId)
    {
        lock (_clientChannelsLock)
        {
            _clientChannels.TryGetValue(clientId, out var ch);
            return ch;
        }
    }

    /// <summary>Number of currently registered clients.</summary>
    public int RegisteredClientCount
    {
        get { lock (_clientChannelsLock) return _clientChannels.Count; }
    }

    private void OnEncoderFrameEncoded(object? sender, AacFrameEventArgs e)
    {
        if (e.AudioSpecificConfig != null)
        {
            lock (_encoderLock) _audioSpecificConfig = e.AudioSpecificConfig;
        }

        // Snapshot channels under lock then write outside it — same pattern as the
        // H.264 fan-out so registering / unregistering clients never waits on TryWrite.
        Channel<AacFrameEventArgs>[]? snapshot = null;
        int count = 0;
        lock (_clientChannelsLock)
        {
            count = _clientChannels.Count;
            if (count > 0)
            {
                snapshot = ArrayPool<Channel<AacFrameEventArgs>>.Shared.Rent(count);
                int i = 0;
                foreach (var ch in _clientChannels.Values)
                    snapshot[i++] = ch;
            }
        }
        if (snapshot != null)
        {
            for (int i = 0; i < count; i++)
                snapshot[i].Writer.TryWrite(e);
            ArrayPool<Channel<AacFrameEventArgs>>.Shared.Return(snapshot, clearArray: true);
        }

        try { FrameEncoded?.Invoke(this, e); }
        catch (Exception ex) { BaluLogger.Error("[AacEncoderManager]", $"FrameEncoded subscriber error: {ex.Message}"); }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Stop();
        lock (_clientChannelsLock)
        {
            foreach (var ch in _clientChannels.Values)
                ch.Writer.TryComplete();
            _clientChannels.Clear();
        }
    }
}
