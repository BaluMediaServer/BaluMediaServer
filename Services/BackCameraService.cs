using System.Collections.Concurrent;
using Android.Content;
using BaluMediaServer.Interfaces;
using BaluMediaServer.Models;
using Com.BaluMedia.CameraStreamer;
using Microsoft.Maui.ApplicationModel;

namespace BaluMediaServer.Platforms.Android.Services;

public class BackCameraService : Java.Lang.Object, ICameraService, IBackCameraFrameCallback
{
    private CameraFrameCaptureService? _cameraCapture;
    private readonly Context _context;
    private readonly CancellationTokenSource _cts = new();
    private readonly BlockingCollection<VideoFrame> _videoFrames = new(25);
    private Task? _thread;
    private DateTime _lastFrameTime;
    private readonly TimeSpan _minFrameInterval = TimeSpan.FromMilliseconds(22); // +- 45 fps 
    public event EventHandler<FrameEventArgs>? FrameReceived;
    public event EventHandler<string>? ErrorOccurred;
    private bool _threadRunning = false;
    public BackCameraService()
    {
        _context = Platform.CurrentActivity ?? global::Android.App.Application.Context;
        _cameraCapture = new CameraFrameCaptureService(_context);
        _cameraCapture.SetBackCameraCallback(this);
    }
    public void StartCapture(int width = 640, int height = 480)
    {
        try
        {
            _cameraCapture?.StartBackCameraCapture(width, height);
            _threadRunning = true;
            Task.Run(ProcessFrames, _cts.Token);
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
            _cameraCapture?.StopBackCameraCapture();
            _threadRunning = false;
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
            // Direct processing - no queue, no thread
            if (!_videoFrames.TryAdd(frame))
            {
                frame?.Dispose(); // drop safely
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
    public void ProcessFrames()
    {
        while (!_cts.IsCancellationRequested && _threadRunning)
        {
            try
            {
                var frame = _videoFrames.Take(_cts.Token);
                if (frame != null)
                {
                    //await _sem.WaitAsync(_cts.Token);
                    try
                    {
                        var now = DateTime.UtcNow;
                        if (now - _lastFrameTime < _minFrameInterval)
                        {
                            frame?.Dispose();
                            continue;
                        }
                        _lastFrameTime = now;
                        ProcessFrame(frame);
                    }
                    finally
                    {
                        //_sem.Release();
                        frame?.Dispose();
                    }
                }
            }
            catch(Exception ex)
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
            _cameraCapture?.StopBackCameraCapture();
            _threadRunning = false;
            _cts?.Cancel();
            if (_thread != null)
            {
                try
                {
                    _thread.Wait(TimeSpan.FromSeconds(5));
                }
                catch {}
                _thread.Dispose();
            }
            _cameraCapture?.Dispose();
            _cameraCapture = null;
            _cts?.Dispose();
        }
        base.Dispose(disposing);
    }
}
