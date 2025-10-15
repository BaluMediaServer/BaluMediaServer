using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;
using Android.Util;
using BaluMediaServer.Models;
using BaluMediaServer.Repositories;
using Java.Lang;
using JetBrains.Annotations;
namespace BaluMediaServer.Services;

public class MjpegServer : IDisposable
{
    private readonly HttpListener _listener;
    private Task? _thread, _watchdog;
    private DateTime _lastFrame = DateTime.UtcNow, _lastBackFrameSent = DateTime.UtcNow, _lastFrontFrameSent = DateTime.UtcNow;
    private readonly double _frameIntervalMs;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<HttpListenerResponse, string> _clientsFront = new(), _clientsBack = new();
    private ConcurrentDictionary<string, DateTime> _clientLastFrameTime = new();
    private int _quality = 80;
    private int _port;

    public MjpegServer(int port = 8089, int quality = 30)
    {
        _frameIntervalMs = 1000 / 30;
        _port = port;
        _listener = new();
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/Back/");
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/Front/");
        _quality = quality;
        Server.OnNewBackFrame += OnBackFrameAvailable;
        Server.OnNewFrontFrame += OnFrontFrameAvailable;
        _watchdog = Task.Run(Watchdog, _cts.Token);
    }
    public void Dispose()
    {
        _cts?.Cancel();
        Server.OnNewBackFrame -= OnBackFrameAvailable;
        Server.OnNewFrontFrame -= OnFrontFrameAvailable;
        
        //EventBuss.Command -= OnCommandSend;
        _listener?.Close();
        
        try
        {
            _thread?.Dispose();
            _watchdog?.Dispose();
        }
        catch { }
        _cts?.Dispose();
    }
    private void OnCommandSend(BussCommand command)
    {
        switch (command)
        {
            case BussCommand.START_MJPEG_SERVER:
                Start();
                break;
            case BussCommand.STOP_MJPEG_SERVER:
                Stop();
                break;
        }
    }
    private async Task Watchdog()
    {
        while (!_cts.IsCancellationRequested)
        {
            if ((DateTime.UtcNow - _lastFrame).Seconds > 5 && _listener.IsListening)
            {
                EventBuss.SendCommand(BussCommand.START_CAMERA_FRONT);
                EventBuss.SendCommand(BussCommand.START_CAMERA_BACK);
                await Task.Delay(5000, _cts.Token);
            }else
                await Task.Delay(1000, _cts.Token);
        }
    }
    private void OnBackFrameAvailable(object? sender, FrameEventArgs arg)
    {
        if (arg != null && arg.Data != null && arg.Data.Length > 0)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastBackFrameSent).TotalMilliseconds < _frameIntervalMs)
                return;
            
            _lastBackFrameSent = now;
            var jpegData = Server.EncodeToJpeg(arg.Data, arg.Width, arg.Height, Android.Graphics.ImageFormatType.Nv21, _quality);
            Task.Run(async () => await PushBackFrameAsync(jpegData), _cts.Token);
        }
    }
    private void OnFrontFrameAvailable(object? sender, FrameEventArgs arg)
    {
        if (arg != null && arg.Data != null && arg.Data.Length > 0)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastFrontFrameSent).TotalMilliseconds < _frameIntervalMs)
                return;

            _lastFrontFrameSent = now;
            var jpegData = Server.EncodeToJpeg(arg.Data, arg.Width, arg.Height, Android.Graphics.ImageFormatType.Nv21, _quality);
            Task.Run(async () => await PushFrontFrameAsync(jpegData), _cts.Token);
        }
    }
    public void Start()
    {
        try
        {
            _listener.Start();
            Log.Debug("MJPEG SERVER", "STARTING SERVER");
            EventBuss.SendCommand(BussCommand.START_CAMERA_FRONT);
            EventBuss.SendCommand(BussCommand.START_CAMERA_BACK);
            _thread = Task.Run(ListenLoop, _cts.Token);
            Log.Debug("MJPEG SERVER", "STARTED SERVER");
        }
        catch (System.Exception ex)
        {
            // Assuming that is already started
        }
        
    }

    public void Stop()
    {
        _clientsBack.Clear();
        _clientsFront.Clear();
        EventBuss.SendCommand(BussCommand.STOP_CAMERA_BACK);
        EventBuss.SendCommand(BussCommand.STOP_CAMERA_FRONT);
        _listener.Stop();
    }

    private async Task ListenLoop()
    {
        while (_listener.IsListening && !_cts.IsCancellationRequested)
        {
            try
            {
                Log.Debug("MJPEG SERVER", "WAITING CLIENT");
                var ctx = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleClient(ctx), _cts.Token);
            }
            catch
            {
                // Listener was stopped or failed
            }
        }
    }

    private async Task HandleClient(HttpListenerContext context)
    {
        Log.Debug("MJPEG SERVER", "CLIENT RECEIVED");
        var response = context.Response;
        response.ContentType = "multipart/x-mixed-replace; boundary=--frame";
        response.StatusCode = 200;
        response.SendChunked = true;
        var uri = context.Request.Url?.ToString() ?? string.Empty;
        if (System.String.IsNullOrEmpty(uri)) return;
        if (uri.Contains("Back"))
        {
            _clientsBack.TryAdd(response, Guid.NewGuid().ToString());
        }
        else
        {
            _clientsFront.TryAdd(response, Guid.NewGuid().ToString());
        }
        Log.Debug("MJPEG SERVER", "SERVING FRAMES");
        try
        {
            while (response.OutputStream.CanWrite) await Task.Delay(100, _cts.Token);
        }
        catch(System.Exception ex)
        {
            Log.Debug("MJPEG SERVER", $"ERROR: {ex.Message}");
        }
        finally
        {
            if (uri.Contains("Back"))
            {
                _clientsBack.TryRemove(response, out var _);
            }
            else
            {
                _clientsFront.TryRemove(response, out var _);
            }

            response.OutputStream.Close();
            response.Close();
        }
    }
    private async Task WriteDataAsync(HttpListenerResponse client, byte[] jpegBytes)
    {
        try
        {
            if (_clientsBack.TryGetValue(client, out var clientId))
            {
                var now = DateTime.UtcNow;
                
                if (!_clientLastFrameTime.TryGetValue(clientId, out var lastTime))
                {
                    _clientLastFrameTime[clientId] = now;
                }
                else if ((now - lastTime).TotalMilliseconds < _frameIntervalMs)
                {
                    return;
                }
                
                _clientLastFrameTime[clientId] = now;
            }
            var header = Encoding.ASCII.GetBytes(
                "\r\n--frame\r\nContent-Type: image/jpeg\r\nContent-Length: " + jpegBytes.Length + "\r\n\r\n");
            await client.OutputStream.WriteAsync(header, 0, header.Length, _cts.Token).ConfigureAwait(false);
            await client.OutputStream.WriteAsync(jpegBytes, 0, jpegBytes.Length, _cts.Token).ConfigureAwait(false);
            await client.OutputStream.FlushAsync(_cts.Token);
        }
        catch
        {
            _clientsBack.TryRemove(client, out var _);
            try { client.OutputStream.Close(); client.Close(); } catch { }
        }
    }
    public async Task PushBackFrameAsync(byte[] jpegBytes)
    {
        Log.Debug("MJPEG SERVER", "SENDING BACK FRAME");
        var tasks = _clientsBack.Keys.Select(async p => await WriteDataAsync(p, jpegBytes));
        await Task.WhenAll(tasks);
    }
    public async Task PushFrontFrameAsync(byte[] jpegBytes)
    {
        Log.Debug("MJPEG SERVER", "SENDING FRONT FRAME");
        var tasks = _clientsFront.Keys.Select(async p => await WriteDataAsync(p, jpegBytes));
        await Task.WhenAll(tasks);
    }
}
