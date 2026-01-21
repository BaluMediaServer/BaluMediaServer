using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Android.Util;
using BaluMediaServer.Models;

namespace BaluMediaServer.RTSP.Protocol;

/// <summary>
/// Generates SDP (Session Description Protocol) for RTSP streaming.
/// Supports H.264 and MJPEG codecs with proper sprop-parameter-sets for VLC and other players.
/// </summary>
public class SdpGenerator : ISdpGenerator
{
    private byte[]? _currentSps;
    private byte[]? _currentPps;
    private readonly object _spsPpsLock = new();

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

    /// <inheritdoc/>
    public string GenerateSdp(CodecType codec = CodecType.H264)
    {
        var serverIp = GetLocalIpAddress();
        var sdp = new StringBuilder();
        sdp.AppendLine("v=0");
        sdp.AppendLine($"o=- {DateTime.UtcNow.Ticks} 1 IN IP4 {serverIp}");
        sdp.AppendLine("s=RTSP Server Stream");
        sdp.AppendLine("t=0 0");
        sdp.AppendLine("a=tool:BaluMediaServer");
        sdp.AppendLine("a=sendonly");

        if (codec == CodecType.H264)
        {
            sdp.AppendLine("m=video 0 RTP/AVP 96");
            sdp.AppendLine($"c=IN IP4 {serverIp}");
            sdp.AppendLine("a=rtpmap:96 H264/90000");

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
            sdp.AppendLine($"a=fmtp:96 {fmtpParams}");
            sdp.AppendLine("a=control:trackID=0");
        }
        else
        {
            sdp.AppendLine("m=video 0 RTP/AVP 26");
            sdp.AppendLine($"c=IN IP4 {serverIp}");
            sdp.AppendLine("a=rtpmap:26 JPEG/90000");
            sdp.AppendLine("a=control:trackID=0");
        }

        return sdp.ToString();
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
            Log.Error("[SdpGenerator]", $"Error generating sprop-parameter-sets: {ex.Message}");
            return string.Empty;
        }
    }

    /// <inheritdoc/>
    public string GetLocalIpAddress()
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
                    networkInterface.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                    networkInterface.Name.ToLower().Contains("tun0"))
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
