using System.Collections.Concurrent;
using System.Threading.Channels;
using Android.Content;
using BaluMediaServer.Interfaces;
using BaluMediaServer.Models;
using Com.BaluMedia.CameraStreamer;
using Microsoft.Maui.ApplicationModel;

namespace BaluMediaServer.Platforms.Android.Services;

/// <summary>
/// Service for capturing frames from the back-facing camera.
/// Implements the Android camera callback interface and provides frame data via events.
/// </summary>
public class BackCameraService : Java.Lang.Object, ICameraService, IBackCameraFrameCallback
{
    private CameraFrameCaptureService? _cameraCapture;
    private readonly Context _context;
    private readonly CancellationTokenSource _cts = new();
    //private readonly BlockingCollection<VideoFrame> _videoFrames = new(25);
    private Channel<VideoFrame> _videoFrames = default!;
    private Task? _thread;
    private DateTime _lastFrameTime;
    private readonly TimeSpan _minFrameInterval = TimeSpan.FromMilliseconds(22); // +- 45 fps
    private volatile bool _disposed;  // Prevents JNI access after disposal

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

    private bool _threadRunning = false;

    /// <summary>
    /// Initializes a new instance of the <see cref="BackCameraService"/> class.
    /// Sets up the camera capture service with the back camera callback.
    /// </summary>
    public BackCameraService()
    {
        _context = Platform.CurrentActivity ?? global::Android.App.Application.Context;
        _cameraCapture = new CameraFrameCaptureService(_context);
        _cameraCapture.SetBackCameraCallback(this);
    }

    /// <summary>
    /// Starts capturing frames from the back camera at the specified resolution.
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
                }
            );
            _threadRunning = true;
            _thread = Task.Run(ProcessFramesAsync, _cts.Token);

            // Now start the camera - frames can safely arrive
            _cameraCapture = new(_context);
            _cameraCapture.SetBackCameraCallback(this);
            _cameraCapture?.StartBackCameraCapture(width, height);
        }
        catch (Exception ex)
        {
            SafeInvokeError($"Failed to start capture: {ex.Message}");
        }
    }

    /// <summary>
    /// Stops the back camera capture and completes the frame channel.
    /// </summary>
    public void StopCapture()
    {
        try
        {
            _cameraCapture?.StopBackCameraCapture();
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
            // Check disposed flag FIRST to prevent JNI access after disposal
            if (_disposed || _videoFrames == null)
            {
                SafeRecycleFrame(frame);
                return;
            }

            var now = DateTime.UtcNow;
            if (now - _lastFrameTime < _minFrameInterval)
            {
                SafeRecycleFrame(frame);
                return; // Drop immediately
            }
            _lastFrameTime = DateTime.UtcNow;
            if (!_videoFrames.Writer.TryWrite(frame))
            {
                SafeRecycleFrame(frame);
            }
        }
        catch (Exception ex)
        {
            if (!_disposed) // Only log if not disposing
            {
                SafeInvokeError($"Error processing frame: {ex.Message}");
            }
            SafeRecycleFrame(frame);
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
            try { frame.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Safely recycles or disposes a frame, catching all exceptions during disposal.
    /// Used when service may be in the process of being disposed.
    /// </summary>
    /// <param name="frame">The video frame to safely recycle.</param>
    private void SafeRecycleFrame(VideoFrame? frame)
    {
        if (frame == null) return;
        try
        {
            if (!_disposed && _cameraCapture != null)
            {
                _cameraCapture.RecycleFrame(frame);
            }
            else
            {
                frame.Dispose();
            }
        }
        catch
        {
            // Swallow all exceptions during frame cleanup
            try { frame.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Processes a single video frame and raises the FrameReceived event.
    /// Checks disposed flag before accessing VideoFrame JNI methods.
    /// </summary>
    /// <param name="frame">The video frame to process.</param>
    public void ProcessFrame(VideoFrame frame)
    {
        // Check disposed BEFORE accessing VideoFrame JNI methods to prevent SIGSEGV
        if (_disposed) return;

        var args = new FrameEventArgs
        {
            Data = frame.GetData()!,
            Width = frame.Width,
            Height = frame.Height,
            Timestamp = frame.Timestamp,
            Format = frame.Format,
            CameraId = frame.CameraId
        };

        // Check disposed again before invoking event
        if (_disposed) return;

        try
        {
            FrameReceived?.Invoke(this, args);
        }
        catch
        {
            // Swallow subscriber exceptions
        }
    }

    /// <summary>
    /// Continuously processes frames from the channel until cancelled.
    /// Checks disposed flag to prevent JNI access after disposal.
    /// </summary>
    /// <returns>A task that represents the asynchronous frame processing operation.</returns>
    public async Task ProcessFramesAsync()
    {
        // Check both _threadRunning and _disposed for clean shutdown
        while (!_cts.IsCancellationRequested && _threadRunning && !_disposed)
        {
            try
            {
                // Check disposed before blocking read
                if (_disposed) break;

                // Use async read - this is blocking the thread currently
                var frame = await _videoFrames.Reader.ReadAsync(_cts.Token).ConfigureAwait(false);
                try
                {
                    // Check disposed after read completes, before JNI access
                    if (_disposed)
                    {
                        SafeRecycleFrame(frame);
                        break;
                    }
                    ProcessFrame(frame);
                }
                finally
                {
                    // Safely recycle frame buffer
                    SafeRecycleFrame(frame);
                }
            }
            catch (OperationCanceledException)
            {
                break; // Normal cancellation
            }
            catch (ChannelClosedException)
            {
                break; // Channel completed during disposal
            }
            catch (Exception ex)
            {
                // Don't log errors during disposal
                if (!_disposed)
                {
                    OnError(ex.Message);
                }
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
    /// Releases all resources used by the back camera service.
    /// Sets _disposed flag FIRST to stop processing task before native cleanup.
    /// </summary>
    /// <param name="disposing">True to release both managed and unmanaged resources.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Set disposed flag FIRST to stop processing task from accessing JNI
            _disposed = true;
            _threadRunning = false;

            // Signal channel completion and cancel token to unblock processing task
            _videoFrames?.Writer.TryComplete();
            _cts?.Cancel();

            // Wait for processing task to fully stop BEFORE touching native resources
            if (_thread != null)
            {
                try
                {
                    if (!_thread.Wait(TimeSpan.FromSeconds(5)))
                    {
                        global::Android.Util.Log.Warn("[BackCameraService]", "Processing task did not stop within 5s timeout");
                    }
                }
                catch { }
                _thread = null;
            }

            // Now safe to stop camera - processing task has exited
            try
            {
                _cameraCapture?.StopBackCameraCapture();
            }
            catch { }

            try
            {
                _cameraCapture?.Dispose();
            }
            catch { }
            _cameraCapture = null;

            _cts?.Dispose();
        }
        base.Dispose(disposing);
    }
}
