using BaluMediaServer.Models;

namespace BaluMediaServer.RTSP.Streaming;

/// <summary>
/// Interface for managing H.264 encoder lifecycle and per-client frame channels.
/// Uses a fan-out model: each encoded frame is delivered to every registered client independently.
/// </summary>
public interface IH264EncoderManager
{
    /// <summary>
    /// Event raised when an H.264 frame is encoded.
    /// </summary>
    event EventHandler<H264FrameEventArgs>? FrameEncoded;

    /// <summary>
    /// Registers a per-client channel for the specified camera.
    /// Must be called before the client starts consuming frames.
    /// </summary>
    /// <param name="cameraId">The camera ID (0 = back, 1 = front).</param>
    /// <param name="clientId">The client ID.</param>
    void RegisterClientChannel(int cameraId, string clientId);

    /// <summary>
    /// Unregisters the client's channel and completes its writer.
    /// Any pending ReadAsync on that channel will receive ChannelClosedException.
    /// </summary>
    /// <param name="cameraId">The camera ID.</param>
    /// <param name="clientId">The client ID.</param>
    void UnregisterClientChannel(int cameraId, string clientId);

    /// <summary>
    /// Starts the encoder for the specified camera.
    /// </summary>
    /// <param name="cameraId">The camera ID (0 = back, 1 = front).</param>
    /// <param name="width">The video width.</param>
    /// <param name="height">The video height.</param>
    /// <param name="frameSize">The actual frame size (optional).</param>
    void StartEncoder(int cameraId, int width, int height, int frameSize = 0);

    /// <summary>
    /// Stops the encoder for the specified camera.
    /// </summary>
    /// <param name="cameraId">The camera ID.</param>
    void StopEncoder(int cameraId);

    /// <summary>
    /// Restarts the encoder with new dimensions.
    /// </summary>
    /// <param name="cameraId">The camera ID.</param>
    /// <param name="frameSize">The frame size.</param>
    /// <param name="hintWidth">The hint width.</param>
    /// <param name="hintHeight">The hint height.</param>
    void RestartEncoderWithNewSize(int cameraId, int frameSize, int hintWidth, int hintHeight);

    /// <summary>
    /// Tries to dequeue an encoded frame for the specified client without blocking.
    /// </summary>
    /// <param name="cameraId">The camera ID.</param>
    /// <param name="clientId">The client ID.</param>
    /// <param name="frame">The dequeued frame.</param>
    /// <returns>True if a frame was dequeued.</returns>
    bool TryDequeueFrame(int cameraId, string clientId, out H264FrameEventArgs? frame);

    /// <summary>
    /// Asynchronously waits for and dequeues an encoded frame for the specified client.
    /// Each client receives its own copy of every frame (fan-out, not round-robin).
    /// </summary>
    /// <param name="cameraId">The camera ID.</param>
    /// <param name="clientId">The client ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The dequeued frame.</returns>
    ValueTask<H264FrameEventArgs> DequeueFrameAsync(int cameraId, string clientId, CancellationToken cancellationToken);

    /// <summary>
    /// Synchronously blocks the calling thread until a frame is available, a timeout elapses,
    /// or cancellation is requested. Returns null on timeout/cancellation/channel-closed.
    /// <para>
    /// Must only be called from dedicated OS threads (LongRunning tasks, Thread class) — never
    /// from thread-pool threads. Avoids the 10–70ms async-continuation scheduling latency that
    /// occurs when using <see cref="DequeueFrameAsync"/> on Android's busy thread pool.
    /// </para>
    /// </summary>
    /// <param name="cameraId">The camera ID.</param>
    /// <param name="clientId">The client ID.</param>
    /// <param name="timeoutMs">Maximum wait in milliseconds before returning null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    H264FrameEventArgs? WaitDequeueFrame(int cameraId, string clientId, int timeoutMs, CancellationToken cancellationToken);

    /// <summary>
    /// Clears the cached SPS/PPS values.
    /// Should be called when the encoder is restarted with new dimensions.
    /// </summary>
    void ClearSpsPps();

    /// <summary>
    /// Gets the current SPS and PPS.
    /// </summary>
    /// <returns>Tuple of SPS and PPS.</returns>
    (byte[]? sps, byte[]? pps) GetSpsPps();

    /// <summary>
    /// Gets the actual encoder resolution.
    /// </summary>
    /// <param name="cameraId">The camera ID.</param>
    /// <returns>Tuple of width and height.</returns>
    (int width, int height) GetActualResolution(int cameraId);

    /// <summary>
    /// Feeds a raw frame to the encoder.
    /// </summary>
    /// <param name="cameraId">The camera ID.</param>
    /// <param name="frame">The frame data.</param>
    void FeedFrame(int cameraId, FrameEventArgs frame);

    /// <summary>
    /// Updates the encoder bitrate.
    /// </summary>
    /// <param name="cameraId">The camera ID.</param>
    /// <param name="bitrate">The new bitrate.</param>
    void UpdateBitrate(int cameraId, int bitrate);

    /// <summary>
    /// Gets the expected frame size for the encoder.
    /// </summary>
    /// <param name="cameraId">The camera ID.</param>
    /// <returns>The expected frame size.</returns>
    int GetExpectedFrameSize(int cameraId);

    /// <summary>
    /// Checks if the encoder for the specified camera is currently running.
    /// </summary>
    bool IsEncoderRunning(int cameraId);

    /// <summary>
    /// Requests the encoder to emit an IDR keyframe on the next output frame.
    /// Use when a new client starts playing so their decoder can start immediately
    /// rather than waiting up to one full I-frame interval.
    /// </summary>
    /// <param name="cameraId">The camera ID.</param>
    void RequestKeyFrame(int cameraId);
}
