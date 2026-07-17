using BaluMediaServer.Models;

namespace BaluMediaServer.RTSP.Protocol;

/// <summary>
/// Interface for SDP generation for RTSP streaming.
/// </summary>
public interface ISdpGenerator
{
    /// <summary>
    /// Generates an SDP description for the specified codec.
    /// </summary>
    /// <param name="codec">The codec type (H264 or MJPEG).</param>
    /// <returns>The SDP string.</returns>
    string GenerateSdp(CodecType codec);

    /// <summary>
    /// Gets the sprop-parameter-sets value for SDP (base64-encoded SPS and PPS).
    /// </summary>
    /// <param name="sps">The Sequence Parameter Set.</param>
    /// <param name="pps">The Picture Parameter Set.</param>
    /// <returns>The formatted sprop-parameter-sets string.</returns>
    string GetSpropParameterSets(byte[]? sps, byte[]? pps);

    /// <summary>
    /// Gets the device's local IPv4 address for the SDP connection line (and reused by the ONVIF
    /// layer for the advertised stream/snapshot/service URIs). Prefers a directly-reachable LAN
    /// interface (Wi-Fi / Ethernet / cellular, matched by interface name) and only falls back to a
    /// VPN tunnel (e.g. Tailscale <c>tun0</c>, WireGuard) when no LAN address exists, so clients on
    /// the same network always receive a reachable address. Loopback and link-local addresses are
    /// never returned; the fallback when nothing is found is <c>"0.0.0.0"</c>.
    /// </summary>
    /// <returns>The local IPv4 address string, or <c>"0.0.0.0"</c> if none could be determined.</returns>
    string GetLocalIpAddress();
}
