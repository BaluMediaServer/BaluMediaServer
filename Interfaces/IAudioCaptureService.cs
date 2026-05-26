using BaluMediaServer.Models;

namespace BaluMediaServer.Interfaces;

/// <summary>
/// Contract for an audio capture service that produces raw PCM frames from the device microphone.
/// Mirrors <see cref="ICameraService"/> for the video pipeline: lifecycle methods plus a
/// <see cref="FrameReceived"/> event for downstream encoders.
/// </summary>
public interface IAudioCaptureService : IDisposable
{
    /// <summary>
    /// Raised on the capture thread when a new PCM frame is available.
    /// Subscribers must be tolerant of being invoked from a background thread.
    /// </summary>
    event EventHandler<AudioFrameEventArgs>? FrameReceived;

    /// <summary>
    /// Raised when an unrecoverable error occurs during capture (e.g. permission denied,
    /// AudioRecord state error). Capture is stopped before this fires.
    /// </summary>
    event EventHandler<string>? ErrorOccurred;

    /// <summary>
    /// True while the capture loop is running and producing frames.
    /// </summary>
    bool IsCapturing { get; }

    /// <summary>
    /// Starts microphone capture at the requested sample rate and channel count.
    /// </summary>
    /// <param name="sampleRateHz">Sample rate in Hz (typical: 44100 or 48000).</param>
    /// <param name="channels">Channel count: 1 for mono, 2 for stereo.</param>
    void StartCapture(int sampleRateHz, int channels);

    /// <summary>
    /// Stops microphone capture and releases the underlying AudioRecord.
    /// Safe to call when not currently capturing.
    /// </summary>
    void StopCapture();
}
