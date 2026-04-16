using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Android.Util;
using BaluMediaServer.Models;
using BaluMediaServer.Platforms.Android.Services;
using BaluMediaServer.Repositories;
using BaluMediaServer.RTSP.ClientManagement;
using BaluMediaServer.RTSP.Protocol;
using BaluMediaServer.RTSP.Security;
using BaluMediaServer.RTSP.Streaming;
using BaluMediaServer.RTSP.Transport;
using AuthManager = BaluMediaServer.RTSP.Security.AuthenticationManager;

namespace BaluMediaServer.Services;

/// <summary>
/// Main RTSP server implementation that handles client connections and video streaming.
/// Supports H.264 and MJPEG codecs with both TCP interleaved and UDP transport modes.
/// Manages front and back camera services and provides authentication support.
/// </summary>
public class Server : IDisposable
{
    // Camera services
    private FrontCameraService _frontService = new();
    private BackCameraService _backService = new();
    private MjpegServer? _mjpegServer;

    // Core server state
    private Socket _socket = default!;
    private CancellationTokenSource _cts = new();
    private readonly string _address;
    private readonly int _port;
    private readonly int _maxClients;
    private bool _isStreaming;
    private bool _isCapturingFront;
    private bool _isCapturingBack;
    private bool _mjpegServerEnabled;

    // Camera configuration
    private int _backCameraWidth, _backCameraHeight, _frontCameraWidth, _frontCameraHeight;
    private readonly bool _frontCameraEnabled;
    private readonly bool _backCameraEnabled;

    // MJPEG server configuration
    private readonly int _mjpegServerQuality;
    private readonly int _mjpegServerPort;
    private readonly bool _mjpegUseHttps;
    private readonly string? _mjpegCertificatePath;
    private readonly string? _mjpegCertificatePassword;

    // Frame caching
    private FrameEventArgs? _latestFrontFrame;
    private FrameEventArgs? _latestBackFrame;
    private readonly object _frameFrontLock = new();
    private readonly object _frameBackLock = new();

    // Per-camera locks to prevent races between resolution changes and concurrent SETUP/PLAY
    private readonly object _backCameraLock = new();
    private readonly object _frontCameraLock = new();

    // Module dependencies
    private readonly IAuthenticationManager _authManager;
    private readonly SdpGenerator _sdpGenerator;
    private readonly ITransportManager _transportManager;
    private readonly IRtpPacketBuilder _rtpBuilder;
    private readonly RtcpManager _rtcpManager;
    private readonly H264EncoderManager _encoderManager;
    private readonly JpegEncoderService _jpegEncoder;
    private readonly StreamingController _streamingController;
    private readonly ClientManager _clientManager;

    private bool _enabled;
    private List<VideoProfile> _videoProfiles = new();

    /// <summary>
    /// Event raised when the client list changes.
    /// </summary>
    public static event Action<List<Client>>? OnClientsChange;

    /// <summary>
    /// Event raised when streaming state changes.
    /// </summary>
    public static event EventHandler<bool>? OnStreaming;

    /// <summary>
    /// Event raised when a new frame is received from the front camera.
    /// </summary>
    public static event EventHandler<FrameEventArgs>? OnNewFrontFrame;

    /// <summary>
    /// Event raised when a new frame is received from the back camera.
    /// </summary>
    public static event EventHandler<FrameEventArgs>? OnNewBackFrame;

    /// <summary>
    /// Gets a value indicating whether the server is currently running.
    /// </summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="Server"/> class with individual parameters.
    /// </summary>
    public Server(int Port = 7778, int MaxClients = 100, string Address = "0.0.0.0",
        Dictionary<string, string>? Users = null, bool BackCameraEnabled = true,
        bool FrontCameraEnabled = true, bool AuthRequired = true, int MjpegServerQuality = 80,
        int MjpegServerPort = 8089, bool UseHttps = false, string? CertificatePath = null,
        string? CertificatePassword = null, VideoResolution BackCameraResolution = VideoResolution.VGA_640x480,
        VideoResolution FrontCameraResolution = VideoResolution.VGA_640x480)
    {
        _enabled = true;
        _port = Port;
        _maxClients = MaxClients;
        _address = Address;
        _mjpegServerQuality = MjpegServerQuality;
        _mjpegServerPort = MjpegServerPort;
        _mjpegUseHttps = UseHttps;
        _mjpegCertificatePath = CertificatePath;
        _mjpegCertificatePassword = CertificatePassword;
        _frontCameraEnabled = FrontCameraEnabled;
        _backCameraEnabled = BackCameraEnabled;
        _backCameraWidth = BackCameraResolution.GetWidth();
        _backCameraHeight = BackCameraResolution.GetHeight();
        _frontCameraWidth = FrontCameraResolution.GetWidth();
        _frontCameraHeight = FrontCameraResolution.GetHeight();

        // Initialize modules
        _authManager = new AuthManager { RequireAuthentication = AuthRequired };
        _authManager.AddUser("admin", "password123");
        if (Users != null)
        {
            foreach (var item in Users)
            {
                if (_authManager.Users.ContainsKey(item.Key))
                    _authManager.UpdateUser(item.Key, item.Value);
                else
                    _authManager.AddUser(item.Key, item.Value);
            }
        }

        _sdpGenerator = new SdpGenerator();
        _transportManager = new TransportManager(_cts);
        _rtpBuilder = new RtpPacketBuilder(_transportManager);
        _rtcpManager = new RtcpManager(_transportManager, _cts.Token);
        _encoderManager = new H264EncoderManager();
        _jpegEncoder = new JpegEncoderService(quality: _mjpegServerQuality);
        _clientManager = new ClientManager(_transportManager);
        _streamingController = new StreamingController(_encoderManager, _jpegEncoder, _rtpBuilder, _transportManager, _clientManager);

        // Wire up events with exception safety
        _clientManager.OnClientsChange += clients =>
        {
            try { OnClientsChange?.Invoke(clients); }
            catch (Exception ex) { Log.Error("[RTSP Server]", $"OnClientsChange subscriber error: {ex.Message}"); }
        };
        _streamingController.StreamingStateChanged += (_, streaming) =>
        {
            try { OnStreaming?.Invoke(this, streaming); }
            catch (Exception ex) { Log.Error("[RTSP Server]", $"OnStreaming subscriber error: {ex.Message}"); }
        };
        _streamingController.CameraStartRequested += OnCameraStartRequested;
        _streamingController.EncoderResolutionFallback += OnEncoderResolutionFallback;
        _streamingController.GetLatestFrame = GetLatestFrame;
        _rtcpManager.ClientCleanupRequired += (_, client) => _clientManager.CleanupClient(client);
        _rtcpManager.BitrateAdjustmentRequired += OnBitrateAdjustmentRequired;
        _encoderManager.FrameEncoded += OnEncoderFrameEncoded;

        // Initialize MJPEG server
        _mjpegServer = CreateMjpegServer();

        // Configure error handlers
        _backService.ErrorOccurred += LogError;
        _frontService.ErrorOccurred += LogError;

        ConfigureSocket();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Server"/> class using a configuration object.
    /// </summary>
    public Server(ServerConfiguration configuration)
        : this(
            Port: configuration.Port,
            MaxClients: configuration.MaxClients,
            Address: configuration.BaseAddress,
            Users: configuration.Users,
            BackCameraEnabled: configuration.BackCameraEnabled,
            FrontCameraEnabled: configuration.FrontCameraEnabled,
            AuthRequired: configuration.AuthRequired,
            MjpegServerQuality: configuration.MjpegServerQuality,
            MjpegServerPort: configuration.MjpegServerPort,
            UseHttps: configuration.UseHttps,
            CertificatePath: configuration.CertificatePath,
            CertificatePassword: configuration.CertificatePassword,
            BackCameraResolution: configuration.BackCameraResolution,
            FrontCameraResolution: configuration.FrontCameraResolution)
    {
        _enabled = configuration.EnableServer;
        if (configuration.StartMjpegServer)
            _mjpegServer?.Start(true);
    }

    /// <summary>
    /// Gets a value indicating whether RTSP streaming is active.
    /// </summary>
    public bool IsStreaming() => _isStreaming;

    /// <summary>
    /// Gets a value indicating whether the MJPEG server is streaming.
    /// </summary>
    public bool MjpegServerStreaming() => _mjpegServer?.IsStreaming() ?? false;

    /// <summary>
    /// Adds a new user for authentication.
    /// </summary>
    public bool AddUser(string user, string password) => _authManager.AddUser(user, password);

    /// <summary>
    /// Updates an existing user's password.
    /// </summary>
    public bool UpdateUser(string user, string password) => _authManager.UpdateUser(user, password);

    /// <summary>
    /// Removes a user from the authentication list.
    /// </summary>
    public bool RemoveUser(string user) => _authManager.RemoveUser(user);

    /// <summary>
    /// Sets the resolution for the back camera.
    /// If the camera is running, restarts the full pipeline at the new resolution.
    /// </summary>
    public void SetBackCameraResolution(VideoResolution resolution)
        => ApplyResolutionChange(0, resolution.GetWidth(), resolution.GetHeight());

    /// <summary>
    /// Sets a custom resolution for the back camera.
    /// If the camera is running, restarts the full pipeline at the new resolution.
    /// </summary>
    public void SetBackCameraResolution(int width, int height)
        => ApplyResolutionChange(0, width, height);

    /// <summary>
    /// Sets the resolution for the front camera.
    /// If the camera is running, restarts the full pipeline at the new resolution.
    /// </summary>
    public void SetFrontCameraResolution(VideoResolution resolution)
        => ApplyResolutionChange(1, resolution.GetWidth(), resolution.GetHeight());

    /// <summary>
    /// Sets a custom resolution for the front camera.
    /// If the camera is running, restarts the full pipeline at the new resolution.
    /// </summary>
    public void SetFrontCameraResolution(int width, int height)
        => ApplyResolutionChange(1, width, height);

    /// <summary>
    /// Gets the current back camera resolution.
    /// </summary>
    public (int Width, int Height) GetBackCameraResolution() => (_backCameraWidth, _backCameraHeight);

    /// <summary>
    /// Gets the current front camera resolution.
    /// </summary>
    public (int Width, int Height) GetFrontCameraResolution() => (_frontCameraWidth, _frontCameraHeight);

    /// <summary>
    /// Adds a video profile configuration for streaming.
    /// </summary>
    public void SetVideoProfile(VideoProfile profile) => _videoProfiles.Add(profile);

    /// <summary>
    /// Starts the RTSP server and begins accepting client connections.
    /// </summary>
    public bool Start()
    {
        if (_enabled && !IsRunning)
        {
            // Probe encoder-supported resolution BEFORE starting cameras.
            // This avoids the costly double-start: camera at 1920x1080 → encoder falls back
            // to 1280x720 → camera restarts at 1280x720 (adds 10-30s on MediaTek).
            var (backW, backH) = H264Encoder.ProbeSupportedResolution(_backCameraWidth, _backCameraHeight);
            if (backW != _backCameraWidth || backH != _backCameraHeight)
            {
                Log.Info("[RTSP Server]", $"Adjusting back camera resolution to encoder-supported {backW}x{backH} (was {_backCameraWidth}x{_backCameraHeight})");
                _backCameraWidth = backW;
                _backCameraHeight = backH;
            }
            var (frontW, frontH) = H264Encoder.ProbeSupportedResolution(_frontCameraWidth, _frontCameraHeight);
            if (frontW != _frontCameraWidth || frontH != _frontCameraHeight)
            {
                Log.Info("[RTSP Server]", $"Adjusting front camera resolution to encoder-supported {frontW}x{frontH} (was {_frontCameraWidth}x{_frontCameraHeight})");
                _frontCameraWidth = frontW;
                _frontCameraHeight = frontH;
            }

            ConfigureSocket();
            IsRunning = true;
            EventBuss.Command += OnCommandSend;
            _backService.FrameReceived += OnBackFrameAvailable;
            _frontService.FrameReceived += OnFrontFrameAvailable;
            Task.Run(ListenAsync, _cts.Token);
            Task.Run(WatchDog, _cts.Token);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Stops the RTSP server and disconnects all clients.
    /// </summary>
    public void Stop()
    {
        Log.Warn("[RTSP Server]", $"Stop() called - stack trace: {Environment.StackTrace}");
        IsRunning = false;
        EventBuss.Command -= OnCommandSend;
        _cts?.Cancel();
        _cts = new();
        _backService.FrameReceived -= OnBackFrameAvailable;
        _frontService.FrameReceived -= OnFrontFrameAvailable;
        _mjpegServer?.Stop();
        _socket.Close();
        _socket?.Dispose();
    }

    /// <summary>
    /// Releases all resources used by the server.
    /// </summary>
    public void Dispose()
    {
        Log.Warn("[RTSP Server]", $"Dispose() called - stack trace: {Environment.StackTrace}");
        IsRunning = false;
        EventBuss.Command -= OnCommandSend;
        _mjpegServer?.Dispose();
        _jpegEncoder?.Dispose();
        _cts?.Cancel();
        _socket?.Dispose();
    }

    private MjpegServer CreateMjpegServer() => new(
        port: _mjpegServerPort,
        quality: _mjpegServerQuality,
        bindAddress: _address,
        authEnabled: _authManager.RequireAuthentication,
        users: _authManager.Users.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
        useHttps: _mjpegUseHttps,
        certificatePath: _mjpegCertificatePath,
        certificatePassword: _mjpegCertificatePassword
    );

    private void LogError(object? sender, string error)
    {
        if (sender is FrontCameraService)
            Log.Error("FRONT CAMERA SERVICE ERROR", error);
        else
            Log.Error("BACK CAMERA SERVICE ERROR", error);
    }

    private Socket CreateConfiguredSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, true);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.SendBuffer, 65536);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer, 65536);
        return socket;
    }

    private void ConfigureSocket()
    {
        IPEndPoint endpoint = new(IPAddress.Parse(_address), _port);
        try
        {
            _socket?.Close();
            _socket?.Dispose();
            _socket = CreateConfiguredSocket();
            _socket.Bind(endpoint);
            _socket.Listen(_maxClients);
        }
        catch (Exception ex)
        {
            Log.Warn("[RTSP Server]", $"Socket bind failed, retrying: {ex.Message}");
            try
            {
                _socket?.Close();
                _socket?.Dispose();
                _socket = CreateConfiguredSocket();
                _socket.Bind(endpoint);
                _socket.Listen(_maxClients);
            }
            catch (Exception retryEx)
            {
                Log.Error("[RTSP Server]", $"Socket configuration failed: {retryEx.Message}");
                throw; // Re-throw to caller - socket cannot be configured
            }
        }
    }

    private void OnCommandSend(BussCommand command)
    {
        try
        {
            switch (command)
            {
                case BussCommand.START_CAMERA_FRONT:
                    lock (_frontCameraLock)
                    {
                        if (!_isCapturingFront && _frontCameraEnabled)
                        {
                            if (!IsRunning)
                            {
                                _frontService = new();
                                _frontService.FrameReceived += OnFrontFrameAvailable;
                            }
                            _frontService.StartCapture(_frontCameraWidth, _frontCameraHeight);
                            _isCapturingFront = true;
                        }
                    }
                    break;
                case BussCommand.STOP_CAMERA_FRONT:
                    // NOTE: Camera stop disabled for continuous streaming to prevent interruptions
                    // To stop cameras, use the explicit Stop() method or stop the server
                    Log.Debug("[RTSP Server]", "STOP_CAMERA_FRONT command ignored - continuous streaming mode enabled");
                    break;
                case BussCommand.START_CAMERA_BACK:
                    lock (_backCameraLock)
                    {
                        if (!_isCapturingBack && _backCameraEnabled)
                        {
                            if (!IsRunning)
                            {
                                _backService = new();
                                _backService.FrameReceived += OnBackFrameAvailable;
                            }
                            _backService.StartCapture(_backCameraWidth, _backCameraHeight);
                            _isCapturingBack = true;
                        }
                    }
                    break;
                case BussCommand.STOP_CAMERA_BACK:
                    // NOTE: Camera stop disabled for continuous streaming to prevent interruptions
                    // To stop cameras, use the explicit Stop() method or stop the server
                    Log.Debug("[RTSP Server]", "STOP_CAMERA_BACK command ignored - continuous streaming mode enabled");
                    break;
                case BussCommand.START_MJPEG_SERVER:
                    if (!_mjpegServerEnabled)
                    {
                        _mjpegServerEnabled = true;
                        _mjpegServer ??= CreateMjpegServer();
                        _mjpegServer.Start();
                    }
                    break;
                case BussCommand.STOP_MJPEG_SERVER:
                    // NOTE: MJPEG server stop disabled for continuous streaming to prevent interruptions
                    // To stop the MJPEG server, use the explicit Stop() method or stop the RTSP server
                    Log.Debug("[RTSP Server]", "STOP_MJPEG_SERVER command ignored - continuous streaming mode enabled");
                    break;
                case BussCommand.SWITCH_CAMERA:
                    // Implementation unchanged
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("BALU MEDIA SERVER SERVER", ex.Message);
        }
    }

    private bool _loggedFirstBackFrame;
    private int _getLatestFrameNullCount;
    private void OnBackFrameAvailable(object? sender, FrameEventArgs arg)
    {
        if (arg?.Data != null && arg.Data.Length > 0)
        {
            if (!_loggedFirstBackFrame)
            {
                _loggedFirstBackFrame = true;
                Log.Info("[RTSP Server]", $"First back frame received: {arg.Width}x{arg.Height}, {arg.Data.Length} bytes");
            }
            lock (_frameBackLock)
            {
                var wasNull = _latestBackFrame == null;
                _latestBackFrame = arg;
                if (wasNull && _getLatestFrameNullCount > 0)
                {
                    Log.Info("[RTSP Server]", $"Back frame restored after {_getLatestFrameNullCount} null reads: {arg.Width}x{arg.Height}, {arg.Data.Length} bytes");
                }
            }
            // Feed encoder FIRST — lowest latency path. Event subscribers run after.
            if (_isStreaming)
            {
                _encoderManager.FeedFrame(0, arg);
            }
            // Queue for JPEG encoding only when RTSP-MJPEG clients exist
            if (_clientManager.HasMjpegClients)
                _jpegEncoder.QueueFrame(arg, 0);
            try
            {
                OnNewBackFrame?.Invoke(this, arg);
            }
            catch (Exception ex)
            {
                Log.Error("[RTSP Server]", $"OnNewBackFrame subscriber error: {ex.Message}");
            }
        }
    }

    private void OnFrontFrameAvailable(object? sender, FrameEventArgs arg)
    {
        if (arg?.Data != null && arg.Data.Length > 0)
        {
            lock (_frameFrontLock)
            {
                _latestFrontFrame = arg;
            }
            // Feed encoder FIRST — lowest latency path. Event subscribers run after.
            if (_isStreaming)
            {
                _encoderManager.FeedFrame(1, arg);
            }
            // Queue for JPEG encoding only when RTSP-MJPEG clients exist
            if (_clientManager.HasMjpegClients)
                _jpegEncoder.QueueFrame(arg, 1);
            try
            {
                OnNewFrontFrame?.Invoke(this, arg);
            }
            catch (Exception ex)
            {
                Log.Error("[RTSP Server]", $"OnNewFrontFrame subscriber error: {ex.Message}");
            }
        }
    }

    private FrameEventArgs? GetLatestFrame(int cameraId)
    {
        if (cameraId == 1)
        {
            lock (_frameFrontLock)
            {
                return _latestFrontFrame;
            }
        }
        else
        {
            lock (_frameBackLock)
            {
                var frame = _latestBackFrame;
                if (frame == null)
                {
                    // Log only every 10th call to avoid flooding logcat
                    if (++_getLatestFrameNullCount % 10 == 1)
                    {
                        Log.Info("[RTSP Server]", $"GetLatestFrame(back): null (x{_getLatestFrameNullCount}), isCapturing={_isCapturingBack}, isStreaming={_isStreaming}");
                    }
                }
                else
                {
                    _getLatestFrameNullCount = 0;
                }
                return frame;
            }
        }
    }

    /// <summary>
    /// Pre-starts the camera and H.264 encoder during SETUP so they're ready
    /// when PLAY arrives. Without this, the encoder warm-up (~500ms) causes
    /// live555/VLC to timeout waiting for the first RTP packet on first connect.
    /// </summary>
    private void PreStartCameraAndEncoder(Client client)
    {
        var cameraLock = client.CameraId == 0 ? _backCameraLock : _frontCameraLock;

        lock (cameraLock)
        {
            // Start camera capture if not already running
            if (client.CameraId == 0 && !_isCapturingBack && _backCameraEnabled)
            {
                _backService.StartCapture(_backCameraWidth, _backCameraHeight);
                _isCapturingBack = true;
                Log.Info("[RTSP Server]", "Pre-started back camera at SETUP time");
            }
            else if (client.CameraId == 1 && !_isCapturingFront && _frontCameraEnabled)
            {
                _frontService.StartCapture(_frontCameraWidth, _frontCameraHeight);
                _isCapturingFront = true;
                Log.Info("[RTSP Server]", "Pre-started front camera at SETUP time");
            }
        }

        // Enable frame feeding to encoder
        _isStreaming = true;
        _streamingController.SetStreamingState(true);

        // Pre-warm H.264 encoder in background (needs first frame for dimensions)
        if (client.Codec == CodecType.H264 && !_encoderManager.IsEncoderRunning(client.CameraId))
        {
            PreWarmEncoderAsync(client.CameraId);
        }
    }

    private void OnCameraStartRequested(object? sender, int cameraId)
    {
        if (cameraId == 1)
        {
            lock (_frontCameraLock)
            {
                if (!_isCapturingFront)
                {
                    _frontService.StartCapture(_frontCameraWidth, _frontCameraHeight);
                    _isCapturingFront = true;
                }
            }
        }
        else if (cameraId == 0)
        {
            lock (_backCameraLock)
            {
                if (!_isCapturingBack)
                {
                    _backService.StartCapture(_backCameraWidth, _backCameraHeight);
                    _isCapturingBack = true;
                }
            }
        }
    }

    private void OnEncoderResolutionFallback(object? sender, (int cameraId, int width, int height) args)
    {
        var (cameraId, actualW, actualH) = args;
        var cameraLock = cameraId == 0 ? _backCameraLock : _frontCameraLock;

        lock (cameraLock)
        {
            // Stop encoder and restart camera at the resolution the encoder actually supports.
            // Do NOT pre-warm here — StreamingController will wait for the new frame and restart the encoder.
            if (cameraId == 0)
            {
                if (_backCameraWidth == actualW && _backCameraHeight == actualH)
                    return;
                Log.Info("[RTSP Server]", $"Encoder fallback: restarting back camera {_backCameraWidth}x{_backCameraHeight} -> {actualW}x{actualH}");
                _backCameraWidth = actualW;
                _backCameraHeight = actualH;
                _encoderManager.StopEncoder(cameraId);
                _encoderManager.ClearSpsPps();
                _sdpGenerator.ClearSpsPps();
                lock (_frameBackLock) { _latestBackFrame = null; }
                _loggedFirstBackFrame = false; // Reset so we see first frame after restart
                _backService.StopCapture();
                _isCapturingBack = false;
                _backService.StartCapture(actualW, actualH);
                _isCapturingBack = true;
            }
            else
            {
                if (_frontCameraWidth == actualW && _frontCameraHeight == actualH)
                    return;
                Log.Info("[RTSP Server]", $"Encoder fallback: restarting front camera {_frontCameraWidth}x{_frontCameraHeight} -> {actualW}x{actualH}");
                _frontCameraWidth = actualW;
                _frontCameraHeight = actualH;
                _encoderManager.StopEncoder(cameraId);
                _encoderManager.ClearSpsPps();
                _sdpGenerator.ClearSpsPps();
                _latestFrontFrame = null;
                _frontService.StopCapture();
                _isCapturingFront = false;
                _frontService.StartCapture(actualW, actualH);
                _isCapturingFront = true;
            }
        }
    }

    /// <summary>
    /// Applies a resolution change for the specified camera. If the camera is running,
    /// restarts the full pipeline: encoder → SPS/PPS → camera → clients → pre-warm.
    /// Thread-safe via per-camera locks.
    /// </summary>
    private void ApplyResolutionChange(int cameraId, int newWidth, int newHeight)
    {
        var cameraLock = cameraId == 0 ? _backCameraLock : _frontCameraLock;

        lock (cameraLock)
        {
            // Read current dimensions
            int oldWidth, oldHeight;
            if (cameraId == 0)
            {
                oldWidth = _backCameraWidth;
                oldHeight = _backCameraHeight;
            }
            else
            {
                oldWidth = _frontCameraWidth;
                oldHeight = _frontCameraHeight;
            }

            // Skip if unchanged
            if (oldWidth == newWidth && oldHeight == newHeight)
                return;

            Log.Info("[RTSP Server]", $"Resolution change for {(cameraId == 0 ? "back" : "front")} camera: {oldWidth}x{oldHeight} -> {newWidth}x{newHeight}");

            // Update resolution fields
            if (cameraId == 0)
            {
                _backCameraWidth = newWidth;
                _backCameraHeight = newHeight;
            }
            else
            {
                _frontCameraWidth = newWidth;
                _frontCameraHeight = newHeight;
            }

            // If camera not running, just update fields (resolution used on next start)
            bool isCameraRunning = cameraId == 0 ? _isCapturingBack : _isCapturingFront;
            if (!isCameraRunning)
            {
                Log.Info("[RTSP Server]", $"Camera {cameraId} not running, resolution will apply on next start");
                return;
            }

            // Stop H.264 encoder
            _encoderManager.StopEncoder(cameraId);
            Log.Info("[RTSP Server]", $"Stopped H264 encoder for camera {cameraId}");

            // Clear SPS/PPS caches (old resolution params are invalid)
            _encoderManager.ClearSpsPps();
            _sdpGenerator.ClearSpsPps();

            // Stop camera capture
            if (cameraId == 0)
            {
                _backService.StopCapture();
                _isCapturingBack = false;
            }
            else
            {
                _frontService.StopCapture();
                _isCapturingFront = false;
            }

            // Clear cached latest frame (prevents encoder pre-warm from picking up stale old-resolution frame)
            if (cameraId == 0)
            {
                lock (_frameBackLock) { _latestBackFrame = null; }
            }
            else
            {
                lock (_frameFrontLock) { _latestFrontFrame = null; }
            }

            // Disconnect affected RTSP clients (only those watching this camera)
            DisconnectClientsForCamera(cameraId);

            // Restart camera at new resolution
            if (cameraId == 0)
            {
                _backService.StartCapture(newWidth, newHeight);
                _isCapturingBack = true;
            }
            else
            {
                _frontService.StartCapture(newWidth, newHeight);
                _isCapturingFront = true;
            }
            Log.Info("[RTSP Server]", $"Restarted {(cameraId == 0 ? "back" : "front")} camera at {newWidth}x{newHeight}");

            // Pre-warm encoder in background
            PreWarmEncoderAsync(cameraId);
        }
    }

    /// <summary>
    /// Disconnects RTSP clients watching the specified camera.
    /// MJPEG HTTP clients are NOT disconnected — MJPEG adapts automatically since each frame is independent.
    /// </summary>
    private void DisconnectClientsForCamera(int cameraId)
    {
        var clients = _clientManager.GetActiveClients();
        int disconnected = 0;

        foreach (var client in clients)
        {
            if (client.CameraId == cameraId)
            {
                _clientManager.CleanupClient(client);
                disconnected++;
            }
        }

        if (disconnected > 0)
            Log.Info("[RTSP Server]", $"Disconnected {disconnected} RTSP client(s) for camera {cameraId}");
    }

    /// <summary>
    /// Fire-and-forget background task that waits for the first new frame from the camera,
    /// then starts the H.264 encoder. Uses more retries than initial pre-warm since
    /// camera restart takes longer than initial start.
    /// </summary>
    private void PreWarmEncoderAsync(int cameraId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                FrameEventArgs? frame = null;
                int retries = 0;
                const int maxRetries = 50;

                while ((frame = GetLatestFrame(cameraId)) == null || frame.Data == null)
                {
                    if (retries++ > maxRetries)
                    {
                        Log.Warn("[RTSP Server]", $"Pre-warm timeout waiting for frame from camera {cameraId} after resolution change");
                        return;
                    }
                    int delayMs = Math.Min(10 * (1 << Math.Min(retries, 4)), 100);
                    await Task.Delay(delayMs, _cts.Token).ConfigureAwait(false);
                }

                _encoderManager.StartEncoder(cameraId, frame.Width, frame.Height, frame.Data.Length);

                // Check if encoder fell back to a different resolution
                var (actualW, actualH) = _encoderManager.GetActualResolution(cameraId);
                if (actualW > 0 && actualH > 0 && (actualW != frame.Width || actualH != frame.Height))
                {
                    Log.Warn("[RTSP Server]", $"Pre-warm: encoder fell back to {actualW}x{actualH} (camera: {frame.Width}x{frame.Height}) — restarting camera");
                    // Reuse the same fallback handler
                    OnEncoderResolutionFallback(this, (cameraId, actualW, actualH));
                    Log.Info("[RTSP Server]", $"Camera {cameraId} restarted at {actualW}x{actualH} — encoder will be started by StreamingController");
                }
                else
                {
                    Log.Info("[RTSP Server]", $"Pre-warmed H264 encoder for camera {cameraId}: {frame.Width}x{frame.Height}");
                }
            }
            catch (OperationCanceledException)
            {
                // Server shutting down, ignore
            }
            catch (Exception ex)
            {
                Log.Error("[RTSP Server]", $"Pre-warm encoder error after resolution change: {ex.Message}");
            }
        }, _cts.Token);
    }

    private void OnBitrateAdjustmentRequired(object? sender, (Client client, int newBitrate) args)
    {
        _encoderManager.UpdateBitrate(args.client.CameraId, args.newBitrate);
    }

    private void OnEncoderFrameEncoded(object? sender, H264FrameEventArgs e)
    {
        // Update SDP generator with SPS/PPS
        if (e.Sps != null || e.Pps != null)
        {
            _sdpGenerator.UpdateSpsPps(e.Sps, e.Pps);
        }
    }

    private async Task WatchDog()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                _clientManager.NotifyClientsChanged();

                var playingClients = _clientManager.PlayingClientCount;
                var deadClients = _clientManager.GetDeadClients();

                if (deadClients.Count > 0)
                {
                    Log.Info("[RTSP Server]", $"WatchDog cleaning up {deadClients.Count} dead client(s)");
                }

                foreach (var client in deadClients)
                {
                    _clientManager.CleanupClient(client);
                }

                // NOTE: Auto-stop functionality disabled for continuous streaming
                // Cameras and encoders will keep running even when no clients are connected
                // This prevents stream interruptions and frame drops
                // To manually stop, use the Stop() method or stop commands via EventBus

                var mjpegClientCount = _mjpegServer?.ClientCount ?? 0;
                Log.Debug("[RTSP Server]", $"WatchDog: Active clients: RTSP={playingClients}, MJPEG={mjpegClientCount}, Cameras: Back={_isCapturingBack}, Front={_isCapturingFront}, Streaming={_isStreaming}");
            }
            catch (Exception ex)
            {
                Log.Error("[RTSP Server]", $"WatchDog error: {ex.Message}");
            }

            // Report streaming state considering BOTH RTSP and MJPEG clients
            // This prevents the "IDLE" state when only MJPEG clients are connected
            var hasAnyClients = _isStreaming || (_mjpegServer?.ClientCount ?? 0) > 0;
            var camerasRunning = _isCapturingBack || _isCapturingFront;
            try
            {
                OnStreaming?.Invoke(this, hasAnyClients || camerasRunning);
            }
            catch (Exception ex)
            {
                Log.Error("[RTSP Server]", $"OnStreaming subscriber error: {ex.Message}");
            }
            await Task.Delay(5000, _cts.Token).ConfigureAwait(false);
        }
    }

    private async Task ListenAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var client = await _socket.AcceptAsync(_cts.Token);
                client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, true);
                client.SendTimeout = 3000; // 3s send timeout — replaces per-packet CTS allocation
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await HandleClient(client);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("[RTSP Server]", $"HandleClient unhandled error: {ex.Message}");
                    }
                }, _cts.Token);
            }
            catch { }
        }
    }

    private async Task HandleClient(Socket socket)
    {
        Client? client = null;
        try
        {
            client = new Client
            {
                Socket = socket,
                Id = Guid.NewGuid().ToString(),
                ConnectedAt = DateTime.UtcNow
            };
            _clientManager.AddClient(client);

            using NetworkStream stream = new(socket, false); // ownsSocket=false: we manage socket lifetime
            using StreamReader reader = new(stream);
            using StreamWriter writer = new(stream) { AutoFlush = true, NewLine = "\r\n" };

            // Create protocol handler for this connection
            var protocolHandler = new RtspProtocolHandler(
                _authManager,
                _sdpGenerator,
                _transportManager,
                _rtcpManager,
                _cts.Token,
                _frontCameraEnabled,
                _backCameraEnabled,
                _backCameraWidth,
                _backCameraHeight,
                _frontCameraWidth,
                _frontCameraHeight);

            while (!_cts.IsCancellationRequested)
            {
                string? requestLine;
                try
                {
                    requestLine = await reader.ReadLineAsync().ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // Socket closed or reset by client — signal streaming task to stop immediately
                    // so it doesn't linger waiting for 10 consecutive send errors
                    if (client != null) lock (client) { client.IsPlaying = false; }
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                // ReadLineAsync returns null when the stream is closed (client disconnected)
                if (requestLine == null)
                {
                    Log.Info("[RTSP Server]", $"Client {client.Id} disconnected (end of stream)");
                    // Signal streaming task to stop immediately rather than waiting for 10 send errors
                    if (client != null) lock (client) { client.IsPlaying = false; }
                    break;
                }

                if (string.IsNullOrEmpty(requestLine)) continue;

                Log.Debug("[RTSP Server]", requestLine);
                var request = await protocolHandler.ParseRequestAsync(reader, requestLine).ConfigureAwait(false);
                if (request == null) continue;

                await ProcessRtspRequest(writer, request, client, protocolHandler).ConfigureAwait(false);
            }
        }
        finally
        {
            // Only close the socket if the client is not actively streaming.
            // The streaming task will handle socket cleanup when it finishes.
            if (client != null && !client.IsPlaying)
            {
                socket?.Close();
            }
        }
    }

    private async Task ProcessRtspRequest(StreamWriter writer, RtspRequest request, Client client, RtspProtocolHandler protocolHandler)
    {
        // OPTIONS must be handled before authentication — VLC sends OPTIONS
        // as an unauthenticated capability probe per RFC 2326 §10.1
        if (request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            await protocolHandler.HandleOptionsAsync(writer, request).ConfigureAwait(false);
            return;
        }

        if (!_authManager.IsAuthenticated(request) && _authManager.RequireAuthentication)
        {
            await _authManager.SendAuthenticationRequiredAsync(writer, request.CSeq).ConfigureAwait(false);
            return;
        }

        var uri = new Uri(request.Uri);
        if (!await protocolHandler.HandleUriAsync(uri.AbsolutePath, writer, request, client).ConfigureAwait(false))
        {
            return;
        }

        switch (request.Method.ToUpper())
        {
            case "DESCRIBE":
                await protocolHandler.HandleDescribeAsync(writer, request, client).ConfigureAwait(false);
                break;
            case "SETUP":
                await protocolHandler.HandleSetupAsync(writer, request, client).ConfigureAwait(false);
                // Pre-start camera and encoder during SETUP so they're warm by the time
                // PLAY is received. This prevents live555/VLC timeout on first connect,
                // since the encoder needs ~500ms to start producing frames.
                PreStartCameraAndEncoder(client);
                break;
            case "PLAY":
                if (await protocolHandler.HandlePlayAsync(writer, request, client).ConfigureAwait(false))
                {
                    _isStreaming = true;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _streamingController.StreamToClientAsync(client, _cts.Token);
                        }
                        catch (Exception ex)
                        {
                            Log.Error("[RTSP Server]", $"StreamToClient unhandled error: {ex.Message}");
                        }
                    }, _cts.Token);
                }
                break;
            case "TEARDOWN":
                await protocolHandler.HandleTeardownAsync(writer, request, client).ConfigureAwait(false);
                break;
            default:
                await protocolHandler.SendResponseAsync(writer, 405, "Method Not Allowed", request.CSeq).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// Encodes raw image data to JPEG format.
    /// Thread-safe: Creates and disposes Java objects within the same call to prevent JNI crashes.
    /// </summary>
    /// <param name="rawImageData">The raw image data.</param>
    /// <param name="width">The image width.</param>
    /// <param name="height">The image height.</param>
    /// <param name="format">The image format.</param>
    /// <param name="quality">The JPEG quality (0-100).</param>
    /// <returns>The JPEG encoded data.</returns>
    // Per-thread reusable MemoryStream to reduce GC pressure from JPEG encoding
    [ThreadStatic]
    private static MemoryStream? t_jpegOutputStream;

    public static byte[] EncodeToJpeg(byte[] rawImageData, int width, int height, Android.Graphics.ImageFormatType format, int quality = 80)
    {
        // Validate input to prevent JNI crashes on invalid data
        if (rawImageData == null || rawImageData.Length == 0 || width <= 0 || height <= 0)
        {
            return Array.Empty<byte>();
        }

        Android.Graphics.YuvImage? yuvImage = null;
        Android.Graphics.Rect? rect = null;
        Android.Graphics.Bitmap? bitmap = null;

        try
        {
            var outputStream = t_jpegOutputStream ??= new MemoryStream(width * height);
            outputStream.SetLength(0); // reset for reuse

            if (format == Android.Graphics.ImageFormatType.Nv21 || format == Android.Graphics.ImageFormatType.Yuv420888)
            {
                // Create Java objects and immediately use them
                yuvImage = new Android.Graphics.YuvImage(rawImageData, Android.Graphics.ImageFormatType.Nv21, width, height, null);
                rect = new Android.Graphics.Rect(0, 0, width, height);
                yuvImage.CompressToJpeg(rect, quality, outputStream);
            }
            else
            {
                bitmap = Android.Graphics.BitmapFactory.DecodeByteArray(rawImageData, 0, rawImageData.Length);
                if (bitmap != null)
                {
                    bitmap.Compress(Android.Graphics.Bitmap.CompressFormat.Jpeg!, quality, outputStream);
                }
                else
                {
                    Log.Error("[RTSP]", "Failed to decode image data");
                    return Array.Empty<byte>();
                }
            }
            // Use GetBuffer + length to avoid the extra allocation from ToArray()
            var length = (int)outputStream.Length;
            var result = new byte[length];
            Buffer.BlockCopy(outputStream.GetBuffer(), 0, result, 0, length);
            return result;
        }
        catch (Exception ex)
        {
            Log.Error("[RTSP]", $"JPEG encoding error: {ex.Message}");
            return Array.Empty<byte>();
        }
        finally
        {
            // Explicitly dispose Java objects to prevent JNI crashes on background threads
            // Must dispose in finally block to ensure cleanup even on exceptions
            try { rect?.Dispose(); } catch { }
            try { yuvImage?.Dispose(); } catch { }
            try { bitmap?.Dispose(); } catch { }
        }
    }
}
