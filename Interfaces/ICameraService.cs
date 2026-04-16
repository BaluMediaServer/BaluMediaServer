using BaluMediaServer.Models;

namespace BaluMediaServer.Interfaces;

/// <summary>
/// Interface for camera capture services.
/// Defines the contract for front and back camera implementations.
/// </summary>
public interface ICameraService : IDisposable
{
    /// <summary>
    /// Starts camera capture at the specified resolution.
    /// </summary>
    /// <param name="width">The desired capture width in pixels.</param>
    /// <param name="height">The desired capture height in pixels.</param>
    public void StartCapture(int width, int height);

    /// <summary>
    /// Stops the camera capture and releases camera resources.
    /// </summary>
    public void StopCapture();

    /// <summary>
    /// Processes a single pre-marshaled frame and raises the FrameReceived event.
    /// </summary>
    /// <param name="args">The managed frame data.</param>
    public void ProcessFrame(FrameEventArgs args);

    /// <summary>
    /// Asynchronously processes frames from the frame queue.
    /// Runs continuously until cancelled.
    /// </summary>
    /// <returns>A task that represents the asynchronous frame processing operation.</returns>
    public Task ProcessFramesAsync();
}
