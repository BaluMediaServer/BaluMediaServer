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
        EventBuss.Command += OnCommandSend;
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

        // Wire up events
        _clientManager.OnClientsChange += clients => OnClientsChange?.Invoke(clients);
        _streamingController.StreamingStateChanged += (_, streaming) => OnStreaming?.Invoke(this, streaming);
        _streamingController.CameraStartRequested += OnCameraStartRequested;
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
    /// </summary>
    public void SetBackCameraResolution(VideoResolution resolution)
    {
        _backCameraWidth = resolution.GetWidth();
        _backCameraHeight = resolution.GetHeight();
    }

    /// <summary>
    /// Sets a custom resolution for the back camera.
    /// </summary>
    public void SetBackCameraResolution(int width, int height)
    {
        _backCameraWidth = width;
        _backCameraHeight = height;
    }

    /// <summary>
    /// Sets the resolution for the front camera.
    /// </summary>
    public void SetFrontCameraResolution(VideoResolution resolution)
    {
        _frontCameraWidth = resolution.GetWidth();
        _frontCameraHeight = resolution.GetHeight();
    }

    /// <summary>
    /// Sets a custom resolution for the front camera.
    /// </summary>
    public void SetFrontCameraResolution(int width, int height)
    {
        _frontCameraWidth = width;
        _frontCameraHeight = height;
    }

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
            ConfigureSocket();
            IsRunning = true;
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
        catch
        {
            _socket?.Close();
            _socket?.Dispose();
            _socket = CreateConfiguredSocket();
            _socket.Bind(endpoint);
            _socket.Listen(_maxClients);
        }
    }

    private void OnCommandSend(BussCommand command)
    {
        try
        {
            switch (command)
            {
                case BussCommand.START_CAMERA_FRONT:
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
                    break;
                case BussCommand.STOP_CAMERA_FRONT:
                    // NOTE: Camera stop disabled for continuous streaming to prevent interruptions
                    // To stop cameras, use the explicit Stop() method or stop the server
                    Log.Debug("[RTSP Server]", "STOP_CAMERA_FRONT command ignored - continuous streaming mode enabled");
                    break;
                case BussCommand.START_CAMERA_BACK:
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

    private void OnBackFrameAvailable(object? sender, FrameEventArgs arg)
    {
        if (arg?.Data != null && arg.Data.Length > 0)
        {
            OnNewBackFrame?.Invoke(this, arg);
            lock (_frameBackLock)
            {
                _latestBackFrame = arg;
            }
            if (_isStreaming)
            {
                _encoderManager.FeedFrame(0, arg);
            }
            // Queue for JPEG encoding (used by MJPEG clients)
            _jpegEncoder.QueueFrame(arg, 0);
        }
    }

    private void OnFrontFrameAvailable(object? sender, FrameEventArgs arg)
    {
        if (arg?.Data != null && arg.Data.Length > 0)
        {
            OnNewFrontFrame?.Invoke(this, arg);
            lock (_frameFrontLock)
            {
                _latestFrontFrame = arg;
            }
            if (_isStreaming)
            {
                _encoderManager.FeedFrame(1, arg);
            }
            // Queue for JPEG encoding (used by MJPEG clients)
            _jpegEncoder.QueueFrame(arg, 1);
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
                return _latestBackFrame;
            }
        }
    }

    private void OnCameraStartRequested(object? sender, int cameraId)
    {
        if (cameraId == 1 && !_isCapturingFront)
        {
            _frontService.StartCapture(_frontCameraWidth, _frontCameraHeight);
            _isCapturingFront = true;
        }
        else if (cameraId == 0 && !_isCapturingBack)
        {
            _backService.StartCapture(_backCameraWidth, _backCameraHeight);
            _isCapturingBack = true;
        }
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
            OnStreaming?.Invoke(this, hasAnyClients || camerasRunning);
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
                _ = Task.Run(() => HandleClient(client), _cts.Token);
            }
            catch { }
        }
    }

    private async Task HandleClient(Socket socket)
    {
        try
        {
            var client = new Client
            {
                Socket = socket,
                Id = Guid.NewGuid().ToString(),
                ConnectedAt = DateTime.UtcNow
            };
            _clientManager.AddClient(client);

            using NetworkStream stream = new(socket, true);
            using StreamReader reader = new(stream);
            using StreamWriter writer = new(stream) { AutoFlush = true };

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

            while (socket.Connected && !_cts.IsCancellationRequested)
            {
                var requestLine = await reader.ReadLineAsync().ConfigureAwait(false) ?? string.Empty;
                if (string.IsNullOrEmpty(requestLine)) continue;

                Log.Debug("[RTSP Server]", requestLine);
                var request = await protocolHandler.ParseRequestAsync(reader, requestLine).ConfigureAwait(false);
                if (request == null) continue;

                await ProcessRtspRequest(writer, request, client, protocolHandler).ConfigureAwait(false);
            }
        }
        finally
        {
            socket?.Close();
        }
    }

    private async Task ProcessRtspRequest(StreamWriter writer, RtspRequest request, Client client, RtspProtocolHandler protocolHandler)
    {
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
            case "OPTIONS":
                await protocolHandler.HandleOptionsAsync(writer, request).ConfigureAwait(false);
                break;
            case "DESCRIBE":
                await protocolHandler.HandleDescribeAsync(writer, request, client).ConfigureAwait(false);
                break;
            case "SETUP":
                await protocolHandler.HandleSetupAsync(writer, request, client).ConfigureAwait(false);
                break;
            case "PLAY":
                if (await protocolHandler.HandlePlayAsync(writer, request, client).ConfigureAwait(false))
                {
                    _isStreaming = true;
                    _ = Task.Run(() => _streamingController.StreamToClientAsync(client, _cts.Token), _cts.Token);
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
    /// </summary>
    /// <param name="rawImageData">The raw image data.</param>
    /// <param name="width">The image width.</param>
    /// <param name="height">The image height.</param>
    /// <param name="format">The image format.</param>
    /// <param name="quality">The JPEG quality (0-100).</param>
    /// <returns>The JPEG encoded data.</returns>
    public static byte[] EncodeToJpeg(byte[] rawImageData, int width, int height, Android.Graphics.ImageFormatType format, int quality = 80)
    {
        try
        {
            using var outputStream = new MemoryStream();
            if (format == Android.Graphics.ImageFormatType.Nv21 || format == Android.Graphics.ImageFormatType.Yuv420888)
            {
                var yuvImage = new Android.Graphics.YuvImage(rawImageData, Android.Graphics.ImageFormatType.Nv21, width, height, null);
                var rect = new Android.Graphics.Rect(0, 0, width, height);
                yuvImage.CompressToJpeg(rect, quality, outputStream);
            }
            else
            {
                var bitmap = Android.Graphics.BitmapFactory.DecodeByteArray(rawImageData, 0, rawImageData.Length);
                if (bitmap != null)
                {
                    bitmap.Compress(Android.Graphics.Bitmap.CompressFormat.Jpeg!, quality, outputStream);
                    bitmap.Dispose();
                }
                else
                {
                    Log.Error("[RTSP]", "Failed to decode image data");
                    return Array.Empty<byte>();
                }
            }
            return outputStream.ToArray();
        }
        catch (Exception ex)
        {
            Log.Error("[RTSP]", $"JPEG encoding error: {ex.Message}");
            return Array.Empty<byte>();
        }
    }
}
