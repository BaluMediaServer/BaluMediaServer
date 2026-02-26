using BaluMediaServer.Models;

namespace BaluMediaServer.RTSP.Streaming;

/// <summary>
/// Interface for managing H.264 encoder lifecycle and frame queue.
/// </summary>
public interface IH264EncoderManager
{
    /// <summary>
    /// Event raised when an H.264 frame is encoded.
    /// </summary>
    event EventHandler<H264FrameEventArgs>? FrameEncoded;

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
    /// Tries to dequeue an encoded frame.
    /// </summary>
    /// <param name="cameraId">The camera ID.</param>
    /// <param name="frame">The dequeued frame.</param>
    /// <returns>True if a frame was dequeued.</returns>
    bool TryDequeueFrame(int cameraId, out H264FrameEventArgs? frame);

    /// <summary>
    /// Asynchronously waits for and dequeues an encoded frame.
    /// This is the preferred method over TryDequeueFrame as it eliminates polling overhead.
    /// </summary>
    /// <param name="cameraId">The camera ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The dequeued frame.</returns>
    ValueTask<H264FrameEventArgs> DequeueFrameAsync(int cameraId, CancellationToken cancellationToken);

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
}
