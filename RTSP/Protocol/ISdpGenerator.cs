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
    /// Gets the local IP address for SDP generation.
    /// </summary>
    /// <returns>The local IP address string.</returns>
    string GetLocalIpAddress();
}
