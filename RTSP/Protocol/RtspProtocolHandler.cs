using System.Net;
using System.Net.Sockets;
using Android.Util;
using BaluMediaServer.Models;
using BaluMediaServer.RTSP.Security;
using BaluMediaServer.RTSP.Transport;

namespace BaluMediaServer.RTSP.Protocol;

/// <summary>
/// Handles RTSP protocol parsing, routing, and response generation.
/// </summary>
public class RtspProtocolHandler : IRtspProtocolHandler
{
    private readonly IAuthenticationManager _authManager;
    private readonly ISdpGenerator _sdpGenerator;
    private readonly ITransportManager _transportManager;
    private readonly IRtcpManager _rtcpManager;
    private readonly CancellationToken _cancellationToken;

    private readonly bool _frontCameraEnabled;
    private readonly bool _backCameraEnabled;
    private readonly int _backCameraWidth;
    private readonly int _backCameraHeight;
    private readonly int _frontCameraWidth;
    private readonly int _frontCameraHeight;

    /// <summary>
    /// Creates a new RtspProtocolHandler.
    /// </summary>
    public RtspProtocolHandler(
        IAuthenticationManager authManager,
        ISdpGenerator sdpGenerator,
        ITransportManager transportManager,
        IRtcpManager rtcpManager,
        CancellationToken cancellationToken,
        bool frontCameraEnabled,
        bool backCameraEnabled,
        int backCameraWidth,
        int backCameraHeight,
        int frontCameraWidth,
        int frontCameraHeight)
    {
        _authManager = authManager;
        _sdpGenerator = sdpGenerator;
        _transportManager = transportManager;
        _rtcpManager = rtcpManager;
        _cancellationToken = cancellationToken;
        _frontCameraEnabled = frontCameraEnabled;
        _backCameraEnabled = backCameraEnabled;
        _backCameraWidth = backCameraWidth;
        _backCameraHeight = backCameraHeight;
        _frontCameraWidth = frontCameraWidth;
        _frontCameraHeight = frontCameraHeight;
    }

    /// <inheritdoc/>
    public async Task<RtspRequest?> ParseRequestAsync(StreamReader reader, string requestLine)
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
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_cancellationToken).ConfigureAwait(false)))
        {
            var colonIndex = line.IndexOf(':');
            if (colonIndex > 0)
            {
                var key = line.Substring(0, colonIndex).Trim();
                var value = line.Substring(colonIndex + 1).Trim();
                request.Headers[key] = value;
                if (key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                {
                    request.Auth = _authManager.ParseAuthorization(value, request.Method, request.Uri);
                }
            }
        }

        if (request.Headers.TryGetValue("Content-Length", out var lengthStr) &&
            int.TryParse(lengthStr, out var length) && length > 0)
        {
            var buffer = new char[length];
            await reader.ReadAsync(buffer, 0, length).ConfigureAwait(false);
            request.Body = new string(buffer);
        }

        return request;
    }

    /// <inheritdoc/>
    public async Task SendResponseAsync(StreamWriter writer, int statusCode, string statusText,
        int cseq, Dictionary<string, string>? headers = null, string? body = null)
    {
        await writer.WriteLineAsync($"RTSP/1.0 {statusCode} {statusText}").ConfigureAwait(false);
        await writer.WriteLineAsync($"CSeq: {cseq}").ConfigureAwait(false);

        if (headers != null)
        {
            foreach (var header in headers)
            {
                await writer.WriteLineAsync($"{header.Key}: {header.Value}").ConfigureAwait(false);
            }
        }

        if (!string.IsNullOrEmpty(body))
        {
            await writer.WriteLineAsync($"Content-Length: {body.Length}").ConfigureAwait(false);
            await writer.WriteLineAsync($"Content-Type: application/sdp").ConfigureAwait(false);
        }

        await writer.WriteLineAsync().ConfigureAwait(false);

        if (!string.IsNullOrEmpty(body))
        {
            await writer.WriteAsync(body).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task HandleOptionsAsync(StreamWriter writer, RtspRequest request)
    {
        var headers = new Dictionary<string, string>
        {
            ["Public"] = "OPTIONS, DESCRIBE, SETUP, PLAY, TEARDOWN"
        };

        await SendResponseAsync(writer, 200, "OK", request.CSeq, headers).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task HandleDescribeAsync(StreamWriter writer, RtspRequest request, Client client)
    {
        lock (client)
        {
            client.Codec = request.Uri.Contains("mjpeg") ? CodecType.MJPEG : CodecType.H264;
        }
        var sdp = _sdpGenerator.GenerateSdp(client.Codec);
        var headers = new Dictionary<string, string>
        {
            ["Content-Base"] = request.Uri
        };

        await SendResponseAsync(writer, 200, "OK", request.CSeq, headers, sdp).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task HandleSetupAsync(StreamWriter writer, RtspRequest request, Client client)
    {
        if (string.IsNullOrEmpty(client.SessionId))
        {
            client.SessionId = Guid.NewGuid().ToString("N").Substring(0, 16);
        }

        if (!request.Headers.TryGetValue("Transport", out var transport))
        {
            await SendResponseAsync(writer, 400, "Bad Request", request.CSeq).ConfigureAwait(false);
            return;
        }

        Log.Debug("[RtspProtocol]", $"Transport: {transport}");
        var transportParams = _transportManager.ParseTransport(transport);
        var responseHeaders = new Dictionary<string, string>
        {
            ["Session"] = $"{client.SessionId};timeout=60"
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

            // Set client dimensions based on camera configuration
            if (client.CameraId == 1) // Front camera
            {
                client.Width = _frontCameraWidth;
                client.Height = _frontCameraHeight;
            }
            else // Back camera
            {
                client.Width = _backCameraWidth;
                client.Height = _backCameraHeight;
            }

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
            client.UdpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            client.UdpSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            client.UdpSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.SendBuffer, 65536);
            client.UdpSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer, 65536);

            var serverRtpPort = _transportManager.GetAvailablePort();
            var serverRtcpPort = serverRtpPort + 1;
            client.RtcpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            IPEndPoint endPoint = new(IPAddress.Any, serverRtcpPort);
            client.RtcpSocket.Bind(endPoint);
            _ = Task.Run(() => _rtcpManager.ListenRtcpPortAsync(client), _cancellationToken);

            responseHeaders["Transport"] = $"RTP/AVP/UDP;unicast;client_port={rtpPort}-{rtcpPort};server_port={serverRtpPort}-{serverRtcpPort}";
        }
        else
        {
            await SendResponseAsync(writer, 461, "Unsupported Transport", request.CSeq).ConfigureAwait(false);
            return;
        }

        await SendResponseAsync(writer, 200, "OK", request.CSeq, responseHeaders).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> HandlePlayAsync(StreamWriter writer, RtspRequest request, Client client)
    {
        if (string.IsNullOrEmpty(client.SessionId))
        {
            await SendResponseAsync(writer, 454, "Session Not Found", request.CSeq).ConfigureAwait(false);
            return false;
        }

        var responseHeaders = new Dictionary<string, string>
        {
            ["Session"] = $"{client.SessionId};timeout=60",
            ["RTP-Info"] = $"url={request.Uri}/trackID=0;seq={client.SequenceNumber};rtptime={client.RtpTimestamp}"
        };

        await SendResponseAsync(writer, 200, "OK", request.CSeq, responseHeaders).ConfigureAwait(false);

        lock (client)
        {
            client.IsPlaying = true;
        }

        return true;
    }

    /// <inheritdoc/>
    public async Task HandleTeardownAsync(StreamWriter writer, RtspRequest request, Client client)
    {
        if (string.IsNullOrEmpty(client.SessionId))
        {
            await SendResponseAsync(writer, 454, "Session Not Found", request.CSeq).ConfigureAwait(false);
            return;
        }

        lock (client)
        {
            client.IsPlaying = false;
        }

        var responseHeaders = new Dictionary<string, string>
        {
            ["Session"] = $"{client.SessionId};timeout=60"
        };

        await SendResponseAsync(writer, 200, "OK", request.CSeq, responseHeaders).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> HandleUriAsync(string uri, StreamWriter writer, RtspRequest request, Client client)
    {
        if (!uri.Contains("/live"))
        {
            await SendResponseAsync(writer, 404, "Not Found", request.CSeq).ConfigureAwait(false);
            return false;
        }

        if (uri.Contains("/live/front") && !_frontCameraEnabled)
        {
            await SendResponseAsync(writer, 400, "Front Camera not enabled", request.CSeq).ConfigureAwait(false);
            return false;
        }
        else if ((uri.Contains("/live/back") || uri.Contains("/live")) && !_backCameraEnabled)
        {
            await SendResponseAsync(writer, 400, "Back Camera not enabled", request.CSeq).ConfigureAwait(false);
            return false;
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

        return true;
    }
}
