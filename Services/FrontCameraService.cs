using System.Collections.Concurrent;
using System.Threading.Channels;
using Android.Content;
using Android.Runtime;
using Android.Util;
using BaluMediaServer.Interfaces;
using BaluMediaServer.Models;
using Com.BaluMedia.CameraStreamer;
using Microsoft.Maui.ApplicationModel;

namespace BaluMediaServer.Platforms.Android.Services;

/// <summary>
/// Service for capturing frames from the front-facing camera.
/// Implements the Android camera callback interface and provides frame data via events.
/// </summary>
public class FrontCameraService : Java.Lang.Object, ICameraService, IFrontCameraFrameCallback
{
    private CameraFrameCaptureService? _cameraCapture;
    private readonly Context _context;
    private readonly CancellationTokenSource _cts = new();
    private Channel<VideoFrame> _videoFrames;
    private Task? _thread;
    private DateTime _lastFrameTime;
    private readonly TimeSpan _minFrameInterval = TimeSpan.FromMilliseconds(22); // +- 45 fps

    /// <summary>
    /// Calculates the channel capacity based on resolution to limit memory usage.
    /// </summary>
    /// <param name="width">The video width in pixels.</param>
    /// <param name="height">The video height in pixels.</param>
    /// <returns>The calculated channel capacity.</returns>
    private static int GetChannelCapacity(int width, int height)
    {
        int frameSize = (width * height * 3) / 2; // YUV420 frame size
        // Target ~8MB max buffer to prevent memory issues at high resolutions
        // Keep minimal buffer for high-res to reduce memory pressure
        const int maxBufferSize = 8_000_000;
        int capacity = Math.Max(2, maxBufferSize / frameSize);
        return Math.Min(capacity, 10); // Cap at 10 frames max
    }

    /// <summary>
    /// Event raised when a new frame is received and processed from the camera.
    /// </summary>
    public event EventHandler<FrameEventArgs>? FrameReceived;

    /// <summary>
    /// Event raised when an error occurs during camera operations.
    /// </summary>
    public event EventHandler<string>? ErrorOccurred;

    /// <summary>
    /// Indicates whether the frame processing thread is running.
    /// </summary>
    public bool _threadRunning = false;

    /// <summary>
    /// Initializes a new instance of the <see cref="FrontCameraService"/> class.
    /// Sets up the camera capture service with the front camera callback.
    /// </summary>
    public FrontCameraService()
    {
        _context = Platform.CurrentActivity ?? global::Android.App.Application.Context;
        _cameraCapture = new CameraFrameCaptureService(_context);
        _cameraCapture.SetFrontCameraCallback(this);
    }

    /// <summary>
    /// Starts capturing frames from the front camera at the specified resolution.
    /// </summary>
    /// <param name="width">The desired capture width in pixels. Default is 640.</param>
    /// <param name="height">The desired capture height in pixels. Default is 480.</param>
    public void StartCapture(int width = 640, int height = 480)
    {
        try
        {
            // Create channel BEFORE starting capture to avoid race condition
            // Use dynamic capacity based on resolution to limit memory usage
            int channelCapacity = GetChannelCapacity(width, height);
            _videoFrames = Channel.CreateBounded<VideoFrame>(
                    new BoundedChannelOptions(channelCapacity)
                    {
                        FullMode = BoundedChannelFullMode.DropOldest,
                        SingleReader = true,
                        SingleWriter = false
                    });
            _threadRunning = true;
            _thread = Task.Run(ProcessFramesAsync, _cts.Token);

            // Now start the camera - frames can safely arrive
            _cameraCapture?.StartFrontCameraCapture(width, height);
        }
        catch (Exception ex)
        {
            SafeInvokeError($"Failed to start capture: {ex.Message}");
        }
    }

    /// <summary>
    /// Stops the front camera capture and completes the frame channel.
    /// </summary>
    public void StopCapture()
    {
        try
        {
            _cameraCapture?.StopFrontCameraCapture();
            _threadRunning = false;
            _videoFrames?.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            SafeInvokeError($"Failed to stop capture: {ex.Message}");
        }
    }

    /// <summary>
    /// Callback invoked by the native camera service when a new frame is available.
    /// Implements rate limiting and queues frames for processing.
    /// </summary>
    /// <param name="frame">The video frame from the camera.</param>
    public void OnFrameAvailable(VideoFrame frame)
    {
        try
        {
            // Check if channel exists (may be called before StartCapture)
            if (_videoFrames == null)
            {
                RecycleFrame(frame);
                return;
            }

            var now = DateTime.UtcNow;
            if (now - _lastFrameTime < _minFrameInterval)
            {
                RecycleFrame(frame);
                return; // Drop immediately
            }
            _lastFrameTime = DateTime.UtcNow;
            if (!_videoFrames.Writer.TryWrite(frame))
            {
                RecycleFrame(frame);
            }
        }
        catch (Exception ex)
        {
            SafeInvokeError($"Error processing frame: {ex.Message}");
            RecycleFrame(frame);
        }
    }

    /// <summary>
    /// Recycles the frame buffer back to the native pool for reuse.
    /// This reduces memory allocations at high resolutions.
    /// </summary>
    /// <param name="frame">The video frame to recycle.</param>
    private void RecycleFrame(VideoFrame? frame)
    {
        if (frame == null) return;
        try
        {
            _cameraCapture?.RecycleFrame(frame);
        }
        catch
        {
            // Fallback to dispose if recycle fails
            frame.Dispose();
        }
    }

    /// <summary>
    /// Processes a single video frame and raises the FrameReceived event.
    /// </summary>
    /// <param name="frame">The video frame to process.</param>
    public void ProcessFrame(VideoFrame frame)
    {
        var args = new FrameEventArgs
        {
            Data = frame.GetData()!,
            Width = frame.Width,
            Height = frame.Height,
            Timestamp = frame.Timestamp,
            Format = frame.Format,
            CameraId = frame.CameraId
        };
        FrameReceived?.Invoke(this, args);
    }

    /// <summary>
    /// Continuously processes frames from the channel until cancelled.
    /// </summary>
    /// <returns>A task that represents the asynchronous frame processing operation.</returns>
    public async Task ProcessFramesAsync()
    {
        while (!_cts.IsCancellationRequested && _threadRunning)
        {
            try
            {
                // Use async read - this is blocking the thread currently
                var frame = await _videoFrames.Reader.ReadAsync(_cts.Token).ConfigureAwait(false);
                try
                {
                    ProcessFrame(frame);
                }
                finally
                {
                    // Recycle frame buffer back to native pool for reuse
                    RecycleFrame(frame);
                }
            }
            catch (OperationCanceledException)
            {
                break; // Normal cancellation
            }
            catch (Exception ex)
            {
                OnError(ex.Message);
            }
        }
    }

    /// <summary>
    /// Raises the ErrorOccurred event with the specified error message.
    /// </summary>
    /// <param name="error">The error message to report.</param>
    public void OnError(string error)
    {
        SafeInvokeError(error);
    }

    /// <summary>
    /// Safely invokes the ErrorOccurred event, catching any subscriber exceptions
    /// to prevent crashes in native callback contexts.
    /// </summary>
    private void SafeInvokeError(string error)
    {
        try
        {
            ErrorOccurred?.Invoke(this, error);
        }
        catch
        {
            // Swallow subscriber exceptions to prevent native callback crash
        }
    }

    /// <summary>
    /// Releases all resources used by the front camera service.
    /// </summary>
    /// <param name="disposing">True to release both managed and unmanaged resources.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cameraCapture?.StopFrontCameraCapture();
            _cts?.Cancel();
            _videoFrames?.Writer.TryComplete();
            if (_thread != null)
            {
                try
                {
                    _thread.Wait(TimeSpan.FromSeconds(5));
                }
                catch { }
                _thread.Dispose();
            }
            _cameraCapture?.Dispose();
            _cameraCapture = null;
            _cts?.Dispose();
        }
        base.Dispose(disposing);
    }
}
