using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using BaluMediaServer.Models;
using BaluMediaServer.Repositories;
using Java.Lang;
using JetBrains.Annotations;
namespace BaluMediaServer.Services;

public class MjpegServer : IDisposable
{
    private readonly HttpListener _listener;
    private Task? _thread, _watchdog;
    private DateTime _lastFrame = DateTime.UtcNow;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<HttpListenerResponse, string> _clientsFront = new(), _clientsBack = new();
    private int _quality = 80;
    private int _port;

    public MjpegServer(int port = 8089, int quality = 80)
    {
        _port = port;
        _listener = new();
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/Back/");
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/Front/");
        _quality = quality;
        Server.OnNewBackFrame += OnBackFrameAvailable;
        Server.OnNewFrontFrame += OnFrontFrameAvailable;
        _watchdog = Task.Run(Watchdog, _cts.Token);
        // Needs verification with Server to avoid reduplicate instances and overload the CPU
        //EventBuss.Command += OnCommandSend;
    }
    public void Dispose()
    {
        _cts?.Cancel();
        Server.OnNewBackFrame -= OnBackFrameAvailable;
        Server.OnNewFrontFrame -= OnFrontFrameAvailable;
        
        //EventBuss.Command -= OnCommandSend;
        _listener?.Close();
        _cts?.Dispose();
        try
        {
            _thread?.Dispose();
            _watchdog?.Dispose();
        }
        catch { }
        
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
            _ = this.PushBackFrameAsync(Server.EncodeToJpeg(arg.Data, arg.Width, arg.Height, Android.Graphics.ImageFormatType.Nv21, _quality));
            _lastFrame = DateTime.UtcNow;
        }
    }
    private void OnFrontFrameAvailable(object? sender, FrameEventArgs arg)
    {
        if (arg != null && arg.Data != null && arg.Data.Length > 0)
        {
            _ = this.PushFrontFrameAsync(Server.EncodeToJpeg(arg.Data, arg.Width, arg.Height, Android.Graphics.ImageFormatType.Nv21, _quality));
            _lastFrame = DateTime.UtcNow;
        }
    }
    public void Start()
    {
        try
        {
            _listener.Start();
            EventBuss.SendCommand(BussCommand.START_CAMERA_FRONT);
            EventBuss.SendCommand(BussCommand.START_CAMERA_BACK);
            _thread = Task.Run(ListenLoop, _cts.Token);
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

        try
        {
            while (response.OutputStream.CanWrite) await Task.Delay(100, _cts.Token);
        }
        catch
        {
            // Client disconnected
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
            var header = Encoding.ASCII.GetBytes(
                "\r\n--frame\r\nContent-Type: image/jpeg\r\nContent-Length: " + jpegBytes.Length + "\r\n\r\n");
            await client.OutputStream.WriteAsync(header, 0, header.Length, _cts.Token).ConfigureAwait(false);
            await client.OutputStream.WriteAsync(jpegBytes, 0, jpegBytes.Length, _cts.Token).ConfigureAwait(false);
            client.OutputStream.Flush();
        }
        catch
        {
            _clientsBack.TryRemove(client, out var _);
            try { client.OutputStream.Close(); client.Close(); } catch { }
        }
    }
    public async Task PushBackFrameAsync(byte[] jpegBytes)
    {
        var tasks = _clientsBack.Keys.Select(async p => await WriteDataAsync(p, jpegBytes));
        await Task.WhenAll(tasks);
    }
    public async Task PushFrontFrameAsync(byte[] jpegBytes)
    {
        var tasks = _clientsBack.Keys.Select(async p => await WriteDataAsync(p, jpegBytes));
        await Task.WhenAll(tasks);
    }
}
