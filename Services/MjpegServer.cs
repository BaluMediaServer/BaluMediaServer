using System.Buffers;
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

/// <summary>
/// HTTP server for MJPEG streaming, providing web browser-compatible video streams.
/// Supports both front and back camera streams with optional HTTPS and Basic authentication.
/// </summary>
public class MjpegServer : IDisposable
{
    private readonly HttpListener _listener;
    private Task? _thread, _watchdog, _backEncoderTask, _frontEncoderTask;
    private DateTime _lastFrame = DateTime.UtcNow;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<HttpListenerResponse, string> _clientsFront = new(), _clientsBack = new();
    private readonly ConcurrentDictionary<string, DateTime> _clientLastFrameTime = new();
    private readonly ConcurrentDictionary<string, int> _clientFrameCount = new(); // Track frames for batch flushing
    private int _quality = 80;
    private int _port;
    private string _bindAddress;
    private bool _authEnabled = false;
    private volatile bool _streamStarted = true;  // volatile for thread-safe reads
    private readonly object _streamLock = new();
    private Dictionary<string, string> _users = new();
    private bool _useHttps = false;
    private string? _certificatePath;
    private string? _certificatePassword;

    // Performance optimization constants
    private const int ClientTimeoutSeconds = 30;
    private const int WriteTimeoutMs = 5000;        // Increased from 2000ms for slow networks
    private const int WatchdogTimeoutSeconds = 10;  // Increased from 5s for better tolerance
    private const int FlushEveryNFrames = 5;        // Batch flushing for smoother delivery
    private const int FrameQueueCapacity = 10;      // Larger buffer for smoother video

    // Frame queues for background encoding (larger buffer = smoother video, more delay)
    private readonly Channel<FrameEventArgs> _backFrameQueue = Channel.CreateBounded<FrameEventArgs>(
        new BoundedChannelOptions(FrameQueueCapacity) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Channel<FrameEventArgs> _frontFrameQueue = Channel.CreateBounded<FrameEventArgs>(
        new BoundedChannelOptions(FrameQueueCapacity) { FullMode = BoundedChannelFullMode.DropOldest });

    // Pre-computed boundary bytes to avoid repeated allocations
    private static readonly byte[] BoundaryBytes = Encoding.ASCII.GetBytes("\r\n--frame\r\n");
    private static readonly byte[] ContentTypeBytes = Encoding.ASCII.GetBytes("Content-Type: image/jpeg\r\n");
    private static readonly byte[] ContentLengthPrefix = Encoding.ASCII.GetBytes("Content-Length: ");
    private static readonly byte[] HeaderEnd = Encoding.ASCII.GetBytes("\r\n\r\n");

    /// <summary>
    /// Initializes a new instance of the <see cref="MjpegServer"/> class.
    /// </summary>
    /// <param name="port">The HTTP server port. Default is 8089.</param>
    /// <param name="quality">The JPEG compression quality (1-100). Default is 30.</param>
    /// <param name="bindAddress">The address to bind to ("*" for all interfaces). Default is "*".</param>
    /// <param name="authEnabled">Whether to enable Basic HTTP authentication. Default is false.</param>
    /// <param name="users">Dictionary of username/password pairs for authentication.</param>
    /// <param name="useHttps">Whether to use HTTPS instead of HTTP. Default is false.</param>
    /// <param name="certificatePath">Path to the SSL certificate file for HTTPS.</param>
    /// <param name="certificatePassword">Password for the SSL certificate.</param>
    public MjpegServer(int port = 8089, int quality = 30, string bindAddress = "*",
        bool authEnabled = false, Dictionary<string, string>? users = null,
        bool useHttps = false, string? certificatePath = null, string? certificatePassword = null)
    {
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

        // Start background encoder tasks (don't block camera callbacks)
        _backEncoderTask = Task.Run(BackEncoderLoopAsync, _cts.Token);
        _frontEncoderTask = Task.Run(FrontEncoderLoopAsync, _cts.Token);
    }
    /// <summary>
    /// Gets a value indicating whether the server is currently streaming.
    /// </summary>
    /// <returns><c>true</c> if streaming is active; otherwise, <c>false</c>.</returns>
    public bool IsStreaming() => _streamStarted;

    /// <summary>
    /// Releases all resources used by the MJPEG server.
    /// </summary>
    public void Dispose()
    {
        Log.Warn("MJPEG SERVER", $"Dispose() called - stack trace: {Environment.StackTrace}");
        _cts?.Cancel();
        Server.OnNewBackFrame -= OnBackFrameAvailable;
        Server.OnNewFrontFrame -= OnFrontFrameAvailable;

        // Complete frame queues to stop encoder tasks
        _backFrameQueue.Writer.TryComplete();
        _frontFrameQueue.Writer.TryComplete();

        //EventBuss.Command -= OnCommandSend;
        _listener?.Close();

        try
        {
            _thread?.Dispose();
            _watchdog?.Dispose();
            _backEncoderTask?.Dispose();
            _frontEncoderTask?.Dispose();
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
        int statusCounter = 0;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                // Log status every 5 seconds for debugging (visible in release mode)
                statusCounter++;
                if (statusCounter >= 5)
                {
                    statusCounter = 0;
                    Log.Info("MJPEG SERVER", $"Watchdog status: IsListening={_listener.IsListening}, StreamStarted={_streamStarted}, Clients={ClientCount} (Back={BackClientCount}, Front={FrontClientCount})");
                }

                // NOTE: Camera restart disabled for continuous streaming to prevent interruptions
                // Cameras will continue running even if frames are temporarily delayed
                // This prevents stream cuts and ensures fluid streaming

                if (_streamStarted && _listener.IsListening && ClientCount > 0)
                {
                    var timeSinceLastFrame = (DateTime.UtcNow - _lastFrame).TotalSeconds;
                    if (timeSinceLastFrame > WatchdogTimeoutSeconds)
                    {
                        Log.Info("MJPEG SERVER", $"Watchdog: No frames for {timeSinceLastFrame:F0}s (continuous mode - no restart)");
                    }
                }
                await Task.Delay(1000, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                Log.Warn("MJPEG SERVER", "Watchdog cancelled");
                break;
            }
        }
        Log.Warn("MJPEG SERVER", "Watchdog exited");
    }
    private void OnBackFrameAvailable(object? sender, FrameEventArgs arg)
    {
        if (arg != null && arg.Data != null && arg.Data.Length > 0)
        {
            // No frame rate limiting - queue all frames for smoother video
            // The bounded channel will drop oldest if overwhelmed
            _backFrameQueue.Writer.TryWrite(arg);
        }
    }

    private void OnFrontFrameAvailable(object? sender, FrameEventArgs arg)
    {
        if (arg != null && arg.Data != null && arg.Data.Length > 0)
        {
            // No frame rate limiting - queue all frames for smoother video
            // The bounded channel will drop oldest if overwhelmed
            _frontFrameQueue.Writer.TryWrite(arg);
        }
    }

    /// <summary>
    /// Background task that encodes and pushes back camera frames.
    /// </summary>
    private async Task BackEncoderLoopAsync()
    {
        await foreach (var arg in _backFrameQueue.Reader.ReadAllAsync(_cts.Token))
        {
            try
            {
                if (_clientsBack.IsEmpty) continue; // Skip encoding if no clients

                var jpegData = Server.EncodeToJpeg(arg.Data, arg.Width, arg.Height,
                    Android.Graphics.ImageFormatType.Nv21, _quality);
                await PushBackFrameAsync(jpegData).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (System.Exception ex)
            {
                Log.Debug("MJPEG SERVER", $"Back encoder error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Background task that encodes and pushes front camera frames.
    /// </summary>
    private async Task FrontEncoderLoopAsync()
    {
        await foreach (var arg in _frontFrameQueue.Reader.ReadAllAsync(_cts.Token))
        {
            try
            {
                if (_clientsFront.IsEmpty) continue; // Skip encoding if no clients

                var jpegData = Server.EncodeToJpeg(arg.Data, arg.Width, arg.Height,
                    Android.Graphics.ImageFormatType.Nv21, _quality);
                await PushFrontFrameAsync(jpegData).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (System.Exception ex)
            {
                Log.Debug("MJPEG SERVER", $"Front encoder error: {ex.Message}");
            }
        }
    }
    /// <summary>
    /// Starts the MJPEG HTTP server and optionally begins camera streaming.
    /// </summary>
    /// <param name="StartWithoutStream">If true, starts the server without automatically starting cameras.
    /// Cameras will start on-demand when the first client connects.</param>
    public void Start(bool StartWithoutStream = false)
    {
        try
        {
            _listener.Start();
            Log.Info("MJPEG SERVER", $"STARTING SERVER - StartWithoutStream={StartWithoutStream}");
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
            Log.Info("MJPEG SERVER", $"STARTED SERVER - IsListening={_listener.IsListening}");
        }
        catch (System.Exception ex)
        {
            Log.Warn("MJPEG SERVER", $"Start() exception: {ex.Message}");
        }

    }

    /// <summary>
    /// Stops the MJPEG server and disconnects all clients.
    /// </summary>
    public void Stop()
    {
        Log.Warn("MJPEG SERVER", $"Stop() called - stack trace: {Environment.StackTrace}");
        _clientsBack.Clear();
        _clientsFront.Clear();
        EventBuss.SendCommand(BussCommand.STOP_CAMERA_BACK);
        EventBuss.SendCommand(BussCommand.STOP_CAMERA_FRONT);
        _streamStarted = false;
        _listener.Stop();
    }

    private async Task ListenLoop()
    {
        Log.Info("MJPEG SERVER", "ListenLoop started");
        while (_listener.IsListening && !_cts.IsCancellationRequested)
        {
            try
            {
                Log.Debug("MJPEG SERVER", "WAITING CLIENT");
                var ctx = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleClient(ctx), _cts.Token);
            }
            catch (System.Exception ex)
            {
                // Listener was stopped or failed - log it in release mode
                Log.Warn("MJPEG SERVER", $"ListenLoop exception: {ex.GetType().Name}: {ex.Message}");
            }
        }
        // Log why we exited the loop
        Log.Warn("MJPEG SERVER", $"ListenLoop EXITED - IsListening={_listener.IsListening}, IsCancelled={_cts.IsCancellationRequested}");
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

            // Cleanup frame time and frame count tracking
            _clientLastFrameTime.TryRemove(clientId, out _);
            _clientFrameCount.TryRemove(clientId, out _);

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
    private async Task WriteDataAsync(HttpListenerResponse client, byte[] jpegBytes, bool isBackCamera, CancellationToken cancellationToken)
    {
        string? clientId = null;
        byte[]? combinedBuffer = null;

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

            // Build complete frame in single buffer for efficient single write
            var contentLengthStr = jpegBytes.Length.ToString();
            var headerSize = BoundaryBytes.Length + ContentTypeBytes.Length +
                            ContentLengthPrefix.Length + contentLengthStr.Length + HeaderEnd.Length;
            var totalSize = headerSize + jpegBytes.Length;

            // Rent buffer from pool to avoid allocation
            combinedBuffer = ArrayPool<byte>.Shared.Rent(totalSize);

            // Copy all parts into combined buffer
            var offset = 0;
            Buffer.BlockCopy(BoundaryBytes, 0, combinedBuffer, offset, BoundaryBytes.Length);
            offset += BoundaryBytes.Length;
            Buffer.BlockCopy(ContentTypeBytes, 0, combinedBuffer, offset, ContentTypeBytes.Length);
            offset += ContentTypeBytes.Length;
            Buffer.BlockCopy(ContentLengthPrefix, 0, combinedBuffer, offset, ContentLengthPrefix.Length);
            offset += ContentLengthPrefix.Length;
            var contentLengthBytes = Encoding.ASCII.GetBytes(contentLengthStr);
            Buffer.BlockCopy(contentLengthBytes, 0, combinedBuffer, offset, contentLengthBytes.Length);
            offset += contentLengthBytes.Length;
            Buffer.BlockCopy(HeaderEnd, 0, combinedBuffer, offset, HeaderEnd.Length);
            offset += HeaderEnd.Length;
            Buffer.BlockCopy(jpegBytes, 0, combinedBuffer, offset, jpegBytes.Length);

            // Single write for entire frame (more efficient than multiple small writes)
            await client.OutputStream.WriteAsync(combinedBuffer, 0, totalSize, cancellationToken).ConfigureAwait(false);

            // Batch flushing: only flush every N frames to reduce syscalls
            var frameCount = _clientFrameCount.AddOrUpdate(clientId, 1, (_, count) => count + 1);
            if (frameCount >= FlushEveryNFrames)
            {
                await client.OutputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                _clientFrameCount[clientId] = 0;
            }
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

            // Cleanup frame time and frame count tracking
            if (clientId != null)
            {
                _clientLastFrameTime.TryRemove(clientId, out _);
                _clientFrameCount.TryRemove(clientId, out _);
            }

            try { client.OutputStream.Close(); client.Close(); } catch { }
        }
        finally
        {
            // Return buffer to pool
            if (combinedBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(combinedBuffer);
            }
        }
    }

    /// <summary>
    /// Pushes a JPEG frame to all connected back camera clients.
    /// </summary>
    /// <param name="jpegBytes">The JPEG-encoded frame data.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task PushBackFrameAsync(byte[] jpegBytes)
    {
        if (_clientsBack.IsEmpty)
            return;

        _lastFrame = DateTime.UtcNow;

        // Fire-and-forget per client to avoid blocking on slow clients
        var tasks = _clientsBack.Keys.Select(client =>
            WriteDataAsyncWithTimeout(client, jpegBytes, isBackCamera: true));

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
    /// Pushes a JPEG frame to all connected front camera clients.
    /// </summary>
    /// <param name="jpegBytes">The JPEG-encoded frame data.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task PushFrontFrameAsync(byte[] jpegBytes)
    {
        if (_clientsFront.IsEmpty)
            return;

        _lastFrame = DateTime.UtcNow;

        // Fire-and-forget per client to avoid blocking on slow clients
        var tasks = _clientsFront.Keys.Select(client =>
            WriteDataAsyncWithTimeout(client, jpegBytes, isBackCamera: false));

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
    private async Task WriteDataAsyncWithTimeout(HttpListenerResponse client, byte[] jpegBytes, bool isBackCamera)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        cts.CancelAfter(WriteTimeoutMs);

        try
        {
            // FIX: Pass the linked cancellation token to WriteDataAsync
            await WriteDataAsync(client, jpegBytes, isBackCamera, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Timeout - remove slow client
            string? clientId = null;
            if (isBackCamera)
            {
                if (_clientsBack.TryRemove(client, out clientId))
                {
                    _clientLastFrameTime.TryRemove(clientId, out _);
                    _clientFrameCount.TryRemove(clientId, out _);
                }
            }
            else
            {
                if (_clientsFront.TryRemove(client, out clientId))
                {
                    _clientLastFrameTime.TryRemove(clientId, out _);
                    _clientFrameCount.TryRemove(clientId, out _);
                }
            }

            try { client.OutputStream.Close(); client.Close(); } catch { }
            Log.Debug("MJPEG SERVER", $"Removed slow client due to {WriteTimeoutMs}ms timeout");
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
