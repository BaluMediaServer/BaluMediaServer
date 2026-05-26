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
    private readonly bool _audioTrackEnabled;

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
        int frontCameraHeight,
        bool audioTrackEnabled = false)
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
        _audioTrackEnabled = audioTrackEnabled;
    }

    /// <summary>
    /// Extracts <c>trackID=N</c> from a URI (SETUP or other). Returns 0 (video) when no
    /// trackID is present — preserving backwards compatibility with single-track clients
    /// that issue SETUP against the base URI.
    /// </summary>
    private static int ParseTrackIdFromUri(string uri)
    {
        const string marker = "trackID=";
        int idx = uri.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return 0;
        int start = idx + marker.Length;
        int end = start;
        while (end < uri.Length && char.IsDigit(uri[end])) end++;
        if (end == start) return 0;
        return int.TryParse(uri.AsSpan(start, end - start), out var trackId) ? trackId : 0;
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
            await writer.WriteLineAsync($"Content-Length: {System.Text.Encoding.UTF8.GetByteCount(body)}").ConfigureAwait(false);
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
            ["Content-Base"] = request.Uri.TrimEnd('/') + "/"
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

        // Parse trackID from the SETUP URI. Track 0 is video (default if absent),
        // track 1 is audio. Audio SETUP populates a separate set of Client.Audio* fields
        // so that both tracks can co-exist on a single RTSP session.
        int trackId = ParseTrackIdFromUri(request.Uri);
        bool isAudio = trackId == 1;

        if (isAudio && !_audioTrackEnabled)
        {
            // Audio not advertised in SDP — reject any SETUP for trackID=1.
            await SendResponseAsync(writer, 404, "Not Found", request.CSeq).ConfigureAwait(false);
            return;
        }

        BaluLogger.Debug("[RtspProtocol]", $"Transport (track={trackId}): {transport}");
        var transportParams = _transportManager.ParseTransport(transport);
        var responseHeaders = new Dictionary<string, string>
        {
            ["Session"] = $"{client.SessionId};timeout=60"
        };

        if (transportParams.ContainsKey("interleaved"))
        {
            client.Transport = TransportMode.TCPInterleaved;
            byte rtpChannel, rtcpChannel;
            if (transportParams["interleaved"].Contains("-"))
            {
                var channels = transportParams["interleaved"].Split('-');
                rtpChannel = byte.Parse(channels[0]);
                rtcpChannel = byte.Parse(channels[1]);
            }
            else
            {
                // Defaults — video on 0/1, audio on 2/3 — only used when the client
                // didn't specify channels explicitly (rare; VLC always specifies).
                rtpChannel = isAudio ? (byte)2 : (byte)0;
                rtcpChannel = isAudio ? (byte)3 : (byte)1;
            }

            if (isAudio)
            {
                client.AudioRtpChannel = rtpChannel;
                client.AudioRtcpChannel = rtcpChannel;
                client.AudioSetupComplete = true;
            }
            else
            {
                client.RtpChannel = rtpChannel;
                client.RtcpChannel = rtcpChannel;
                AssignVideoDimensions(client);
            }

            responseHeaders["Transport"] = $"RTP/AVP/TCP;unicast;interleaved={rtpChannel}-{rtcpChannel}";
        }
        else if (transportParams.ContainsKey("client_port"))
        {
            client.Transport = TransportMode.UDP;
            var ports = transportParams["client_port"].Split('-');
            var rtpPort = int.Parse(ports[0]);
            var rtcpPort = int.Parse(ports[1]);

            var clientIp = ((IPEndPoint?)client.Socket.RemoteEndPoint)?.Address;
            var serverRtpPort = _transportManager.GetAvailablePort();
            var serverRtcpPort = serverRtpPort + 1;

            var udpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            udpSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udpSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.SendBuffer, 65536);
            udpSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer, 65536);
            udpSocket.Bind(new IPEndPoint(IPAddress.Any, serverRtpPort));

            var rtcpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            rtcpSocket.Bind(new IPEndPoint(IPAddress.Any, serverRtcpPort));

            if (isAudio)
            {
                client.AudioRtpEndPoint = new IPEndPoint(clientIp!, rtpPort);
                client.AudioRtcpEndPoint = new IPEndPoint(clientIp!, rtcpPort);
                client.AudioUdpSocket = udpSocket;
                client.AudioRtcpSocket = rtcpSocket;
                client.AudioSetupComplete = true;
                // No RTCP listener spawned for the audio track yet — there is no audio RTP
                // flow in this build, so receiver reports for trackID=1 are uninteresting.
            }
            else
            {
                client.RtpEndPoint = new IPEndPoint(clientIp!, rtpPort);
                client.RtcpEndPoint = new IPEndPoint(clientIp!, rtcpPort);
                client.UdpSocket = udpSocket;
                client.RtcpSocket = rtcpSocket;
                _ = Task.Run(() => _rtcpManager.ListenRtcpPortAsync(client), _cancellationToken);
                AssignVideoDimensions(client);
            }

            responseHeaders["Transport"] = $"RTP/AVP/UDP;unicast;client_port={rtpPort}-{rtcpPort};server_port={serverRtpPort}-{serverRtcpPort}";
        }
        else
        {
            await SendResponseAsync(writer, 461, "Unsupported Transport", request.CSeq).ConfigureAwait(false);
            return;
        }

        await SendResponseAsync(writer, 200, "OK", request.CSeq, responseHeaders).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets the client's stream width/height from the active camera configuration.
    /// Only called for the video track SETUP.
    /// </summary>
    private void AssignVideoDimensions(Client client)
    {
        if (client.CameraId == 1)
        {
            client.Width = _frontCameraWidth;
            client.Height = _frontCameraHeight;
        }
        else
        {
            client.Width = _backCameraWidth;
            client.Height = _backCameraHeight;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> HandlePlayAsync(StreamWriter writer, RtspRequest request, Client client)
    {
        if (string.IsNullOrEmpty(client.SessionId))
        {
            await SendResponseAsync(writer, 454, "Session Not Found", request.CSeq).ConfigureAwait(false);
            return false;
        }

        // Initialize RTP state before sending PLAY response so RTP-Info matches actual first packets
        lock (client)
        {
            client.SequenceNumber = (ushort)Random.Shared.Next(0, ushort.MaxValue);
            client.RtpTimestamp = (uint)Random.Shared.Next(0, int.MaxValue);
            client.LastRtpTime = DateTime.UtcNow;

            if (client.AudioSetupComplete)
            {
                client.AudioSequenceNumber = (ushort)Random.Shared.Next(0, ushort.MaxValue);
                client.AudioRtpTimestamp = (uint)Random.Shared.Next(0, int.MaxValue);
            }
        }

        // The PLAY URI may itself include /trackID=N when the client issues PLAY per-track.
        // Strip it so the RTP-Info url= entries are anchored at the aggregate session URI.
        var baseUri = StripTrackSuffix(request.Uri).TrimEnd('/') + "/";
        var rtpInfo = $"url={baseUri}trackID=0;seq={client.SequenceNumber};rtptime={client.RtpTimestamp}";
        if (client.AudioSetupComplete)
        {
            rtpInfo += $",url={baseUri}trackID=1;seq={client.AudioSequenceNumber};rtptime={client.AudioRtpTimestamp}";
        }

        var responseHeaders = new Dictionary<string, string>
        {
            ["Session"] = $"{client.SessionId};timeout=60",
            ["Range"] = "npt=0.000-",
            ["RTP-Info"] = rtpInfo
        };

        await SendResponseAsync(writer, 200, "OK", request.CSeq, responseHeaders).ConfigureAwait(false);

        lock (client)
        {
            client.IsPlaying = true;
        }

        return true;
    }

    /// <summary>
    /// Returns the URI with any trailing <c>/trackID=N</c> segment removed.
    /// </summary>
    private static string StripTrackSuffix(string uri)
    {
        const string marker = "/trackID=";
        int idx = uri.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return idx < 0 ? uri : uri.Substring(0, idx);
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
