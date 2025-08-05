using System.Collections.Concurrent;
using System.Net;
using System.Text;
using BaluMediaServer.Models;
using BaluMediaServer.Repositories;
namespace BaluMediaServer.Services;

public class MjpegServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly ConcurrentDictionary<HttpListenerResponse, string> _clientsFront = new(), _clientsBack = new();
    private readonly object _lockFront = new(), _lockBack = new();
    private int _quality = 80;
    public int Port { get; }

    public MjpegServer(int port = 8089, int quality = 80)
    {
        Port = port;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{Port}/Back/");
        _listener.Prefixes.Add($"http://+:{Port}/Front/");
        _quality = quality;
        Server.OnNewBackFrame += OnBackFrameAvailable;
        Server.OnNewFrontFrame += OnFrontFrameAvailable;
    }
    public void Dispose()
    {
        Server.OnNewBackFrame -= OnBackFrameAvailable;
        Server.OnNewFrontFrame -= OnFrontFrameAvailable;
        _listener?.Close();
    }
    private void OnBackFrameAvailable(object? sender, FrameEventArgs arg)
    {
        if (arg != null && arg.Data != null && arg.Data.Length > 0)
        {
            this.PushBackFrame(Server.EncodeToJpeg(arg.Data, arg.Width, arg.Height, Android.Graphics.ImageFormatType.Nv21, _quality));
        }
    }
    private void OnFrontFrameAvailable(object? sender, FrameEventArgs arg)
    {
        if (arg != null && arg.Data != null && arg.Data.Length > 0)
        {
            this.PushFrontFrame(Server.EncodeToJpeg(arg.Data, arg.Width, arg.Height, Android.Graphics.ImageFormatType.Nv21, _quality));
        }
    }
    public void Start()
    {
        _listener.Start();
        EventBuss.SendCommand(BussCommand.START_CAMERA_FRONT);
        EventBuss.SendCommand(BussCommand.START_CAMERA_BACK);
        _ = Task.Run(ListenLoop);
    }

    public void Stop()
    {
        EventBuss.SendCommand(BussCommand.STOP_CAMERA_BACK);
        EventBuss.SendCommand(BussCommand.STOP_CAMERA_FRONT);
        _listener.Stop();
    }

    private async Task ListenLoop()
    {
        while (_listener.IsListening)
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleClient(ctx));
            }
            catch
            {
                // Listener was stopped or failed
            }
        }
    }

    private void HandleClient(HttpListenerContext context)
    {
        var response = context.Response;
        response.ContentType = "multipart/x-mixed-replace; boundary=--frame";
        response.StatusCode = 200;
        response.SendChunked = true;
        if (context.Request.Url.ToString().Contains("Back"))
        {
            _clientsBack.TryAdd(response, Guid.NewGuid().ToString());
        }
        else
        {
            _clientsFront.TryAdd(response, Guid.NewGuid().ToString());
        }

        try
        {
            while (response.OutputStream.CanWrite)
            {
                Thread.Sleep(100); // Idle until PushFrame is called
            }
        }
        catch
        {
            // Client disconnected
        }
        finally
        {
            if (context.Request.Url.ToString().Contains("Back"))
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

    public void PushBackFrame(byte[] jpegBytes, bool front = false)
    {
        var header = Encoding.ASCII.GetBytes(
            "\r\n--frame\r\nContent-Type: image/jpeg\r\nContent-Length: " + jpegBytes.Length + "\r\n\r\n");

        lock (_lockBack)
        {
            foreach (var client in _clientsBack.Keys.ToList())
            {
                try
                {
                    client.OutputStream.Write(header, 0, header.Length);
                    client.OutputStream.Write(jpegBytes, 0, jpegBytes.Length);
                    client.OutputStream.Flush();
                }
                catch
                {
                    _clientsBack.TryRemove(client, out var _);
                    try { client.OutputStream.Close(); client.Close(); } catch { }
                }
            }
        }
    }
    public void PushFrontFrame(byte[] jpegBytes, bool front = false)
    {
        var header = Encoding.ASCII.GetBytes(
            "\r\n--frame\r\nContent-Type: image/jpeg\r\nContent-Length: " + jpegBytes.Length + "\r\n\r\n");

        lock (_lockFront)
        {
            foreach (var client in _clientsFront.Keys.ToList())
            {
                try
                {
                    client.OutputStream.Write(header, 0, header.Length);
                    client.OutputStream.Write(jpegBytes, 0, jpegBytes.Length);
                    client.OutputStream.Flush();
                }
                catch
                {
                    _clientsFront.TryRemove(client, out var _);
                    try { client.OutputStream.Close(); client.Close(); } catch { }
                }
            }
        }
    }
}
