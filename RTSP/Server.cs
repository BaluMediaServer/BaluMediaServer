using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Android.App;
using Android.Content;
using Android.Hardware;
using Android.OS;
using Android.Runtime;
using Android.Util;
using AndroidX.Core.App;
using BaluMediaServer.Models;
using BaluMediaServer.Platforms.Android.Services;
using BaluMediaServer.Repositories;

namespace BaluMediaServer.Services;

public class Server : IDisposable
{
    private FrontCameraService _frontService = new();
    private BackCameraService _backService = new();
    private MjpegServer _mjpegServer = new();
    private bool RequireAuthentication { get; set; } = true;
    private Socket _socket = default!;
    private int _port = 7778, _maxClients = 100, _nextRtpPort = 5000, _mjpegServerQuality = 80;
    private readonly object _portLock = new();
    private readonly HashSet<int> _usedPorts = new();
    private readonly CancellationTokenSource _cts = new();
    private ConcurrentBag<Client> _clients = new();
    private string _uri = string.Empty;
    private bool _isStreaming = false, _enabled = false, _isCapturingFront = false, _isCapturingBack = false,
    _mjpegServerEnabled = false, _frontCameraEnabled = true, _backCameraEnabled = true, _authRequired = true;
    private FrameEventArgs? _latestFrontFrame, _latestBackFrame;
    private readonly ConcurrentDictionary<string, string> _nonceCache = new();
    private readonly string _address = string.Empty;
    private readonly TimeSpan _nonceExpiry = TimeSpan.FromMinutes(5);
    private readonly object _frameFrontLock = new(), _frameBackLock = new(), _h264FrontLock = new(), _h264BackLock = new();
    private MediaTekH264Encoder? _h264FrontEncoder, _h264BackEncoder;
    private H264FrameEventArgs? _latestH264FrameFront, _latestH264FrameBack;
    private List<VideoProfile> _videoProfiles = new();
    private readonly Dictionary<string, byte[]?> _clientSpsCache = new(), _clientPpsCache = new();
    public static event Action<List<Client>>? OnClientsChange;
    public static event EventHandler<bool>? OnStreaming;
    public static event EventHandler<FrameEventArgs>? OnNewFrontFrame, OnNewBackFrame;
    
    private Dictionary<string, string> _users = new()
    {
        { "admin", "password123" }
    };
    public Server(int Port = 7778, int MaxClients = 100, string Address = "0.0.0.0", Dictionary<string, string>? Users = null, bool BackCameraEnabled = true, bool FrontCameraEnabled = true, bool AuthRequired = true, int MjpegServerQuality = 80)
    {
        EventBuss.Command += OnCommandSend;
        _enabled = true;
        _port = Port;
        _maxClients = MaxClients;
        _users = Users == null ? _users : Users;
        _mjpegServerQuality = MjpegServerQuality;
        _mjpegServer = new(quality: _mjpegServerQuality);
        _authRequired = AuthRequired;
        _frontCameraEnabled = FrontCameraEnabled;
        _backCameraEnabled = BackCameraEnabled;
        _address = Address;
        ConfigureSocket();
    }
    private void ConfigureSocket()
    {
        IPEndPoint endpoint = new(IPAddress.Parse(_address), _port);
        _socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, true); // Disable Nagle's
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.SendBuffer, 65536); // Increase send buffer
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer, 65536);
        _socket.Bind(endpoint);
        _socket.Listen(_maxClients);
    }
    public void SetVideoProfile(VideoProfile profile) => _videoProfiles.Add(profile);
    private void OnCommandSend(BussCommand command)
    {
        switch (command)
        {
            case BussCommand.START_CAMERA_FRONT:
                if (!_isCapturingFront && _frontCameraEnabled)
                {
                    _frontService.StartCapture();
                    _isCapturingFront = true;
                }
                break;
            case BussCommand.STOP_CAMERA_FRONT:
                if (!_isStreaming && _isCapturingFront)
                {
                    _frontService.StopCapture();
                    _isCapturingFront = false;
                }
                break;
            case BussCommand.START_CAMERA_BACK:
                if (!_isCapturingBack && _backCameraEnabled)
                {
                    _backService.StartCapture();
                    _isCapturingBack = true;
                }
                break;
            case BussCommand.STOP_CAMERA_BACK:
                if (!_isStreaming && _isCapturingBack)
                {
                    _backService.StopCapture();
                    _isCapturingBack = false;
                }
                break;
            case BussCommand.START_MJPEG_SERVER:
                if (!_mjpegServerEnabled)
                {
                    _mjpegServer.Start();
                }
                break;
            case BussCommand.STOP_MJPEG_SERVER:
                if (_mjpegServerEnabled)
                {
                    _mjpegServer.Stop();
                }
                break;
            case BussCommand.SWITCH_CAMERA:
                _mjpegServer?.Dispose();
                _mjpegServer = new(quality: _mjpegServerQuality);
                _socket?.Close();
                ConfigureSocket();
                if (_frontCameraEnabled)
                {
                    _frontCameraEnabled = false;
                    _backCameraEnabled = true;
                }
                else if (_backCameraEnabled)
                {
                    _backCameraEnabled = false;
                    _frontCameraEnabled = true;
                }
                break;
            default:
                break;
        }
    }
    private void OnBackFrameAvailable(object? sender, FrameEventArgs arg)
    {
        if (arg != null && arg.Data != null && arg.Data.Length > 0)
        {
            OnNewBackFrame?.Invoke(this, arg);
            lock (_frameBackLock)
            {
                _latestBackFrame = arg;
            }
            if (_h264BackEncoder != null && _isStreaming)
            {
                // Use camera timestamp directly
                _h264BackEncoder.FeedInputBuffer(new() { Data = arg.Data, Timestamp = arg.Timestamp });
            }
        }
    }
    private void OnFrontFrameAvailable(object? sender, FrameEventArgs arg)
    {
        if (arg != null && arg.Data != null && arg.Data.Length > 0)
        {
            OnNewFrontFrame?.Invoke(this, arg);
            lock (_frameFrontLock)
            {
                _latestFrontFrame = arg;
            }
            if (_h264FrontEncoder != null && _isStreaming)
            {
                // Use camera timestamp directly
                _h264FrontEncoder.FeedInputBuffer(new() { Data = arg.Data, Timestamp = arg.Timestamp });
            }
        }
    }
    private void OnH264FrontFrameEncoded(object? sender, H264FrameEventArgs e)
    {
        lock (_h264FrontLock)
        {
            _latestH264FrameFront = e;
        }
    }
    private void OnH264BackFrameEncoded(object? sender, H264FrameEventArgs e)
    {
        lock (_h264BackLock)
        {
            _latestH264FrameBack = e;
        }
    }
    private void StartH264EncoderFront(int width, int height)
    {
        lock (_h264FrontLock)
        {
            if (_h264FrontEncoder == null)
            {
                _h264FrontEncoder = new(width, height, bitrate: 2000000, frameRate: 25);
                _h264FrontEncoder.FrameEncoded += OnH264FrontFrameEncoded;
                _h264FrontEncoder.Start();
                Log.Debug("[RTSP Server]", "H264 front encoder started");
            }
        }
    }
    private void StartH264EncoderBack(int width, int height)
    {
        lock (_h264BackLock)
        {
            if (_h264BackEncoder == null)
            {
                _h264BackEncoder = new(width, height, bitrate: 2000000, frameRate: 25);
                _h264BackEncoder.FrameEncoded += OnH264BackFrameEncoded;
                _h264BackEncoder.Start();
                Log.Debug("[RTSP Server]", "H264 encoder started");
            }
        }
    }
    private void StopH264EncoderFront()
    {
        lock (_h264FrontLock)
        {
            if (_h264FrontEncoder != null)
            {
                _h264FrontEncoder.FrameEncoded -= OnH264FrontFrameEncoded;
                _h264FrontEncoder.Stop();
                _h264FrontEncoder.Dispose();
                _h264FrontEncoder = null;
                Log.Debug("[RTSP Server]", "H264 front encoder stopped");
            }
        }
    }
    private void StopH264EncoderBack()
    {
        lock (_h264BackLock)
        {
            if (_h264BackEncoder != null)
            {
                _h264BackEncoder.FrameEncoded -= OnH264BackFrameEncoded;
                _h264BackEncoder.Stop();
                _h264BackEncoder.Dispose();
                _h264BackEncoder = null;
                Log.Debug("[RTSP Server]", "H264 back encoder stopped");
            }
        }
    }
    public void Stop() => Dispose();
    public bool Start()
    {
        if (_enabled)
        {
            _backService.FrameReceived += OnBackFrameAvailable;
            _frontService.FrameReceived += OnFrontFrameAvailable;
            Task.Run(ListenAsync, _cts.Token);
            Task.Run(WatchDog, _cts.Token);
            return true;
        }
        return false;
    }
    private async Task WatchDog()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                OnClientsChange?.Invoke(_clients.ToList());
                var connected = _clients.Count(p => p.Socket?.Connected ?? false);
                if (connected == 0 && _isStreaming)
                {
                    if (!_mjpegServerEnabled)
                    {
                        if (!_isCapturingBack)
                            _backService.StopCapture();
                        if (!_isCapturingFront)
                            _frontService.StopCapture();
                    }
                    StopH264EncoderBack();
                    StopH264EncoderFront();
                    lock (_clients)
                    {
                        _clients.Clear();
                    }
                    _isStreaming = false;
                }
            }
            catch
            {

            }
            OnStreaming?.Invoke(this, _isStreaming);
            await Task.Delay(1000, _cts.Token);
        }
    }
    public void Dispose()
    {
        _mjpegServer?.Dispose();
        _cts?.Cancel();
        _socket?.Dispose();
    }
    public async Task ListenAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            var client = await _socket.AcceptAsync(_cts.Token);
            _ = Task.Run(() => HandleClient(client), _cts.Token);
        }
    }
    private async Task HandleClient(Socket client)
    {
        try
        {
            var clientSession = new Client
            {
                Socket = client,
                Id = Guid.NewGuid().ToString(),
                ConnectedAt = DateTime.UtcNow
            };
            _clients.Add(clientSession);
            using NetworkStream stream = new(client, true);
            using StreamReader reader = new(stream);
            using StreamWriter writer = new(stream) { AutoFlush = true };

            while (client.Connected && !_cts.IsCancellationRequested)
            {
                var requestLine = await reader.ReadLineAsync() ?? string.Empty;
                if (string.IsNullOrEmpty(requestLine)) continue;
                Log.Debug("[RTSP Server]", requestLine);
                var request = await ParseRtspRequest(reader, requestLine);
                if (request == null) continue;
                await ProcessRtspRequest(writer, request, clientSession);
            }
        }
        finally
        {
            client?.Close();
        }
    }
    private async Task<RtspRequest?> ParseRtspRequest(StreamReader reader, string requestLine)
    {
        var parts = requestLine.Split(' ');
        if (parts.Length != 3) return null;
        var request = new RtspRequest
        {
            Method = parts[0],
            Uri = parts[1],
            Version = parts[2]
        };
        string? line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_cts.Token)))
        {
            var colonIndex = line.IndexOf(':');
            if (colonIndex > 0)
            {
                var key = line.Substring(0, colonIndex).Trim();
                var value = line.Substring(colonIndex + 1).Trim();
                request.Headers[key] = value;
                if (key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                {
                    request.Auth = ParseAuthorization(value, request.Method, request.Uri);
                }
            }
        }
        if (request.Headers.TryGetValue("Content-Length", out var lengthStr) &&
            int.TryParse(lengthStr, out var length) && length > 0)
        {
            var buffer = new char[length];
            await reader.ReadAsync(buffer, 0, length);
            request.Body = new string(buffer);
        }
        return request;
    }
    private RtspAuth? ParseAuthorization(string authHeader, string method, string uri)
    {
        if (string.IsNullOrWhiteSpace(authHeader))
            return null;

        var auth = new RtspAuth 
        { 
            Method = method, 
            Uri = uri,
            RawHeader = authHeader // Store the raw header
        };

        if (authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            auth.Type = AuthType.Basic;
            var base64 = authHeader.Substring(6).Trim();
            
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                var colonIndex = decoded.IndexOf(':');
                if (colonIndex > 0)
                {
                    auth.Username = decoded.Substring(0, colonIndex);
                    auth.Password = decoded.Substring(colonIndex + 1);
                }
            }
            catch (Exception ex)
            {
                Log.Error("[RTSP Auth]", $"Failed to decode Basic auth: {ex.Message}");
                return null;
            }
        }
        else if (authHeader.StartsWith("Digest ", StringComparison.OrdinalIgnoreCase))
        {
            auth.Type = AuthType.Digest;
            var digestParams = ParseDigestAuth(authHeader.Substring(7));
            auth.DigestParams = digestParams; // Store parsed parameters
            
            auth.Username = digestParams.GetValueOrDefault("username")?.Trim('"');
            auth.Realm = digestParams.GetValueOrDefault("realm")?.Trim('"');
            auth.Nonce = digestParams.GetValueOrDefault("nonce")?.Trim('"');
        }

        return auth;
    }
    private Dictionary<string, string> ParseDigestAuth(string digest)
    {
        var parameters = new Dictionary<string, string>();
        var parts = digest.Split(',');
        
        foreach (var part in parts)
        {
            var eqIndex = part.IndexOf('=');
            if (eqIndex > 0)
            {
                var key = part.Substring(0, eqIndex).Trim();
                var value = part.Substring(eqIndex + 1).Trim();
                parameters[key] = value;
            }
        }
        
        return parameters;
    }
    private async Task SendRtspResponse(StreamWriter writer, int statusCode, string statusText,
        int cseq, Dictionary<string, string>? headers = null, string? body = null)
    {
        await writer.WriteLineAsync($"RTSP/1.0 {statusCode} {statusText}");
        await writer.WriteLineAsync($"CSeq: {cseq}");

        if (headers != null)
        {
            foreach (var header in headers)
            {
                await writer.WriteLineAsync($"{header.Key}: {header.Value}");
            }
        }

        if (!string.IsNullOrEmpty(body))
        {
            await writer.WriteLineAsync($"Content-Length: {body.Length}");
            await writer.WriteLineAsync($"Content-Type: application/sdp");
        }

        await writer.WriteLineAsync();

        if (!string.IsNullOrEmpty(body))
        {
            await writer.WriteAsync(body);
        }
    }
    private async Task HandleOptions(StreamWriter writer, RtspRequest request)
    {
        var headers = new Dictionary<string, string>
        {
            ["Public"] = "OPTIONS, DESCRIBE, SETUP, PLAY, TEARDOWN"
        };

        await SendRtspResponse(writer, 200, "OK", request.CSeq, headers);
    }
    private async Task HandleUriResponses(string uri, StreamWriter writer, RtspRequest request, Client client)
    {
        if (!uri.Contains("/live"))
        {
            await SendRtspResponse(writer, 404, "Not Found", request.CSeq);
            return;
        }

        if (uri.Contains("/live/front") && !_frontCameraEnabled)
        {
            await SendRtspResponse(writer, 400, "Front Camera not enabled", request.CSeq);
        }
        else if ((uri.Contains("/live/back") || uri.Contains("/live")) && !_backCameraEnabled)
        {
            await SendRtspResponse(writer, 400, "Back Camera not enabled", request.CSeq);
        }

        if (uri.Contains("/mjpeg"))
        {
            client.Codec = CodecType.MJPEG;
        }
        else
        {
            client.Codec = CodecType.H264;
        }

        if (uri.Contains("/live/front"))
        {
            client.CameraId = 1; // FRONT CAMERA
        }
        else
        {
            client.CameraId = 0; // BACK CAMERA
        }
    }
    private async Task ProcessRtspRequest(StreamWriter writer, RtspRequest request, Client client)
    {
        if (!IsAuthenticated(request) && _authRequired)
        {
            await SendAuthenticationRequired(writer, request.CSeq);
            return;
        }

        var uri = new Uri(request.Uri);

        await HandleUriResponses(uri.AbsolutePath, writer, request, client);

        _uri = uri.AbsolutePath;

        switch (request.Method.ToUpper())
        {
            case "OPTIONS":
                await HandleOptions(writer, request);
                break;
            case "DESCRIBE":
                await HandleDescribe(writer, request, client);
                break;
            case "SETUP":
                await HandleSetup(writer, request, client);
                break;
            case "PLAY":
                await HandlePlay(writer, request, client);
                break;
            case "TEARDOWN":
                await HandleTeardown(writer, request, client);
                break;
            default:
                await SendRtspResponse(writer, 405, "Method Not Allowed", request.CSeq);
                break;
        }
    }
    private bool IsAuthenticated(RtspRequest request)
    {
        
        if (!RequireAuthentication)
            return true;

        if (request.Auth == null)
            return false;

        
        return ValidateCredentials(request.Auth);
    }
    private bool ValidateCredentials(RtspAuth auth)
    {
        if (auth == null || string.IsNullOrEmpty(auth.Username))
            return false;

        // Check if user exists
        if (!_users.TryGetValue(auth.Username, out var validPassword))
            return false;

        if (auth.Type == AuthType.Basic)
        {
            return auth.Password == validPassword;
        }
        else if (auth.Type == AuthType.Digest)
        {
            return ValidateDigestAuth(auth, validPassword);
        }

        return false;
    }
    private async Task SendAuthenticationRequired(StreamWriter writer, int cseq)
    {
        var nonce = GenerateNonce();
        var headers = new Dictionary<string, string>
        {
            ["WWW-Authenticate"] = $"Digest realm=\"RTSP Server\", nonce=\"{nonce}\", algorithm=MD5"
            
            
        };

        await SendRtspResponse(writer, 401, "Unauthorized", cseq, headers);
    }
    private string GenerateNonce()
    {
        var bytes = new byte[16];
        using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }
        var nonce = Convert.ToBase64String(bytes);

        // Store nonce with timestamp
        if(!_nonceCache.ContainsKey(nonce))
            _nonceCache[nonce] = DateTime.UtcNow.Add(_nonceExpiry).ToString("O");
        
        // Clean old nonces
        CleanExpiredNonces();
        
        return nonce;
    }
    private bool IsNonceValid(string nonce)
    {
        if (!_nonceCache.TryGetValue(nonce, out var expiryStr))
            return false;

        if (DateTimeOffset.TryParse(expiryStr, out var expiry))
        {

            return DateTimeOffset.UtcNow < expiry;
        }

        return false;
    }
    private void CleanExpiredNonces()
    {
        var now = DateTime.UtcNow;
        var expiredNonces = _nonceCache
            .Where(kvp => DateTime.TryParse(kvp.Value, out var expiry) && expiry > now)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var nonce in expiredNonces) _nonceCache.TryRemove(nonce, out _);
    }
    private bool ValidateDigestAuth(RtspAuth auth, string password)
    {
        if (string.IsNullOrEmpty(auth.Username) || string.IsNullOrEmpty(auth.Nonce))
            return false;

        try
        {
            // Check if this is a nonce we generated
            if (!_nonceCache.ContainsKey(auth.Nonce))
            {
                Log.Debug("[RTSP Auth]", $"Unknown nonce: {auth.Nonce}");
                return false;
            }

            // Check if nonce is expired
            if (!IsNonceValid(auth.Nonce))
            {
                Log.Debug("[RTSP Auth]", "Nonce expired");
                _nonceCache.TryRemove(auth.Nonce, out _);
                return false;
            }

            // Get the response from the parsed digest parameters
            var providedResponse = auth.DigestParams?.GetValueOrDefault("response")?.Trim('"');
            
            if (string.IsNullOrEmpty(providedResponse))
            {
                Log.Debug("[RTSP Auth]", "No response in digest auth");
                return false;
            }

            // Calculate expected response
            using var md5 = System.Security.Cryptography.MD5.Create();
            
            // HA1 = MD5(username:realm:password)
            var realm = auth.Realm ?? "RTSP Server";
            var ha1Input = $"{auth.Username}:{realm}:{password}";
            var ha1Bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(ha1Input));
            var ha1 = BitConverter.ToString(ha1Bytes).Replace("-", "").ToLower();
            
            // HA2 = MD5(method:uri)
            var ha2Input = $"{auth.Method}:{auth.Uri}";
            var ha2Bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(ha2Input));
            var ha2 = BitConverter.ToString(ha2Bytes).Replace("-", "").ToLower();
            
            // Check if qop is present
            var qop = auth.DigestParams?.GetValueOrDefault("qop")?.Trim('"');
            var nc = auth.DigestParams?.GetValueOrDefault("nc")?.Trim('"');
            var cnonce = auth.DigestParams?.GetValueOrDefault("cnonce")?.Trim('"');
            
            string expectedResponse;
            if (!string.IsNullOrEmpty(qop) && qop == "auth")
            {
                // With qop: Response = MD5(HA1:nonce:nc:cnonce:qop:HA2)
                var responseInput = $"{ha1}:{auth.Nonce}:{nc}:{cnonce}:{qop}:{ha2}";
                var responseBytes = md5.ComputeHash(Encoding.UTF8.GetBytes(responseInput));
                expectedResponse = BitConverter.ToString(responseBytes).Replace("-", "").ToLower();
            }
            else
            {
                // Without qop: Response = MD5(HA1:nonce:HA2)
                var responseInput = $"{ha1}:{auth.Nonce}:{ha2}";
                var responseBytes = md5.ComputeHash(Encoding.UTF8.GetBytes(responseInput));
                expectedResponse = BitConverter.ToString(responseBytes).Replace("-", "").ToLower();
            }
            
            var isValid = expectedResponse.Equals(providedResponse, StringComparison.OrdinalIgnoreCase);
            
            if (isValid)
            {
                Log.Debug("[RTSP Auth]", $"User {auth.Username} authenticated successfully");
            }
            else
            {
                Log.Debug("[RTSP Auth]", $"Authentication failed for user {auth.Username}");
                Log.Debug("[RTSP Auth]", $"Expected: {expectedResponse}, Got: {providedResponse}");
            }
            
            return isValid;
        }
        catch (Exception ex)
        {
            Log.Error("[RTSP Auth]", $"Error validating digest auth: {ex.Message}");
            return false;
        }
    }
    private async Task HandleDescribe(StreamWriter writer, RtspRequest request, Client client)
    {
        lock (client)
        {
            client.Codec = request.Uri.Contains("mjpeg") ? CodecType.MJPEG : CodecType.H264;    
        }
        var sdp = GenerateSDP(client.Codec);
        var headers = new Dictionary<string, string>
        {
            ["Content-Base"] = request.Uri
        };

        await SendRtspResponse(writer, 200, "OK", request.CSeq, headers, sdp);
    }
    private async Task HandleTeardown(StreamWriter writer, RtspRequest request, Client client)
    {
        if (string.IsNullOrEmpty(client.SessionId))
        {
            await SendRtspResponse(writer, 454, "Session Not Found", request.CSeq);
            return;
        }
        lock (client)
        {
            client.IsPlaying = false;    
        }
        var responseHeaders = new Dictionary<string, string>
        {
            ["Session"] = client.SessionId
        };
        
        await SendRtspResponse(writer, 200, "OK", request.CSeq, responseHeaders);
    }
    private async Task HandlePlay(StreamWriter writer, RtspRequest request, Client client)
    {
        if (string.IsNullOrEmpty(client.SessionId))
        {
            await SendRtspResponse(writer, 454, "Session Not Found", request.CSeq);
            return;
        }

        var responseHeaders = new Dictionary<string, string>
        {
            ["Session"] = client.SessionId,
            ["RTP-Info"] = $"url={request.Uri}/track0;seq=0;rtptime=0"
        };

        await SendRtspResponse(writer, 200, "OK", request.CSeq, responseHeaders);
        lock (client)
        {
            client.IsPlaying = true;    
        }
        _ = Task.Run(() => StreamToClient(client), _cts.Token);
    }
    private async Task StreamToClient(Client client)
    {
        Log.Debug("[RTSP Server]", $"Starting stream to client {client.Id} using {client.Transport}");
        if (!_isStreaming)
        {
            if (!_isCapturingFront && client.CameraId == 1)
            {
                _frontService.StartCapture();
                _isCapturingFront = true;
            }
            else if (!_isCapturingBack && client.CameraId == 0)
            {
                _backService.StartCapture();
                _isCapturingBack = true;
            }
            
            _isStreaming = true;
            if (client.Codec == CodecType.H264)
            {
                if (client.CameraId == 0)
                {
                    while (_latestBackFrame == null) await Task.Delay(50, _cts.Token);
                    StartH264EncoderBack(_latestBackFrame.Width, _latestBackFrame.Height);
                }
                else
                {
                    while (_latestFrontFrame == null) await Task.Delay(50, _cts.Token);
                    StartH264EncoderFront(_latestFrontFrame.Width, _latestFrontFrame.Height);
                }
            }
        }
        else
        {
            OnStreaming?.Invoke(this, _isStreaming);
        }

        // Initialize RTP timestamp with random value
        lock (client)
        {
            client.RtpTimestamp = (uint)Random.Shared.Next(0, int.MaxValue);
            client.SequenceNumber = (ushort)Random.Shared.Next(0, ushort.MaxValue);
            client.LastRtpTime = DateTime.UtcNow;
        }
        
        
        const int frameIntervalMs = 40; // 25fps = 40ms per frame
        
        try
        {
            while (client.IsPlaying && client.Socket.Connected && !_cts.IsCancellationRequested)
            {
                var frameStart = DateTime.UtcNow;

                if (client.Codec == CodecType.H264)
                {
                    await StreamH264ToClient(client);
                }
                else
                {
                    await StreamMjpegToClient(client);
                }
                // Calculate actual time elapsed since last frame
                var elapsed = (DateTime.UtcNow - frameStart).TotalMilliseconds;
                var waitTime = frameIntervalMs - (int)elapsed;

                if (waitTime > 0)
                {
                    await Task.Delay(waitTime);
                }

                // Update RTP timestamp based on actual time elapsed
                var rtpElapsed = (DateTime.UtcNow - client.LastRtpTime).TotalMilliseconds;
                lock (client)
                {
                    client.RtpTimestamp += (uint)(rtpElapsed * 90); // 90kHz clock
                    client.LastRtpTime = DateTime.UtcNow;    
                }
                
            }
        }
        catch (Exception ex)
        {
            Log.Error("[RTSP Server]", $"Streaming error: {ex.Message}");
        }
        finally
        {
            CleanupClient(client);
        }
    }
    private async Task StreamMjpegToClient(Client client)
    {
        FrameEventArgs? frame = null;
        if (client.CameraId == 0)
        {
            lock (_frameBackLock)
            {
                frame = _latestBackFrame;
            }
        }
        else
        {
            lock (_frameFrontLock)
            {
                frame = _latestFrontFrame;
            }
        }
        

        if (frame != null && frame.Data != null && frame.Data.Length > 0)
        {
            try
            {
                var jpegData = EncodeToJpeg(frame.Data, frame.Width, frame.Height, Android.Graphics.ImageFormatType.Nv21, client.VideoProfile.Quality);

                if (jpegData != null && jpegData.Length > 0)
                {
                    await SendJpegAsRtp(client, jpegData);
                }
            }
            catch (Exception ex)
            {
                Log.Error("[RTSP Server]", $"MJPEG frame encoding error: {ex.Message}");
            }
        }
    }
    private async Task StreamH264ToClient(Client client)
    {
        H264FrameEventArgs? h264Frame = null;
        
        if (client.CameraId == 0)
        {
            lock (_h264BackLock)
            {
                h264Frame = _latestH264FrameBack;
            }
        }
        else
        {
            lock (_h264FrontLock)
            {
                h264Frame = _latestH264FrameFront;
            }
        }
        
        if (h264Frame != null && h264Frame.NalUnits.Count > 0)
        {
            // Check if this is a new frame
            if (h264Frame.Timestamp > client.LastH264FrameTimestamp)
            {
                lock (client)
                {
                    client.LastH264FrameTimestamp = h264Frame.Timestamp;
                    client.FrameCount++;
                }


                try
                {
                    // Send SPS/PPS if needed
                    bool needsSpsPps = h264Frame.IsKeyFrame ||
                                    client.FrameCount == 1 ||
                                    !_clientSpsCache.ContainsKey(client.Id);

                    if (needsSpsPps && h264Frame.Sps != null && h264Frame.Pps != null)
                    {
                        await SendH264NalAsRtp(client, h264Frame.Sps);
                        await SendH264NalAsRtp(client, h264Frame.Pps);

                        _clientSpsCache[client.Id] = h264Frame.Sps;
                        _clientPpsCache[client.Id] = h264Frame.Pps;
                    }

                    // Send all NAL units
                    foreach (var nalUnit in h264Frame.NalUnits)
                        await SendH264NalAsRtp(client, nalUnit);
                }
                catch (Exception ex)
                {
                    Log.Error("[RTSP Server]", $"H264 streaming error: {ex.Message}");
                }
            }
        }
    }
    private int GetAvailablePort()
    {
        lock (_portLock)
        {
            // Find next available even port (RTP uses even, RTCP uses odd)
            while (_usedPorts.Contains(_nextRtpPort) || _usedPorts.Contains(_nextRtpPort + 1))
            {
                _nextRtpPort += 2;
                
                // Wrap around if we reach the upper limit
                if (_nextRtpPort > 65000)
                {
                    _nextRtpPort = 5000;
                }
            }
            
            // Reserve both RTP and RTCP ports
            _usedPorts.Add(_nextRtpPort);
            _usedPorts.Add(_nextRtpPort + 1);
            
            var port = _nextRtpPort;
            _nextRtpPort += 2;
            
            return port;
        }
    }
    private void ReleaseClientPorts(Client client)
    {
        lock (_portLock)
        {
            if (client.RtpEndPoint != null)
            {
                var rtpPort = client.RtpEndPoint.Port;
                if (rtpPort > 0)
                {
                    _usedPorts.Remove(rtpPort);
                    _usedPorts.Remove(rtpPort + 1);
                }
            }
        }
    }
    private async Task SendJpegAsRtp(Client client, byte[] jpegData)
    {
        const int maxPayloadSize = 1400;
        int offset = 0;
        
        while (offset < jpegData.Length && !_cts.IsCancellationRequested)
        {
            int fragmentOffset = offset;
            int payloadSize = Math.Min(maxPayloadSize - 8, jpegData.Length - offset);
            bool isLastFragment = (offset + payloadSize) >= jpegData.Length;
            
            var payload = new byte[8 + payloadSize];
            // Type-specific header (8 bytes)
            payload[0] = 0; // Type-specific
            payload[1] = (byte)((fragmentOffset >> 16) & 0xFF);
            payload[2] = (byte)((fragmentOffset >> 8) & 0xFF);
            payload[3] = (byte)(fragmentOffset & 0xFF);
            payload[4] = 1; // Type (1 for standard quantization tables)
            payload[5] = 255; // Q value (255 = dynamic tables)
            
            // Width and height must be in 8-pixel units (not divided by 8)
            // But the RTP JPEG header expects values in 8-pixel blocks
            // So if width=1280, we send 160 (1280/8)
            var widthInBlocks = (byte)(client.Width / 8);
            var heightInBlocks = (byte)(client.Height / 8);
            
            // Ensure we don't send 0 dimensions
            if (widthInBlocks == 0) widthInBlocks = 160; // Default 1280/8
            if (heightInBlocks == 0) heightInBlocks = 90;  // Default 720/8
            
            payload[6] = widthInBlocks;
            payload[7] = heightInBlocks;
            
            if (offset == 0)
            {
                var qtHeader = new byte[4];
                qtHeader[0] = 0;
                qtHeader[1] = 0;
                qtHeader[2] = 0;
                qtHeader[3] = 128;
                
                var newPayload = new byte[8 + 4 + 128 + payloadSize];
                Buffer.BlockCopy(payload, 0, newPayload, 0, 8);
                Buffer.BlockCopy(qtHeader, 0, newPayload, 8, 4);
                var qtData = GetStandardQuantizationTables();
                Buffer.BlockCopy(qtData, 0, newPayload, 12, 128);
                Buffer.BlockCopy(jpegData, offset, newPayload, 140, payloadSize);
                payload = newPayload;
                payload[4] = 0;
            }
            else
            {
                Buffer.BlockCopy(jpegData, offset, payload, 8, payloadSize);
            }
            
            var rtpPacket = CreateRtpPacketOld(client, payload, isLastFragment, 26);

            // Send via appropriate transport
            await SendData(client, rtpPacket);
            lock (client)
            {
                client.SequenceNumber++;
            }
            offset += payloadSize;
        }
    }
    private async Task SendData(Client client, byte[] data)
    {
        if (client.Transport == TransportMode.UDP)
        {
            await SendUdpData(client, data, false);
        }
        else if (client.Transport == TransportMode.TCPInterleaved)
        {
            await SendInterleavedData(client.Socket, client.RtpChannel, data);
        }
    }
    private async Task SendH264NalAsRtp(Client client, byte[] nalUnit)
    {
        const int maxPayloadSize = 1400;
        uint nalTimestamp;
        lock (client)
        {
            nalTimestamp = client.RtpTimestamp;
        }
        int nalStart = 0;
        if (nalUnit.Length >= 4 && nalUnit[0] == 0 && nalUnit[1] == 0 && nalUnit[2] == 0 && nalUnit[3] == 1)
        {
            nalStart = 4;
        }
        else if (nalUnit.Length >= 3 && nalUnit[0] == 0 && nalUnit[1] == 0 && nalUnit[2] == 1)
        {
            nalStart = 3;
        }
        
        int nalLength = nalUnit.Length - nalStart;

        if (nalLength <= maxPayloadSize)
        {
            var payload = new byte[nalLength];
            Buffer.BlockCopy(nalUnit, nalStart, payload, 0, nalLength);

            var rtpPacket = CreateRtpPacket(client, payload, nalTimestamp, true, 96);

            // Send via appropriate transport
            await SendData(client, rtpPacket);

            lock (client)
            {
                client.SequenceNumber++;
                if (client.SequenceNumber > 65535)
                    client.SequenceNumber = 0;
            }
        }
        else
        {
            // FU-A fragmentation
            byte nalHeader = nalUnit[nalStart];
            byte nalType = (byte)(nalHeader & 0x1F);
            byte nalNri = (byte)(nalHeader & 0x60);

            int dataOffset = nalStart + 1;
            int remainingData = nalLength - 1;
            bool isFirstFragment = true;

            while (remainingData > 0 && !_cts.IsCancellationRequested)
            {
                int fragmentSize = Math.Min(maxPayloadSize - 2, remainingData);
                bool isLastFragment = fragmentSize == remainingData;

                var payload = new byte[fragmentSize + 2];
                payload[0] = (byte)(nalNri | 28); // FU-A
                payload[1] = nalType;
                if (isFirstFragment) payload[1] |= 0x80; // Start bit
                if (isLastFragment) payload[1] |= 0x40;  // End bit

                Buffer.BlockCopy(nalUnit, dataOffset, payload, 2, fragmentSize);

                var rtpPacket = CreateRtpPacket(client, payload, nalTimestamp, isLastFragment, 96);

                // Send via appropriate transport
                await SendData(client, rtpPacket);
                lock (client)
                {
                    client.SequenceNumber++;
                    if (client.SequenceNumber > 65535)
                        client.SequenceNumber = 0;
                }
                dataOffset += fragmentSize;
                remainingData -= fragmentSize;
                isFirstFragment = false;
            }
        }
        
    }
    private byte[] GetStandardQuantizationTables()
    {
        byte[] tables = new byte[128];
        byte[] luma = {
            16, 11, 10, 16, 24, 40, 51, 61,
            12, 12, 14, 19, 26, 58, 60, 55,
            14, 13, 16, 24, 40, 57, 69, 56,
            14, 17, 22, 29, 51, 87, 80, 62,
            18, 22, 37, 56, 68, 109, 103, 77,
            24, 35, 55, 64, 81, 104, 113, 92,
            49, 64, 78, 87, 103, 121, 120, 101,
            72, 92, 95, 98, 112, 100, 103, 99
        };
        byte[] chroma = {
            17, 18, 24, 47, 99, 99, 99, 99,
            18, 21, 26, 66, 99, 99, 99, 99,
            24, 26, 56, 99, 99, 99, 99, 99,
            47, 66, 99, 99, 99, 99, 99, 99,
            99, 99, 99, 99, 99, 99, 99, 99,
            99, 99, 99, 99, 99, 99, 99, 99,
            99, 99, 99, 99, 99, 99, 99, 99,
            99, 99, 99, 99, 99, 99, 99, 99
        };
        Buffer.BlockCopy(luma, 0, tables, 0, 64);
        Buffer.BlockCopy(chroma, 0, tables, 64, 64);
        return tables;
    }
    private byte[] CreateRtpPacketOld(Client client, byte[] payload, bool marker, byte payloadType)
    {
        var packet = new byte[12 + payload.Length];
        packet[0] = 0x80; // V=2, P=0, X=0, CC=0
        packet[1] = (byte)(marker ? 0x80 | payloadType : payloadType);

        lock (client)
        {
            packet[2] = (byte)(client.SequenceNumber >> 8);
            packet[3] = (byte)(client.SequenceNumber & 0xFF);
            
            // Timestamp (client-specific)
            packet[4] = (byte)(client.RtpTimestamp >> 24);
            packet[5] = (byte)(client.RtpTimestamp >> 16);
            packet[6] = (byte)(client.RtpTimestamp >> 8);
            packet[7] = (byte)(client.RtpTimestamp & 0xFF);
            
            // SSRC (client-specific)
            packet[8] = (byte)(client.SsrcId >> 24);
            packet[9] = (byte)(client.SsrcId >> 16);
            packet[10] = (byte)(client.SsrcId >> 8);
            packet[11] = (byte)(client.SsrcId & 0xFF);
        }
        // Sequence number (client-specific)
        Buffer.BlockCopy(payload, 0, packet, 12, payload.Length);
        return packet;
    }
    private byte[] CreateRtpPacket(Client client, byte[] payload, uint timestamp, bool marker, byte payloadType)
    {
        var packet = new byte[12 + payload.Length];
        packet[0] = 0x80;
        packet[1] = (byte)(marker ? 0x80 | payloadType : payloadType);

        // Sequence number (must be locked when reading)
        ushort seqNum;
        lock (client)
        {
            seqNum = client.SequenceNumber;
        }
        packet[2] = (byte)(seqNum >> 8);
        packet[3] = (byte)(seqNum & 0xFF);

        // Use the provided timestamp for all fragments
        packet[4] = (byte)(timestamp >> 24);
        packet[5] = (byte)(timestamp >> 16);
        packet[6] = (byte)(timestamp >> 8);
        packet[7] = (byte)(timestamp & 0xFF);

        // SSRC
        packet[8] = (byte)(client.SsrcId >> 24);
        packet[9] = (byte)(client.SsrcId >> 16);
        packet[10] = (byte)(client.SsrcId >> 8);
        packet[11] = (byte)(client.SsrcId & 0xFF);

        Buffer.BlockCopy(payload, 0, packet, 12, payload.Length);
        return packet;
    }
    private async Task SendInterleavedData(Socket socket, byte channel, byte[] rtpPacket)
    {
        var frame = new byte[4 + rtpPacket.Length];
        frame[0] = 0x24; // $ magic byte
        frame[1] = channel;
        frame[2] = (byte)(rtpPacket.Length >> 8);
        frame[3] = (byte)(rtpPacket.Length & 0xFF);
        Buffer.BlockCopy(rtpPacket, 0, frame, 4, rtpPacket.Length);
        
        try
        {
            if(socket?.Connected ?? false)
                await socket.SendAsync(frame, SocketFlags.None, _cts.Token);
        }
        catch (Exception ex)
        {
            Log.Error("[RTSP Server]", $"TCP send error: {ex.Message}");
        }
}
    private async Task HandleSetup(StreamWriter writer, RtspRequest request, Client client)
    {
        if (string.IsNullOrEmpty(client.SessionId))
        {
            client.SessionId = Guid.NewGuid().ToString("N").Substring(0, 16);
        }
        if (!request.Headers.TryGetValue("Transport", out var transport))
        {
            await SendRtspResponse(writer, 400, "Bad Request", request.CSeq);
            return;
        }
        Log.Debug("[RTSP Server]", $"Transport: {transport}");
        var transportParams = ParseTransport(transport);
        var responseHeaders = new Dictionary<string, string>
        {
            ["Session"] = client.SessionId
        };
        if (transportParams.ContainsKey("interleaved"))
        {
            client.Transport = TransportMode.TCPInterleaved;
            if (transportParams["interleaved"].Contains("-"))
            {
                var channels = transportParams["interleaved"].Split('-');
                client.RtpChannel = byte.Parse(channels[0]);
                client.RtcpChannel = byte.Parse(channels[1]);
            }
            else
            {
                client.RtpChannel = 0;
                client.RtcpChannel = 1;
            }
            client.Width = 640;
            client.Height = 480;
            responseHeaders["Transport"] = $"RTP/AVP/TCP;unicast;interleaved={client.RtpChannel}-{client.RtcpChannel}";
        }
        else if (transportParams.ContainsKey("client_port"))
        {
            client.Transport = TransportMode.UDP;
            var ports = transportParams["client_port"].Split('-');
            var rtpPort = int.Parse(ports[0]);
            var rtcpPort = int.Parse(ports[1]);

            var clientIp = ((IPEndPoint?)client.Socket.RemoteEndPoint)?.Address;
            client.RtpEndPoint = new IPEndPoint(clientIp!, rtpPort);
            client.RtcpEndPoint = new IPEndPoint(clientIp!, rtcpPort);

            // Create UDP socket for this client
            client.UdpSocket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            client.UdpSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            var serverRtpPort = GetAvailablePort();
            var serverRtcpPort = serverRtpPort + 1;
            client.RtcpSocket = new (AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            client.UdpSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            IPEndPoint endPoint = new(IPAddress.Any, serverRtcpPort);
            client.RtcpSocket.Bind(endPoint);
            _ = Task.Run(() => ListenRtcpPort(client), _cts.Token);
            responseHeaders["Transport"] = $"RTP/AVP/UDP;unicast;client_port={rtpPort}-{rtcpPort};server_port={serverRtpPort}-{serverRtcpPort}";
        }
        else
        {
            await SendRtspResponse(writer, 461, "Unsupported Transport", request.CSeq);
            return;
        }

        await SendRtspResponse(writer, 200, "OK", request.CSeq, responseHeaders);
    }
    private async Task ListenRtcpPort(Client client)
    {
        byte[] buffer = new byte[1024];

        while (!client.IsPlaying && !_cts.IsCancellationRequested) await Task.Delay(100, _cts.Token);
        while (!_cts.IsCancellationRequested && client.RtcpSocket != null)
        {
            var timeout = Task.Delay(TimeSpan.FromSeconds(60), _cts.Token); // 1 minute timeout
            EndPoint remoteEndpoint = new IPEndPoint(IPAddress.Any, 0);
            var task = client.RtcpSocket.ReceiveFromAsync(buffer, SocketFlags.None, remoteEndpoint, _cts.Token).AsTask();
            var result = await Task.WhenAny(task, timeout);
            if (result == timeout)
            {
                CleanupClient(client);
                break;
            }
            else
            {
                var request = task.Result;
                if (request.ReceivedBytes > 0 && buffer.Length >= 2)
                {
                    if (buffer[1] == 203)
                    {
                        CleanupClient(client);
                        break;
                    }
                    else if (buffer[1] == 201)
                    {
                        HandleRtcpReport(client, buffer);
                    }
                    
                }
            }
        }
    }
    private void HandleRtcpReport(Client client, byte[] rtcpData)
    {
        // Parse RTCP RR (Receiver Report)
        if (rtcpData.Length >= 8 && (rtcpData[1] == 201)) // RR packet type
        {
            // Extract packet loss and jitter
            var fractionLost = rtcpData[12];
            var cumulativeLost = (uint)((rtcpData[13] << 16) | (rtcpData[14] << 8) | rtcpData[15]);
            var jitter = (uint)((rtcpData[20] << 24) | (rtcpData[21] << 16) | (rtcpData[22] << 8) | rtcpData[23]);
            
            // Adjust bitrate based on network conditions
            AdjustBitrate(client, fractionLost, jitter);
        }
    }
    private void AdjustBitrate(Client client, byte fractionLost, uint jitter)
    {
        int MIN_BITRATE = client.VideoProfile.MinBitrate;  // 500 kbps
        int MAX_BITRATE = client.VideoProfile.MaxBitrate; // 4 Mbps

        lock (client)
        {
            var previousBitrate = client.CurrentBitrate;
            if (fractionLost > 10) // More than 4% loss
            {
                client.CurrentBitrate = Math.Max(MIN_BITRATE, (int)(client.CurrentBitrate * 0.6));
                client.VideoProfile.Quality = Math.Max(10, (int)(client.VideoProfile.Quality * 0.6));
            }
            else if (fractionLost > 5 && fractionLost <= 10)
            {
                client.CurrentBitrate = Math.Max(MIN_BITRATE, (int)(client.CurrentBitrate * 0.9));
                client.VideoProfile.Quality = Math.Max(10, (int)(client.VideoProfile.Quality * 0.9));
            }
            else if (fractionLost < 2 && jitter < 100) // Good conditions
            {
                var now = DateTime.UtcNow;
                if (now - client.LastCodecUpdate > TimeSpan.FromSeconds(10))
                {
                    client.CurrentBitrate = Math.Min(MAX_BITRATE, (int)(client.CurrentBitrate * 1.1));
                    client.VideoProfile.Quality = Math.Min(100, (int)(client.VideoProfile.Quality * 0.6));
                }
            }
            if (client.CurrentBitrate != previousBitrate && client.Codec == CodecType.H264)
            {
                client.LastCodecUpdate = DateTime.UtcNow;
                if (client.CameraId == 0)
                {
                    _h264BackEncoder?.UpdateBitrate(client.CurrentBitrate);
                }
                else
                {
                    _h264FrontEncoder?.UpdateBitrate(client.CurrentBitrate);
                }
            }
        }
    }
    private async Task SendUdpData(Client client, byte[] rtpPacket, bool isRtcp)
    {
        try
        {
            var endpoint = isRtcp ? client.RtcpEndPoint : client.RtpEndPoint;
            if (client.UdpSocket != null && endpoint != null)
            {
                await client.UdpSocket.SendToAsync(rtpPacket, SocketFlags.None, endpoint);
            }
        }
        catch (SocketException ex)
        {
            Log.Error("[RTSP Server]", $"UDP send error: {ex.Message}");
            // Mark client for cleanup if persistent errors
            if (ex.SocketErrorCode == SocketError.HostUnreachable ||
                ex.SocketErrorCode == SocketError.NetworkUnreachable)
            {
                client.IsPlaying = false;
            }
        }
    }
    private Dictionary<string, string> ParseTransport(string transport)
    {
        var result = new Dictionary<string, string>();
        var parts = transport.Split(';');

        foreach (var part in parts)
        {
            var keyValue = part.Split('=');
            if (keyValue.Length == 2)
            {
                result[keyValue[0].Trim()] = keyValue[1].Trim();
            }
            else
            {
                result[part.Trim()] = "";
            }
        }

        return result;
    }
    private void CleanupClient(Client client)
    {
        try
        {
            lock (client)
            {
                ReleaseClientPorts(client);
                _clientSpsCache.Remove(client.Id);
                _clientPpsCache.Remove(client.Id);
                _clients.TryTake(out _);
                client.Dispose();
            }
            Log.Debug("[RTSP Server]", $"Client {client.Id} cleaned up");
        }
        catch (Exception ex)
        {
            Log.Error("[RTSP Server]", $"Error cleaning up client: {ex.Message}");
        }
    }
    private string GenerateSDP(CodecType codec = CodecType.H264)
    {
        var serverIp = GetLocalIpAddress();
        var sdp = new StringBuilder();
        sdp.AppendLine("v=0");
        sdp.AppendLine($"o=- {DateTime.UtcNow.Ticks} 1 IN IP4 {serverIp}");
        sdp.AppendLine("s=RTSP Server Stream");
        sdp.AppendLine("t=0 0");

        if (codec == CodecType.H264)
        {
            sdp.AppendLine("m=video 0 RTP/AVP 96");
            sdp.AppendLine($"c=IN IP4 {serverIp}");
            sdp.AppendLine("a=rtpmap:96 H264/90000");
            sdp.AppendLine("a=fmtp:96 profile-level-id=42e01e;packetization-mode=1");
            sdp.AppendLine($"a=control:rtsp://{serverIp}:{_port}/live");
        }
        else
        {
            sdp.AppendLine("m=video 0 RTP/AVP 26");
            sdp.AppendLine($"c=IN IP4 {serverIp}");
            sdp.AppendLine("a=rtpmap:26 JPEG/90000");
            sdp.AppendLine($"a=control:rtsp://{serverIp}:{_port}/live");
        }

        return sdp.ToString();
    }
    public static byte[] EncodeToJpeg(byte[] rawImageData, int width, int height, Android.Graphics.ImageFormatType format, int quality = 80 )
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
    private string GetLocalIpAddress()
    {
        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                // Skip loopback and non-operational interfaces
                if (networkInterface.OperationalStatus != OperationalStatus.Up)
                    continue;
                
                // Check for WiFi or Ethernet interfaces on Android
                if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                    networkInterface.NetworkInterfaceType == NetworkInterfaceType.Ethernet || networkInterface.Name.ToLower().Contains("tun0"))
                {
                    foreach (var addr in networkInterface.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            return addr.Address.ToString();
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error getting IP: {ex.Message}");
        }
        
        return "0.0.0.0";
    }
}