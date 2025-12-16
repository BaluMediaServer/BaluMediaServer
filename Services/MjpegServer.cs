using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography.X509Certificates;
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
    private readonly ConcurrentDictionary<string, DateTime> _clientLastFrameTime = new();
    private int _quality = 80;
    private int _port;
    private string _bindAddress;
    private bool _authEnabled = false;
    private volatile bool _streamStarted = true;  // volatile for thread-safe reads
    private readonly object _streamLock = new();
    private Dictionary<string, string> _users = new();
    private const int ClientTimeoutSeconds = 30;
    private bool _useHttps = false;
    private string? _certificatePath;
    private string? _certificatePassword;

    public MjpegServer(int port = 8089, int quality = 30, string bindAddress = "*",
        bool authEnabled = false, Dictionary<string, string>? users = null,
        bool useHttps = false, string? certificatePath = null, string? certificatePassword = null)
    {
        _frameIntervalMs = 1000.0 / 30;
        _port = port;
        _bindAddress = bindAddress;
        _authEnabled = authEnabled;
        _users = users ?? new Dictionary<string, string>();
        _useHttps = useHttps;
        _certificatePath = certificatePath;
        _certificatePassword = certificatePassword;
        _listener = new();

        // On Android, HttpListener works best with "*" or "+" for all interfaces
        // "0.0.0.0" doesn't work properly on Android
        // Use "*" for all interfaces (doesn't require admin on Android)
        string prefix = _bindAddress switch
        {
            "0.0.0.0" => "*",      // Convert to wildcard for Android compatibility
            "127.0.0.1" => "localhost",  // Localhost only
            _ => _bindAddress
        };

        string protocol = _useHttps ? "https" : "http";
        _listener.Prefixes.Add($"{protocol}://{prefix}:{_port}/Back/");
        _listener.Prefixes.Add($"{protocol}://{prefix}:{_port}/Front/");

        _quality = quality;
        Server.OnNewBackFrame += OnBackFrameAvailable;
        Server.OnNewFrontFrame += OnFrontFrameAvailable;
        _watchdog = Task.Run(Watchdog, _cts.Token);
    }
    public bool IsStreaming() => _streamStarted;
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
            try
            {
                // Only restart cameras if stream was started and we have clients
                // This prevents the watchdog from starting cameras when using StartWithoutStream mode
                if (_streamStarted && _listener.IsListening && ClientCount > 0)
                {
                    if ((DateTime.UtcNow - _lastFrame).TotalSeconds > 5)
                    {
                        Log.Debug("MJPEG SERVER", "Watchdog: No frames received for 5s, restarting cameras");
                        EventBuss.SendCommand(BussCommand.START_CAMERA_FRONT);
                        EventBuss.SendCommand(BussCommand.START_CAMERA_BACK);
                        await Task.Delay(5000, _cts.Token);
                        continue;
                    }
                }
                await Task.Delay(1000, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
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
    public void Start(bool StartWithoutStream = false)
    {
        try
        {
            _listener.Start();
            Log.Debug("MJPEG SERVER", "STARTING SERVER");
            if (!StartWithoutStream)
            {
                EventBuss.SendCommand(BussCommand.START_CAMERA_FRONT);
                EventBuss.SendCommand(BussCommand.START_CAMERA_BACK);
                _streamStarted = true;
            }
            else
            {
                _streamStarted = false;
            }
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
        _streamStarted = false;
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
        // Start cameras on-demand when first client connects (thread-safe)
        if (!_streamStarted)
        {
            lock (_streamLock)
            {
                if (!_streamStarted)  // Double-check after acquiring lock
                {
                    Log.Debug("MJPEG SERVER", "First client connected, starting cameras on-demand");
                    EventBuss.SendCommand(BussCommand.START_CAMERA_FRONT);
                    EventBuss.SendCommand(BussCommand.START_CAMERA_BACK);
                    _streamStarted = true;
                }
            }
        }
        var remoteEndpoint = context.Request.RemoteEndPoint?.ToString() ?? "unknown";
        Log.Debug("MJPEG SERVER", $"Client connected from {remoteEndpoint}");

        var response = context.Response;
        var uri = context.Request.Url?.ToString() ?? string.Empty;

        // Authentication check
        if (_authEnabled && _users.Count > 0)
        {
            if (!ValidateAuthentication(context))
            {
                Log.Debug("MJPEG SERVER", $"Authentication failed for {remoteEndpoint}");
                response.StatusCode = 401;
                response.Headers.Add("WWW-Authenticate", "Basic realm=\"MJPEG Stream\"");
                response.Close();
                return;
            }
        }

        if (string.IsNullOrEmpty(uri))
        {
            response.StatusCode = 400;
            response.Close();
            return;
        }

        response.ContentType = "multipart/x-mixed-replace; boundary=--frame";
        response.StatusCode = 200;
        response.SendChunked = true;

        // Add CORS headers for external access
        response.Headers.Add("Access-Control-Allow-Origin", "*");
        response.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");
        response.Headers.Add("Pragma", "no-cache");
        response.Headers.Add("Expires", "0");

        var clientId = Guid.NewGuid().ToString();
        var isBackCamera = uri.Contains("Back");

        if (isBackCamera)
        {
            _clientsBack.TryAdd(response, clientId);
        }
        else
        {
            _clientsFront.TryAdd(response, clientId);
        }

        _clientLastFrameTime[clientId] = DateTime.UtcNow;

        Log.Debug("MJPEG SERVER", $"Serving {(isBackCamera ? "Back" : "Front")} camera to {remoteEndpoint}");

        try
        {
            // Keep connection alive with proper timeout checking
            while (!_cts.IsCancellationRequested)
            {
                // Check if stream is still writable
                if (!response.OutputStream.CanWrite)
                    break;

                // Check for client timeout (no frames sent recently)
                if (_clientLastFrameTime.TryGetValue(clientId, out var lastTime))
                {
                    var timeSinceLastFrame = (DateTime.UtcNow - lastTime).TotalSeconds;
                    if (timeSinceLastFrame > ClientTimeoutSeconds)
                    {
                        Log.Debug("MJPEG SERVER", $"Client {remoteEndpoint} timed out after {timeSinceLastFrame:F0}s");
                        break;
                    }
                }

                await Task.Delay(500, _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Server is stopping
        }
        catch (System.Exception ex)
        {
            Log.Debug("MJPEG SERVER", $"Client error ({remoteEndpoint}): {ex.Message}");
        }
        finally
        {
            // Cleanup client from appropriate dictionary
            if (isBackCamera)
            {
                _clientsBack.TryRemove(response, out _);
            }
            else
            {
                _clientsFront.TryRemove(response, out _);
            }

            // Cleanup frame time tracking
            _clientLastFrameTime.TryRemove(clientId, out _);

            try
            {
                response.OutputStream.Close();
                response.Close();
            }
            catch { }

            Log.Debug("MJPEG SERVER", $"Client {remoteEndpoint} disconnected");
        }
    }

    /// <summary>
    /// Validates Basic authentication from HTTP request
    /// </summary>
    private bool ValidateAuthentication(HttpListenerContext context)
    {
        var authHeader = context.Request.Headers["Authorization"];
        if (string.IsNullOrEmpty(authHeader))
            return false;

        if (!authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var base64Credentials = authHeader.Substring(6);
            var credentials = Encoding.UTF8.GetString(Convert.FromBase64String(base64Credentials));
            var parts = credentials.Split(':');

            if (parts.Length != 2)
                return false;

            var username = parts[0];
            var password = parts[1];

            return _users.TryGetValue(username, out var storedPassword) && storedPassword == password;
        }
        catch
        {
            return false;
        }
    }
    private async Task WriteDataAsync(HttpListenerResponse client, byte[] jpegBytes, bool isBackCamera)
    {
        string? clientId = null;

        try
        {
            // Get client ID from the appropriate dictionary
            if (isBackCamera)
            {
                _clientsBack.TryGetValue(client, out clientId);
            }
            else
            {
                _clientsFront.TryGetValue(client, out clientId);
            }

            if (clientId == null)
                return;

            // Update last frame time for this client (used for timeout tracking)
            _clientLastFrameTime[clientId] = DateTime.UtcNow;

            // Write MJPEG frame
            var header = Encoding.ASCII.GetBytes(
                $"\r\n--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {jpegBytes.Length}\r\n\r\n");

            await client.OutputStream.WriteAsync(header, 0, header.Length, _cts.Token).ConfigureAwait(false);
            await client.OutputStream.WriteAsync(jpegBytes, 0, jpegBytes.Length, _cts.Token).ConfigureAwait(false);
            await client.OutputStream.FlushAsync(_cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // Remove client from appropriate dictionary on error
            if (isBackCamera)
            {
                _clientsBack.TryRemove(client, out _);
            }
            else
            {
                _clientsFront.TryRemove(client, out _);
            }

            // Cleanup frame time tracking
            if (clientId != null)
            {
                _clientLastFrameTime.TryRemove(clientId, out _);
            }

            try { client.OutputStream.Close(); client.Close(); } catch { }
        }
    }

    public async Task PushBackFrameAsync(byte[] jpegBytes)
    {
        if (_clientsBack.IsEmpty)
            return;

        _lastFrame = DateTime.UtcNow;

        // Fire-and-forget per client to avoid blocking on slow clients
        var tasks = _clientsBack.Keys.Select(client =>
            WriteDataAsyncWithTimeout(client, jpegBytes, isBackCamera: true, timeoutMs: 2000));

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            // Individual client errors are handled in WriteDataAsync
        }
    }

    public async Task PushFrontFrameAsync(byte[] jpegBytes)
    {
        if (_clientsFront.IsEmpty)
            return;

        _lastFrame = DateTime.UtcNow;

        // Fire-and-forget per client to avoid blocking on slow clients
        var tasks = _clientsFront.Keys.Select(client =>
            WriteDataAsyncWithTimeout(client, jpegBytes, isBackCamera: false, timeoutMs: 2000));

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            // Individual client errors are handled in WriteDataAsync
        }
    }

    /// <summary>
    /// Writes frame data with a timeout to prevent slow clients from blocking others
    /// </summary>
    private async Task WriteDataAsyncWithTimeout(HttpListenerResponse client, byte[] jpegBytes, bool isBackCamera, int timeoutMs)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        cts.CancelAfter(timeoutMs);

        try
        {
            await WriteDataAsync(client, jpegBytes, isBackCamera).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Timeout - remove slow client
            if (isBackCamera)
            {
                if (_clientsBack.TryRemove(client, out var clientId))
                {
                    _clientLastFrameTime.TryRemove(clientId, out _);
                }
            }
            else
            {
                if (_clientsFront.TryRemove(client, out var clientId))
                {
                    _clientLastFrameTime.TryRemove(clientId, out _);
                }
            }

            try { client.OutputStream.Close(); client.Close(); } catch { }
            Log.Debug("MJPEG SERVER", "Removed slow client due to timeout");
        }
    }

    /// <summary>
    /// Gets the number of connected clients
    /// </summary>
    public int ClientCount => _clientsBack.Count + _clientsFront.Count;

    /// <summary>
    /// Gets the number of back camera clients
    /// </summary>
    public int BackClientCount => _clientsBack.Count;

    /// <summary>
    /// Gets the number of front camera clients
    /// </summary>
    public int FrontClientCount => _clientsFront.Count;
}
