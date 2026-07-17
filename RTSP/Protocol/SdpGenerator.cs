using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Android.Util;
using BaluMediaServer.Models;

namespace BaluMediaServer.RTSP.Protocol;

/// <summary>
/// Generates SDP (Session Description Protocol) for RTSP streaming.
/// Supports H.264 and MJPEG video codecs with proper sprop-parameter-sets for VLC and other
/// players, and — when constructed with <c>audioTrackEnabled = true</c> — appends a second
/// <c>m=audio</c> AAC-LC track (RFC 3640 mpeg4-generic, <c>a=control:trackID=1</c>) so a
/// single RTSP session can advertise both video and audio. The audio <c>fmtp config=</c>
/// value is updated to the hardware encoder's actual AudioSpecificConfig via
/// <see cref="UpdateAudioSpecificConfig"/> as soon as the first encoded frame is available.
/// </summary>
public class SdpGenerator : ISdpGenerator
{
    private byte[]? _currentSps;
    private byte[]? _currentPps;
    private readonly object _spsPpsLock = new();

    private byte[]? _encoderAudioSpecificConfig;
    private readonly object _ascLock = new();

    /// <summary>
    /// True when the SDP should advertise a second <c>m=audio</c> track alongside video.
    /// </summary>
    public bool AudioTrackEnabled { get; }

    /// <summary>
    /// Audio sample rate (Hz) advertised in the SDP rtpmap for the audio track.
    /// </summary>
    public int AudioSampleRateHz { get; }

    /// <summary>
    /// Audio channel count advertised in the SDP rtpmap for the audio track.
    /// </summary>
    public int AudioChannels { get; }

    /// <summary>
    /// Constructs the SDP generator. When <paramref name="audioTrackEnabled"/> is true,
    /// generated SDP includes a second <c>m=audio</c> AAC-LC track with
    /// <c>a=control:trackID=1</c>. Audio parameters are advisory: no audio RTP packets
    /// are emitted by the current build — the audio track exists only to validate
    /// player compatibility for the future encoder work.
    /// </summary>
    public SdpGenerator(bool audioTrackEnabled = false, int audioSampleRateHz = 44100, int audioChannels = 1)
    {
        AudioTrackEnabled = audioTrackEnabled;
        AudioSampleRateHz = audioSampleRateHz;
        AudioChannels = audioChannels;
    }

    /// <summary>
    /// Updates the cached SPS/PPS values.
    /// </summary>
    /// <param name="sps">The Sequence Parameter Set.</param>
    /// <param name="pps">The Picture Parameter Set.</param>
    public void UpdateSpsPps(byte[]? sps, byte[]? pps)
    {
        lock (_spsPpsLock)
        {
            if (sps != null) _currentSps = sps;
            if (pps != null) _currentPps = pps;
        }
    }

    /// <summary>
    /// Clears the cached SPS/PPS values.
    /// </summary>
    public void ClearSpsPps()
    {
        lock (_spsPpsLock)
        {
            _currentSps = null;
            _currentPps = null;
        }
    }

    /// <summary>
    /// Overrides the SDP <c>fmtp config=</c> value with the AudioSpecificConfig
    /// the AAC hardware encoder actually reports. Idempotent — safe to call on
    /// every encoded frame; only the first non-null value is taken.
    /// </summary>
    public void UpdateAudioSpecificConfig(byte[]? asc)
    {
        if (asc == null || asc.Length == 0) return;
        lock (_ascLock)
        {
            if (_encoderAudioSpecificConfig == null)
            {
                _encoderAudioSpecificConfig = (byte[])asc.Clone();
            }
        }
    }

    /// <inheritdoc/>
    public string GenerateSdp(CodecType codec = CodecType.H264)
    {
        var serverIp = GetLocalIpAddress();
        var sdp = new StringBuilder();
        // RFC 4566 requires CRLF line endings — AppendLine() uses platform-native
        // endings (\n on Android), so we use explicit \r\n instead
        sdp.Append("v=0\r\n");
        sdp.Append($"o=- {DateTime.UtcNow.Ticks} 1 IN IP4 {serverIp}\r\n");
        sdp.Append("s=RTSP Server Stream\r\n");
        sdp.Append("t=0 0\r\n");
        sdp.Append("a=tool:BaluMediaServer\r\n");
        sdp.Append("a=sendonly\r\n");

        if (codec == CodecType.H264)
        {
            sdp.Append("m=video 0 RTP/AVP 96\r\n");
            sdp.Append($"c=IN IP4 {serverIp}\r\n");
            sdp.Append("a=rtpmap:96 H264/90000\r\n");
            sdp.Append("a=framerate:25\r\n");

            // Build fmtp line with sprop-parameter-sets for VLC and other players
            var fmtpParams = new StringBuilder("profile-level-id=42e01e;packetization-mode=1");

            byte[]? sps, pps;
            lock (_spsPpsLock)
            {
                sps = _currentSps;
                pps = _currentPps;
            }

            var spropParams = GetSpropParameterSets(sps, pps);
            if (!string.IsNullOrEmpty(spropParams))
            {
                fmtpParams.Append($";sprop-parameter-sets={spropParams}");
            }
            sdp.Append($"a=fmtp:96 {fmtpParams}\r\n");
            sdp.Append("a=control:trackID=0\r\n");

            // Audio track is only advertised alongside H.264 video. MJPEG remains
            // single-track because RTP/MJPEG is rarely consumed with audio in
            // practice and the MJPEG path has not been validated against multi-track SDP.
            if (AudioTrackEnabled)
            {
                AppendAudioBlock(sdp, serverIp);
            }
        }
        else
        {
            sdp.Append("m=video 0 RTP/AVP 26\r\n");
            sdp.Append($"c=IN IP4 {serverIp}\r\n");
            sdp.Append("a=rtpmap:26 JPEG/90000\r\n");
            sdp.Append("a=control:trackID=0\r\n");
        }

        return sdp.ToString();
    }

    /// <summary>
    /// Appends the <c>m=audio</c> block for the AAC-LC audio track to the SDP.
    /// Uses payload type 97 (dynamic), MPEG4-GENERIC RTP profile (RFC 3640),
    /// AAC-hbr mode, and <c>a=control:trackID=1</c>.
    /// </summary>
    private void AppendAudioBlock(StringBuilder sdp, string serverIp)
    {
        int sampleRate = AudioSampleRateHz;
        int channels = AudioChannels < 1 ? 1 : AudioChannels;

        // Prefer the AudioSpecificConfig the encoder actually produced, falling
        // back to a value computed from the SDP sample-rate/channels if the
        // encoder hasn't reported one yet (e.g. DESCRIBE before first frame).
        byte[]? encoderAsc;
        lock (_ascLock) encoderAsc = _encoderAudioSpecificConfig;
        string ascHex = encoderAsc != null
            ? BitConverter.ToString(encoderAsc).Replace("-", string.Empty)
            : BuildAacLcAudioSpecificConfig(sampleRate, channels);

        sdp.Append($"m=audio 0 RTP/AVP 97\r\n");
        sdp.Append($"c=IN IP4 {serverIp}\r\n");
        sdp.Append($"a=rtpmap:97 mpeg4-generic/{sampleRate}/{channels}\r\n");
        sdp.Append("a=fmtp:97 streamtype=5;profile-level-id=1;mode=AAC-hbr;");
        sdp.Append($"config={ascHex};sizelength=13;indexlength=3;indexdeltalength=3\r\n");
        sdp.Append("a=control:trackID=1\r\n");
    }

    /// <summary>
    /// Builds the AAC AudioSpecificConfig (RFC 3640 / ISO 14496-3) hex string used in
    /// the SDP <c>fmtp config=</c> parameter. Encodes Object Type 2 (AAC-LC), a known
    /// sample-rate index (or escape index 15 with explicit 24-bit frequency), the channel
    /// configuration, then 3 bits of GASpecificConfig padding.
    /// </summary>
    /// <returns>Hex-encoded ASC string in uppercase, e.g. "1208" for 44100 Hz mono.</returns>
    internal static string BuildAacLcAudioSpecificConfig(int sampleRateHz, int channels)
    {
        // Standard AAC sample-rate index table (ISO/IEC 14496-3 §1.6.3.4).
        int sampleRateIndex = sampleRateHz switch
        {
            96000 => 0, 88200 => 1, 64000 => 2, 48000 => 3, 44100 => 4,
            32000 => 5, 24000 => 6, 22050 => 7, 16000 => 8, 12000 => 9,
            11025 => 10, 8000 => 11, 7350 => 12,
            _ => 15 // Escape — explicit 24-bit frequency follows.
        };

        if (channels < 1) channels = 1;
        if (channels > 7) channels = 7;

        // Bitstream assembly using a 32-bit accumulator. AAC-LC ASC for known sample rates
        // is exactly 16 bits (5+4+4+3); the escape path is 39 bits — we pad the latter to 40
        // bits / 5 bytes which players tolerate (extra trailing zero bits are ignored).
        const int aacLcObjectType = 2;
        ulong bits = 0;
        int len = 0;

        void Append(uint value, int width)
        {
            bits = (bits << width) | (value & ((1u << width) - 1));
            len += width;
        }

        Append((uint)aacLcObjectType, 5);
        Append((uint)sampleRateIndex, 4);
        if (sampleRateIndex == 15)
        {
            Append((uint)sampleRateHz, 24);
        }
        Append((uint)channels, 4);
        // GASpecificConfig: frameLengthFlag(1) + dependsOnCoreCoder(1) + extensionFlag(1) = 000
        Append(0, 3);

        // Pad to a whole number of bytes.
        int padBits = (8 - (len % 8)) % 8;
        if (padBits > 0)
        {
            Append(0, padBits);
        }

        int byteCount = len / 8;
        var hex = new StringBuilder(byteCount * 2);
        for (int i = byteCount - 1; i >= 0; i--)
        {
            byte b = (byte)((bits >> (i * 8)) & 0xFF);
            hex.Append(b.ToString("X2"));
        }
        return hex.ToString();
    }

    /// <inheritdoc/>
    public string GetSpropParameterSets(byte[]? sps, byte[]? pps)
    {
        if (sps == null || pps == null)
        {
            return string.Empty;
        }

        try
        {
            // Remove start codes before base64 encoding
            var spsWithoutStartCode = RemoveStartCode(sps);
            var ppsWithoutStartCode = RemoveStartCode(pps);

            var spsBase64 = Convert.ToBase64String(spsWithoutStartCode);
            var ppsBase64 = Convert.ToBase64String(ppsWithoutStartCode);

            return $"{spsBase64},{ppsBase64}";
        }
        catch (Exception ex)
        {
            BaluLogger.Error("[SdpGenerator]", $"Error generating sprop-parameter-sets: {ex.Message}");
            return string.Empty;
        }
    }

    /// <inheritdoc/>
    public string GetLocalIpAddress()
    {
        try
        {
            // Prefer a real LAN interface (Wi-Fi / Ethernet / cellular) so the advertised address is
            // reachable from clients on the same network. Only fall back to a VPN tunnel if there is
            // no LAN IPv4 — otherwise a device running a VPN (e.g. Tailscale) advertises its
            // unreachable tunnel IP, breaking ONVIF GetStreamUri/XAddr and the RTSP SDP for LAN clients.
            //
            // Classification is by interface NAME, not NetworkInterfaceType: Mono on Android reports
            // the type as Unknown for wlan0/eth0, so a type-based check silently misses the LAN NIC and
            // falls through to the tunnel. Names are stable (wlan*, eth*, rmnet*, tun*, wg*, ...).
            string? tunnelFallback = null;

            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up)
                    continue;

                var ipv4 = FirstIPv4(networkInterface);   // skips loopback + link-local
                if (ipv4 is null)
                    continue;

                var name = networkInterface.Name.ToLowerInvariant();
                bool isTunnel = name.StartsWith("tun") || name.StartsWith("tap") ||
                                name.StartsWith("ppp") || name.StartsWith("wg") ||
                                networkInterface.NetworkInterfaceType == NetworkInterfaceType.Tunnel;

                if (isTunnel)
                {
                    tunnelFallback ??= ipv4;             // remember, but only use if no LAN address exists
                    continue;
                }

                return ipv4;                             // first non-tunnel, non-loopback IPv4 wins
            }

            if (tunnelFallback is not null)
                return tunnelFallback;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error getting IP: {ex.Message}");
        }

        return "0.0.0.0";
    }

    /// <summary>
    /// Returns the first usable IPv4 unicast address of an interface, or null if it has none.
    /// Skips loopback (127.x) and link-local/APIPA (169.254.x) addresses so they are never advertised.
    /// </summary>
    private static string? FirstIPv4(NetworkInterface networkInterface)
    {
        if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
            networkInterface.Name.Equals("lo", StringComparison.OrdinalIgnoreCase))
            return null;

        foreach (var addr in networkInterface.GetIPProperties().UnicastAddresses)
        {
            if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                continue;
            var ip = addr.Address.ToString();
            if (ip.StartsWith("127.") || ip.StartsWith("169.254."))
                continue;
            return ip;
        }
        return null;
    }

    /// <summary>
    /// Removes start code prefix from NAL unit data.
    /// </summary>
    private static byte[] RemoveStartCode(byte[] nalUnit)
    {
        if (nalUnit.Length >= 4 &&
            nalUnit[0] == 0 && nalUnit[1] == 0 && nalUnit[2] == 0 && nalUnit[3] == 1)
        {
            var result = new byte[nalUnit.Length - 4];
            Array.Copy(nalUnit, 4, result, 0, result.Length);
            return result;
        }
        else if (nalUnit.Length >= 3 &&
                 nalUnit[0] == 0 && nalUnit[1] == 0 && nalUnit[2] == 1)
        {
            var result = new byte[nalUnit.Length - 3];
            Array.Copy(nalUnit, 3, result, 0, result.Length);
            return result;
        }
        return nalUnit;
    }
}
