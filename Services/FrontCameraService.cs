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

public class FrontCameraService : Java.Lang.Object, ICameraService, IFrontCameraFrameCallback
{
    private CameraFrameCaptureService? _cameraCapture;
    private readonly Context _context;
    private readonly CancellationTokenSource _cts = new();
    private Channel<VideoFrame> _videoFrames;
    private Task? _thread;
    private DateTime _lastFrameTime;
    private readonly TimeSpan _minFrameInterval = TimeSpan.FromMilliseconds(22); // +- 45 fps 
    public event EventHandler<FrameEventArgs>? FrameReceived;
    public event EventHandler<string>? ErrorOccurred;
    public bool _threadRunning = false;
    public FrontCameraService()
    {
        _context = Platform.CurrentActivity ?? global::Android.App.Application.Context;
        _cameraCapture = new CameraFrameCaptureService(_context);
        _cameraCapture.SetFrontCameraCallback(this);
    }
    public void StartCapture(int width = 640, int height = 480)
    {
        try
        {
            _cameraCapture?.StartFrontCameraCapture(width, height);
            _videoFrames = Channel.CreateBounded<VideoFrame>(
                    new BoundedChannelOptions(25)
                    {
                        FullMode = BoundedChannelFullMode.Wait,
                        SingleReader = true,
                        SingleWriter = false
                    });
            _threadRunning = true;
            _thread = Task.Run(ProcessFramesAsync, _cts.Token);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Failed to start capture: {ex.Message}");
        }
    }
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
            ErrorOccurred?.Invoke(this, $"Failed to stop capture: {ex.Message}");
        }
    }
    // IFrameCallback implementation
    public void OnFrameAvailable(VideoFrame frame)
    {
        try
        {
            var now = DateTime.UtcNow;
            if (now - _lastFrameTime < _minFrameInterval)
            {
                frame?.Dispose();
                return; // Drop immediately
            }
            _lastFrameTime = DateTime.UtcNow;
            if (!_videoFrames.Writer.TryWrite(frame))
            {
                frame?.Dispose();
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Error processing frame: {ex.Message}");
            frame?.Dispose();
        }
    }
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
                    frame?.Dispose();
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
    public void OnError(string error)
    {
        ErrorOccurred?.Invoke(this, error);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cameraCapture?.StopFrontCameraCapture();
            _cts?.Cancel();
            _videoFrames.Writer.TryComplete();
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
