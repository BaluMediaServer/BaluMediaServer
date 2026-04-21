using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
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
///
/// v1.5.14: Fixed client reconnection bug - semaphore now always releases at least once per frame
/// to prevent new clients from blocking forever. Added _streamStarted reset on last client disconnect
/// to ensure cameras restart on-demand when clients reconnect.
///
/// v1.5.13: Improved smoothness with SemaphoreSlim signaling and per-client frame rate limiting.
/// Inspired by MauiJpegServer architecture for more efficient frame distribution.
/// </summary>
public class MjpegServer : IDisposable
{
    private readonly HttpListener _listener;
    private Task? _thread, _watchdog, _backEncoderTask, _frontEncoderTask;
    private DateTime _lastFrame = DateTime.UtcNow;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<HttpListenerResponse, ClientInfo> _clientsFront = new(), _clientsBack = new();
    private int _quality = 80;
    private int _port;
    private string _bindAddress;
    private bool _authEnabled = false;
    private volatile bool _streamStarted = true;  // volatile for thread-safe reads
    private volatile bool _disposed;              // Prevents JNI access after disposal
    private readonly object _streamLock = new();
    private Dictionary<string, string> _users = new();
    private bool _useHttps = false;
    private string? _certificatePath;
    private string? _certificatePassword;

    // Performance optimization constants
    private const int ClientTimeoutSeconds = 30;    // Timeout if no frames sent for this long
    private const int ClientMaxIdleSeconds = 60;    // Max time waiting for first frame
    private const int WriteTimeoutMs = 5000;        // Increased from 2000ms for slow networks
    private const int WatchdogTimeoutSeconds = 10;  // Increased from 5s for better tolerance
    private const int FlushEveryNFrames = 3;        // Reduced for smoother delivery
    private const int FrameQueueCapacity = 10;      // Larger buffer for smoother video
    private const int DefaultMaxFrameRate = 30;     // Default max FPS per client

    // Frame queues for background encoding (larger buffer = smoother video, more delay)
    private readonly Channel<FrameEventArgs> _backFrameQueue = Channel.CreateBounded<FrameEventArgs>(
        new BoundedChannelOptions(FrameQueueCapacity) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Channel<FrameEventArgs> _frontFrameQueue = Channel.CreateBounded<FrameEventArgs>(
        new BoundedChannelOptions(FrameQueueCapacity) { FullMode = BoundedChannelFullMode.DropOldest });

    // SemaphoreSlim-based frame signaling for efficient client notification (from MauiJpegServer)
    private readonly SemaphoreSlim _backFrameSemaphore = new(0);
    private readonly SemaphoreSlim _frontFrameSemaphore = new(0);

    // Latest encoded JPEG frames for client consumption
    private volatile byte[]? _latestBackJpeg;
    private volatile byte[]? _latestFrontJpeg;

    // Real-time FPS tracking (from MauiJpegServer)
    private readonly Stopwatch _backFpsStopwatch = Stopwatch.StartNew();
    private readonly Stopwatch _frontFpsStopwatch = Stopwatch.StartNew();
    private int _backFrameCount, _frontFrameCount;
    private double _backCurrentFps, _frontCurrentFps;
    private long _totalBackFrames, _totalFrontFrames;

    // Pre-computed boundary bytes to avoid repeated allocations
    private static readonly byte[] BoundaryBytes = Encoding.ASCII.GetBytes("\r\n--frame\r\n");
    private static readonly byte[] ContentTypeBytes = Encoding.ASCII.GetBytes("Content-Type: image/jpeg\r\n");
    private static readonly byte[] ContentLengthPrefix = Encoding.ASCII.GetBytes("Content-Length: ");
    private static readonly byte[] HeaderEnd = Encoding.ASCII.GetBytes("\r\n\r\n");

    /// <summary>
    /// Per-client information for tracking and frame rate limiting
    /// </summary>
    private class ClientInfo
    {
        public string ClientId { get; }
        public DateTime ConnectedAt { get; }
        public DateTime LastFrameTime { get; set; }
        public int FrameCount { get; set; }
        public long FramesSent { get; set; }
        public long BytesSent { get; set; }
        public TimeSpan MinFrameInterval { get; }
        public CancellationTokenSource Cts { get; }

        public ClientInfo(string clientId, int maxFrameRate)
        {
            ClientId = clientId;
            ConnectedAt = DateTime.UtcNow;
            LastFrameTime = DateTime.MinValue;
            MinFrameInterval = TimeSpan.FromMilliseconds(1000.0 / maxFrameRate);
            Cts = new CancellationTokenSource();
        }
    }

    private int _maxFrameRate = DefaultMaxFrameRate;

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
    /// <param name="maxFrameRate">Maximum frames per second per client. Default is 30.</param>
    public MjpegServer(int port = 8089, int quality = 30, string bindAddress = "*",
        bool authEnabled = false, Dictionary<string, string>? users = null,
        bool useHttps = false, string? certificatePath = null, string? certificatePassword = null,
        int maxFrameRate = DefaultMaxFrameRate)
    {
        _port = port;
        _bindAddress = bindAddress;
        _authEnabled = authEnabled;
        _users = users ?? new Dictionary<string, string>();
        _useHttps = useHttps;
        _certificatePath = certificatePath;
        _certificatePassword = certificatePassword;
        _maxFrameRate = maxFrameRate > 0 ? maxFrameRate : DefaultMaxFrameRate;
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
    /// Sets _disposed flag FIRST to stop encoder loops before any JNI cleanup.
    /// </summary>
    public void Dispose()
    {
        BaluLogger.Warn("MJPEG SERVER", $"Dispose() called");

        // Set disposed flag FIRST to stop encoder loops from accessing Java objects
        _disposed = true;

        _cts?.Cancel();
        Server.OnNewBackFrame -= OnBackFrameAvailable;
        Server.OnNewFrontFrame -= OnFrontFrameAvailable;

        // Cancel all client streaming tasks with null safety
        foreach (var client in _clientsBack.Values)
        {
            try { client?.Cts?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        foreach (var client in _clientsFront.Values)
        {
            try { client?.Cts?.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        // Complete frame queues to stop encoder tasks
        _backFrameQueue.Writer.TryComplete();
        _frontFrameQueue.Writer.TryComplete();

        // Wait for encoder tasks to complete before disposing - prevents JNI SIGSEGV
        try
        {
            var encoderTasks = new[] { _backEncoderTask, _frontEncoderTask };
            if (!Task.WaitAll(encoderTasks.Where(t => t != null).ToArray()!, TimeSpan.FromSeconds(5)))
            {
                BaluLogger.Warn("MJPEG SERVER", "Encoder tasks did not complete within 5s timeout");
            }
        }
        catch (System.Exception ex)
        {
            BaluLogger.Debug("MJPEG SERVER", $"Encoder tasks wait error: {ex.Message}");
        }

        //EventBuss.Command -= OnCommandSend;
        _listener?.Close();

        try { _thread?.Dispose(); }
        catch (System.Exception ex) { BaluLogger.Debug("MJPEG SERVER", $"Dispose _thread error: {ex.Message}"); }

        try { _watchdog?.Dispose(); }
        catch (System.Exception ex) { BaluLogger.Debug("MJPEG SERVER", $"Dispose _watchdog error: {ex.Message}"); }

        try { _backEncoderTask?.Dispose(); }
        catch (System.Exception ex) { BaluLogger.Debug("MJPEG SERVER", $"Dispose _backEncoderTask error: {ex.Message}"); }

        try { _frontEncoderTask?.Dispose(); }
        catch (System.Exception ex) { BaluLogger.Debug("MJPEG SERVER", $"Dispose _frontEncoderTask error: {ex.Message}"); }

        try { _backFrameSemaphore?.Dispose(); }
        catch (System.Exception ex) { BaluLogger.Debug("MJPEG SERVER", $"Dispose _backFrameSemaphore error: {ex.Message}"); }

        try { _frontFrameSemaphore?.Dispose(); }
        catch (System.Exception ex) { BaluLogger.Debug("MJPEG SERVER", $"Dispose _frontFrameSemaphore error: {ex.Message}"); }

        _cts?.Dispose();
        BaluLogger.Info("MJPEG SERVER", "Disposed successfully");
    }
    private void OnCommandSend(BussCommand command)
    {
        try
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
        catch (System.Exception ex)
        {
            BaluLogger.Error("MJPEG SERVER", $"OnCommandSend error: {ex.Message}");
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
                    BaluLogger.Info("MJPEG SERVER", $"Watchdog: Listening={_listener.IsListening}, Clients={ClientCount} (Back={BackClientCount}, Front={FrontClientCount}), FPS: Back={_backCurrentFps:F1}, Front={_frontCurrentFps:F1}, Total: Back={_totalBackFrames}, Front={_totalFrontFrames}");

                    // Periodic stale client cleanup
                    CleanupStaleClients();
                }

                // NOTE: Camera restart disabled for continuous streaming to prevent interruptions
                // Cameras will continue running even if frames are temporarily delayed
                // This prevents stream cuts and ensures fluid streaming

                if (_streamStarted && _listener.IsListening && ClientCount > 0)
                {
                    var timeSinceLastFrame = (DateTime.UtcNow - _lastFrame).TotalSeconds;
                    if (timeSinceLastFrame > WatchdogTimeoutSeconds)
                    {
                        BaluLogger.Info("MJPEG SERVER", $"Watchdog: No frames for {timeSinceLastFrame:F0}s (continuous mode - no restart)");
                    }
                }
                await Task.Delay(1000, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                BaluLogger.Warn("MJPEG SERVER", "Watchdog cancelled");
                break;
            }
        }
        BaluLogger.Warn("MJPEG SERVER", "Watchdog exited");
    }

    /// <summary>
    /// Cleans up stale clients that may have disconnected without proper cleanup.
    /// Called periodically by the watchdog to prevent infinite connections.
    /// </summary>
    private void CleanupStaleClients()
    {
        var now = DateTime.UtcNow;
        var staleClients = new List<(HttpListenerResponse response, ClientInfo info, bool isBack)>();

        // Check back camera clients
        foreach (var kvp in _clientsBack)
        {
            var info = kvp.Value;
            var connectionAge = (now - info.ConnectedAt).TotalSeconds;
            var timeSinceLastFrame = (now - info.LastFrameTime).TotalSeconds;

            // Stale if: received frames but none recently, OR never received frames and connected too long
            bool isStale = (info.FramesSent > 0 && timeSinceLastFrame > ClientTimeoutSeconds * 2) ||
                          (info.FramesSent == 0 && connectionAge > ClientMaxIdleSeconds * 2);

            if (isStale)
            {
                staleClients.Add((kvp.Key, info, true));
            }
        }

        // Check front camera clients
        foreach (var kvp in _clientsFront)
        {
            var info = kvp.Value;
            var connectionAge = (now - info.ConnectedAt).TotalSeconds;
            var timeSinceLastFrame = (now - info.LastFrameTime).TotalSeconds;

            bool isStale = (info.FramesSent > 0 && timeSinceLastFrame > ClientTimeoutSeconds * 2) ||
                          (info.FramesSent == 0 && connectionAge > ClientMaxIdleSeconds * 2);

            if (isStale)
            {
                staleClients.Add((kvp.Key, info, false));
            }
        }

        // Remove stale clients
        foreach (var (response, info, isBack) in staleClients)
        {
            BaluLogger.Warn("MJPEG SERVER", $"Watchdog removing stale client {info.ClientId} - FramesSent={info.FramesSent}, Age={(now - info.ConnectedAt).TotalSeconds:F0}s");

            // Cancel client's streaming task
            try { info.Cts.Cancel(); } catch { }

            // Remove from dictionary
            if (isBack)
                _clientsBack.TryRemove(response, out _);
            else
                _clientsFront.TryRemove(response, out _);

            // Close connection
            try
            {
                response.OutputStream.Close();
                response.Close();
            }
            catch { }

            try { info.Cts.Dispose(); } catch { }
        }

        if (staleClients.Count > 0)
        {
            BaluLogger.Info("MJPEG SERVER", $"Watchdog cleaned up {staleClients.Count} stale client(s)");
        }
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
    /// Background task that encodes back camera frames and signals waiting clients.
    /// Uses SemaphoreSlim for efficient client notification (inspired by MauiJpegServer).
    /// Always releases semaphore at least once per frame to prevent race condition where
    /// new clients connecting between frames would block forever (v1.5.14 fix).
    /// Checks _disposed flag before JNI calls to prevent SIGSEGV.
    /// </summary>
    private async Task BackEncoderLoopAsync()
    {
        await foreach (var arg in _backFrameQueue.Reader.ReadAllAsync(_cts.Token))
        {
            // Check disposed flag BEFORE any JNI calls to prevent SIGSEGV
            if (_disposed)
            {
                BaluLogger.Debug("MJPEG SERVER", "Back encoder loop exiting - server disposed");
                break;
            }

            try
            {
                // Skip encoding entirely when no clients are connected
                if (_clientsBack.Count == 0)
                    continue;

                // Validate frame data before JNI encoding
                if (arg.Data == null || arg.Data.Length == 0 || arg.Width <= 0 || arg.Height <= 0)
                {
                    continue;
                }

                var jpegData = Server.EncodeToJpeg(arg.Data, arg.Width, arg.Height,
                    Android.Graphics.ImageFormatType.Nv21, _quality);

                if (_disposed) break; // Check again after encoding

                // Store latest frame for client consumption
                _latestBackJpeg = jpegData;
                _lastFrame = DateTime.UtcNow;

                // Update FPS tracking
                _totalBackFrames++;
                _backFrameCount++;
                if (_backFpsStopwatch.ElapsedMilliseconds >= 1000)
                {
                    _backCurrentFps = _backFrameCount / (_backFpsStopwatch.ElapsedMilliseconds / 1000.0);
                    _backFrameCount = 0;
                    _backFpsStopwatch.Restart();
                }

                // Signal all waiting clients that a new frame is available
                var clientCount = _clientsBack.Count;
                for (int i = 0; i < clientCount; i++)
                {
                    try { _backFrameSemaphore.Release(); }
                    catch (SemaphoreFullException) { break; } // Semaphore full, all clients notified
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (System.Exception ex)
            {
                if (!_disposed) BaluLogger.Debug("MJPEG SERVER", $"Back encoder error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Background task that encodes front camera frames and signals waiting clients.
    /// Uses SemaphoreSlim for efficient client notification (inspired by MauiJpegServer).
    /// Always releases semaphore at least once per frame to prevent race condition where
    /// new clients connecting between frames would block forever (v1.5.14 fix).
    /// Checks _disposed flag before JNI calls to prevent SIGSEGV.
    /// </summary>
    private async Task FrontEncoderLoopAsync()
    {
        await foreach (var arg in _frontFrameQueue.Reader.ReadAllAsync(_cts.Token))
        {
            // Check disposed flag BEFORE any JNI calls to prevent SIGSEGV
            if (_disposed)
            {
                BaluLogger.Debug("MJPEG SERVER", "Front encoder loop exiting - server disposed");
                break;
            }

            try
            {
                // Skip encoding entirely when no clients are connected
                if (_clientsFront.Count == 0)
                    continue;

                // Validate frame data before JNI encoding
                if (arg.Data == null || arg.Data.Length == 0 || arg.Width <= 0 || arg.Height <= 0)
                {
                    continue;
                }

                var jpegData = Server.EncodeToJpeg(arg.Data, arg.Width, arg.Height,
                    Android.Graphics.ImageFormatType.Nv21, _quality);

                if (_disposed) break; // Check again after encoding

                // Store latest frame for client consumption
                _latestFrontJpeg = jpegData;
                _lastFrame = DateTime.UtcNow;

                // Update FPS tracking
                _totalFrontFrames++;
                _frontFrameCount++;
                if (_frontFpsStopwatch.ElapsedMilliseconds >= 1000)
                {
                    _frontCurrentFps = _frontFrameCount / (_frontFpsStopwatch.ElapsedMilliseconds / 1000.0);
                    _frontFrameCount = 0;
                    _frontFpsStopwatch.Restart();
                }

                // Signal all waiting clients that a new frame is available
                var clientCount = _clientsFront.Count;
                for (int i = 0; i < clientCount; i++)
                {
                    try { _frontFrameSemaphore.Release(); }
                    catch (SemaphoreFullException) { break; } // Semaphore full, all clients notified
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (System.Exception ex)
            {
                if (!_disposed) BaluLogger.Debug("MJPEG SERVER", $"Front encoder error: {ex.Message}");
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
            BaluLogger.Info("MJPEG SERVER", $"STARTING SERVER - StartWithoutStream={StartWithoutStream}");
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
            BaluLogger.Info("MJPEG SERVER", $"STARTED SERVER - IsListening={_listener.IsListening}");
        }
        catch (System.Exception ex)
        {
            BaluLogger.Warn("MJPEG SERVER", $"Start() exception: {ex.Message}");
        }

    }

    /// <summary>
    /// Stops the MJPEG server and disconnects all clients.
    /// </summary>
    public void Stop()
    {
        BaluLogger.Warn("MJPEG SERVER", $"Stop() called - stack trace: {Environment.StackTrace}");
        _clientsBack.Clear();
        _clientsFront.Clear();
        EventBuss.SendCommand(BussCommand.STOP_CAMERA_BACK);
        EventBuss.SendCommand(BussCommand.STOP_CAMERA_FRONT);
        _streamStarted = false;
        _listener.Stop();
    }

    private async Task ListenLoop()
    {
        BaluLogger.Info("MJPEG SERVER", "ListenLoop started");
        while (_listener.IsListening && !_cts.IsCancellationRequested)
        {
            try
            {
                BaluLogger.Debug("MJPEG SERVER", "WAITING CLIENT");
                var ctx = await _listener.GetContextAsync();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await HandleClient(ctx);
                    }
                    catch (System.Exception ex)
                    {
                        BaluLogger.Error("MJPEG SERVER", $"HandleClient unhandled error: {ex.Message}");
                    }
                }, _cts.Token);
            }
            catch (System.Exception ex)
            {
                // Listener was stopped or failed - log it in release mode
                BaluLogger.Warn("MJPEG SERVER", $"ListenLoop exception: {ex.GetType().Name}: {ex.Message}");
            }
        }
        // Log why we exited the loop
        BaluLogger.Warn("MJPEG SERVER", $"ListenLoop EXITED - IsListening={_listener.IsListening}, IsCancelled={_cts.IsCancellationRequested}");
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
                    BaluLogger.Debug("MJPEG SERVER", "First client connected, starting cameras on-demand");
                    EventBuss.SendCommand(BussCommand.START_CAMERA_FRONT);
                    EventBuss.SendCommand(BussCommand.START_CAMERA_BACK);
                    _streamStarted = true;
                }
            }
        }
        var remoteEndpoint = context.Request.RemoteEndPoint?.ToString() ?? "unknown";
        BaluLogger.Debug("MJPEG SERVER", $"Client connected from {remoteEndpoint}");

        var response = context.Response;
        var uri = context.Request.Url?.ToString() ?? string.Empty;

        // Authentication check
        if (_authEnabled && _users.Count > 0)
        {
            if (!ValidateAuthentication(context))
            {
                BaluLogger.Debug("MJPEG SERVER", $"Authentication failed for {remoteEndpoint}");
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

        var clientInfo = new ClientInfo(Guid.NewGuid().ToString("N")[..8], _maxFrameRate);
        var isBackCamera = uri.Contains("Back");

        if (isBackCamera)
        {
            _clientsBack.TryAdd(response, clientInfo);
        }
        else
        {
            _clientsFront.TryAdd(response, clientInfo);
        }

        BaluLogger.Info("MJPEG SERVER", $"Client {clientInfo.ClientId} connected from {remoteEndpoint} for {(isBackCamera ? "Back" : "Front")} camera (max {_maxFrameRate} FPS)");

        try
        {
            // Per-client streaming loop with frame rate limiting (inspired by MauiJpegServer)
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, clientInfo.Cts.Token);

            while (!linkedCts.Token.IsCancellationRequested)
            {
                // Check if stream is still writable (basic check)
                if (!response.OutputStream.CanWrite)
                {
                    BaluLogger.Debug("MJPEG SERVER", $"Client {clientInfo.ClientId} stream no longer writable");
                    break;
                }

                // Wait for next frame using semaphore (efficient blocking)
                var semaphore = isBackCamera ? _backFrameSemaphore : _frontFrameSemaphore;
                var waitResult = await semaphore.WaitAsync(1000, linkedCts.Token).ConfigureAwait(false);

                if (!waitResult)
                {
                    // Semaphore timeout - check client health
                    var now = DateTime.UtcNow;
                    var connectionAge = (now - clientInfo.ConnectedAt).TotalSeconds;
                    var timeSinceLastFrame = (now - clientInfo.LastFrameTime).TotalSeconds;

                    // Timeout case 1: Client received frames but stopped receiving
                    if (clientInfo.FramesSent > 0 && timeSinceLastFrame > ClientTimeoutSeconds)
                    {
                        BaluLogger.Debug("MJPEG SERVER", $"Client {clientInfo.ClientId} timed out - no frames for {timeSinceLastFrame:F0}s");
                        break;
                    }

                    // Timeout case 2: Client never received any frames (camera not started, etc.)
                    if (clientInfo.FramesSent == 0 && connectionAge > ClientMaxIdleSeconds)
                    {
                        BaluLogger.Debug("MJPEG SERVER", $"Client {clientInfo.ClientId} idle timeout - no frames received in {connectionAge:F0}s");
                        break;
                    }

                    continue;
                }

                // Get latest frame
                var jpegData = isBackCamera ? _latestBackJpeg : _latestFrontJpeg;
                if (jpegData == null || jpegData.Length == 0)
                    continue;

                // Per-client frame rate limiting (from MauiJpegServer)
                var now2 = DateTime.UtcNow;
                var timeSinceLastClientFrame = now2 - clientInfo.LastFrameTime;
                if (timeSinceLastClientFrame < clientInfo.MinFrameInterval)
                {
                    // Skip this frame to maintain frame rate limit
                    continue;
                }

                // Write frame to client with timeout protection
                var writeSuccess = await WriteFrameToClientAsync(response, jpegData, clientInfo, linkedCts.Token).ConfigureAwait(false);
                if (!writeSuccess)
                {
                    BaluLogger.Debug("MJPEG SERVER", $"Client {clientInfo.ClientId} write failed - disconnecting");
                    break;
                }

                clientInfo.LastFrameTime = now2;
                clientInfo.FramesSent++;
                clientInfo.BytesSent += jpegData.Length;
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (System.Exception ex)
        {
            BaluLogger.Debug("MJPEG SERVER", $"Client {clientInfo.ClientId} error: {ex.Message}");
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

            // Reset _streamStarted when last client disconnects so cameras restart on-demand
            if (_clientsBack.Count == 0 && _clientsFront.Count == 0)
            {
                lock (_streamLock)
                {
                    if (_clientsBack.Count == 0 && _clientsFront.Count == 0)
                    {
                        _streamStarted = false;
                        BaluLogger.Info("MJPEG SERVER", "Last client disconnected, reset stream state for on-demand restart");
                    }
                }
            }

            clientInfo.Cts.Dispose();

            try
            {
                response.OutputStream.Close();
                response.Close();
            }
            catch { }

            BaluLogger.Info("MJPEG SERVER", $"Client {clientInfo.ClientId} disconnected from {remoteEndpoint}. Frames sent: {clientInfo.FramesSent}, Bytes: {clientInfo.BytesSent / 1024}KB");
        }
    }

    /// <summary>
    /// Writes a JPEG frame to a single client with timeout and batch flushing.
    /// </summary>
    private async Task<bool> WriteFrameToClientAsync(HttpListenerResponse client, byte[] jpegBytes, ClientInfo clientInfo, CancellationToken cancellationToken)
    {
        byte[]? combinedBuffer = null;

        try
        {
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

            // Write with timeout
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(WriteTimeoutMs);

            await client.OutputStream.WriteAsync(combinedBuffer, 0, totalSize, timeoutCts.Token).ConfigureAwait(false);

            // Batch flushing: only flush every N frames
            clientInfo.FrameCount++;
            if (clientInfo.FrameCount >= FlushEveryNFrames)
            {
                await client.OutputStream.FlushAsync(timeoutCts.Token).ConfigureAwait(false);
                clientInfo.FrameCount = 0;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            BaluLogger.Debug("MJPEG SERVER", $"Client {clientInfo.ClientId} write timeout ({WriteTimeoutMs}ms)");
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (combinedBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(combinedBuffer);
            }
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

    /// <summary>
    /// Gets the current back camera FPS (real-time calculation)
    /// </summary>
    public double BackCameraFps => _backCurrentFps;

    /// <summary>
    /// Gets the current front camera FPS (real-time calculation)
    /// </summary>
    public double FrontCameraFps => _frontCurrentFps;

    /// <summary>
    /// Gets the total number of back camera frames processed
    /// </summary>
    public long TotalBackFrames => _totalBackFrames;

    /// <summary>
    /// Gets the total number of front camera frames processed
    /// </summary>
    public long TotalFrontFrames => _totalFrontFrames;

    /// <summary>
    /// Gets the latest back camera JPEG frame (for snapshot endpoints)
    /// </summary>
    public byte[]? GetLatestBackFrame() => _latestBackJpeg;

    /// <summary>
    /// Gets the latest front camera JPEG frame (for snapshot endpoints)
    /// </summary>
    public byte[]? GetLatestFrontFrame() => _latestFrontJpeg;
}
