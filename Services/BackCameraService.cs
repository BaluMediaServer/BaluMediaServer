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
    // Channel holds managed FrameEventArgs (not Java VideoFrame objects) so that the
    // processing thread never needs JNI calls — avoiding Mono GC thread-state corruption.
    private Channel<FrameEventArgs> _frameChannel = default!;
    private Task? _thread;
    private DateTime _lastFrameTime;
    private readonly TimeSpan _minFrameInterval = TimeSpan.FromMilliseconds(22); // +- 45 fps
    private volatile bool _disposed;  // Prevents JNI access after disposal
    private int _channelCapacity;

    /// <summary>
    /// Calculates the channel capacity based on resolution to limit memory usage.
    /// </summary>
    /// <param name="width">The video width in pixels.</param>
    /// <param name="height">The video height in pixels.</param>
    /// <returns>The calculated channel capacity.</returns>
    private static int GetChannelCapacity(int width, int height)
    {
        // Capacity=1 with DropOldest ensures the encoder always gets the freshest frame,
        // eliminating queue-induced latency (was 2-10 frames = 80-400ms at 25fps).
        return 1;
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
    private bool _loggedFirstProcessedFrame = false;

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
            // Stop and release the old native camera BEFORE creating a new one.
            // Without this, the old Camera2 device holds the camera lock and the
            // new instance can't open it — causing zero frames for 10-30+ seconds
            // until GC finalizes the orphaned session.
            try { _cameraCapture?.StopBackCameraCapture(); } catch { }
            try { _cameraCapture?.Dispose(); } catch { }
            _cameraCapture = null;

            // Create channel BEFORE starting capture to avoid race condition
            // Use dynamic capacity based on resolution to limit memory usage
            _channelCapacity = GetChannelCapacity(width, height);
            // Channel holds managed FrameEventArgs — DropOldest is safe since there
            // are no native resources to recycle (data was copied in OnFrameAvailable).
            _frameChannel = Channel.CreateBounded<FrameEventArgs>(
                new BoundedChannelOptions(_channelCapacity)
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
            _loggedFirstProcessedFrame = false;
            _cameraCapture?.StartBackCameraCapture(width, height);
            global::Android.Util.Log.Info("[BackCameraService]", $"StartCapture({width}x{height}): camera started, channel capacity={_channelCapacity}, cts cancelled={_cts.IsCancellationRequested}");
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("[BackCameraService]", $"StartCapture({width}x{height}) FAILED: {ex.Message}");
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
            _frameChannel?.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            SafeInvokeError($"Failed to stop capture: {ex.Message}");
        }
    }

    /// <summary>
    /// Callback invoked by the native camera service when a new frame is available.
    /// Marshals all data from the Java VideoFrame into a managed FrameEventArgs and
    /// recycles the native frame immediately. This ensures the processing thread
    /// (ProcessFramesAsync) never crosses the JNI boundary, preventing the Mono GC
    /// thread-state corruption that causes "Cannot transition thread from RUNNING
    /// with DONE_BLOCKING" SIGABRT crashes.
    /// </summary>
    /// <param name="frame">The video frame from the camera.</param>
    public void OnFrameAvailable(VideoFrame frame)
    {
        try
        {
            // Check disposed flag FIRST to prevent JNI access after disposal
            if (_disposed || _frameChannel == null)
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

            // Marshal all data from Java object NOW (we're on the Java callback thread,
            // already in JNI context) so the processing thread stays pure managed code.
            var data = frame.GetData();
            if (data == null || data.Length == 0)
            {
                SafeRecycleFrame(frame);
                return;
            }

            var args = new FrameEventArgs
            {
                Data = data,
                Width = frame.Width,
                Height = frame.Height,
                Timestamp = frame.Timestamp,
                Format = frame.Format,
                CameraId = frame.CameraId
            };

            // Recycle native frame IMMEDIATELY — we've copied everything we need
            SafeRecycleFrame(frame);

            // Channel uses DropOldest which is safe — FrameEventArgs is managed-only
            _frameChannel.Writer.TryWrite(args);
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
    /// Raises the FrameReceived event with pre-marshaled managed data.
    /// No JNI calls — all Java object access happened in OnFrameAvailable.
    /// </summary>
    /// <param name="args">The pre-marshaled frame data.</param>
    public void ProcessFrame(FrameEventArgs args)
    {
        if (_disposed) return;

        if (!_loggedFirstProcessedFrame)
        {
            _loggedFirstProcessedFrame = true;
            global::Android.Util.Log.Info("[BackCameraService]", $"First processed frame: {args.Width}x{args.Height}, {args.Data?.Length ?? 0} bytes, subscribers={FrameReceived?.GetInvocationList().Length ?? 0}");
        }

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
    /// Continuously processes managed frame data from the channel until cancelled.
    /// This thread makes ZERO JNI calls — all Java object access was done in
    /// OnFrameAvailable. This prevents the Mono GC thread-state corruption crash.
    /// </summary>
    /// <returns>A task that represents the asynchronous frame processing operation.</returns>
    public async Task ProcessFramesAsync()
    {
        while (!_cts.IsCancellationRequested && _threadRunning && !_disposed)
        {
            try
            {
                if (_disposed) break;

                var args = await _frameChannel.Reader.ReadAsync(_cts.Token).ConfigureAwait(false);

                if (_disposed) break;

                ProcessFrame(args);
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
            _frameChannel?.Writer.TryComplete();
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
