using System.Net;
using System.Text;
using BaluMediaServer.Models;
using BaluMediaServer.Repositories;
namespace BaluMediaServer.Services;
public class MjpegServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly List<HttpListenerResponse> _clientsFront = new(), _clientsBack = new();
    private readonly object _lock = new();
    public int Port { get; }

    public MjpegServer(int port = 8089)
    {
        Port = port;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{Port}/Back/");
        _listener.Prefixes.Add($"http://localhost:{Port}/Front/");
        _listener.Prefixes.Add($"http://+:{Port}/Back/");
        _listener.Prefixes.Add($"http://+:{Port}/Front/");
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
            this.PushFrame(Server.EncodeToJpeg(arg.Data, arg.Width, arg.Height, Android.Graphics.ImageFormatType.Nv21));
        }
    }
    private void OnFrontFrameAvailable(object? sender, FrameEventArgs arg)
    {
        if (arg != null && arg.Data != null && arg.Data.Length > 0)
        {
            this.PushFrame(Server.EncodeToJpeg(arg.Data, arg.Width, arg.Height, Android.Graphics.ImageFormatType.Nv21));
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

        lock (_lock)
        {
            if (context.Request.Url.ToString().Contains("Back"))
            {
                _clientsBack.Add(response);
            }
            else
            {
                _clientsFront.Add(response);
            }
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
                _clientsBack.Remove(response);
            }
            else
            {
                _clientsFront.Remove(response);
            }

            response.OutputStream.Close();
            response.Close();
        }
    }

    public void PushFrame(byte[] jpegBytes, bool front = false)
    {
        var header = Encoding.ASCII.GetBytes(
            "\r\n--frame\r\nContent-Type: image/jpeg\r\nContent-Length: " + jpegBytes.Length + "\r\n\r\n");

        lock (_lock)
        {
            if (front)
                foreach (var client in _clientsFront.ToList())
                {
                    try
                    {
                        client.OutputStream.Write(header, 0, header.Length);
                        client.OutputStream.Write(jpegBytes, 0, jpegBytes.Length);
                        client.OutputStream.Flush();
                    }
                    catch
                    {
                        _clientsFront.Remove(client);
                        try { client.OutputStream.Close(); client.Close(); } catch { }
                    }
                }
            else
                foreach (var client in _clientsBack.ToList())
                {
                    try
                    {
                        client.OutputStream.Write(header, 0, header.Length);
                        client.OutputStream.Write(jpegBytes, 0, jpegBytes.Length);
                        client.OutputStream.Flush();
                    }
                    catch
                    {
                        _clientsBack.Remove(client);
                        try { client.OutputStream.Close(); client.Close(); } catch { }
                    }
                }
        }
    }
}
