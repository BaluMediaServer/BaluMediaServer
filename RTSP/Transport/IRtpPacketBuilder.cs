using BaluMediaServer.Models;

namespace BaluMediaServer.RTSP.Transport;

/// <summary>
/// Interface for building RTP packets and handling NAL unit fragmentation.
/// </summary>
public interface IRtpPacketBuilder
{
    /// <summary>
    /// Creates an RTP packet with the specified payload and timestamp.
    /// </summary>
    /// <param name="client">The client.</param>
    /// <param name="payload">The payload data.</param>
    /// <param name="timestamp">The RTP timestamp.</param>
    /// <param name="marker">The marker bit.</param>
    /// <param name="payloadType">The payload type.</param>
    /// <returns>The RTP packet.</returns>
    byte[] CreateRtpPacket(Client client, byte[] payload, uint timestamp, bool marker, byte payloadType);

    /// <summary>
    /// Sends an H.264 NAL unit as RTP packets (with fragmentation if needed).
    /// </summary>
    /// <param name="client">The client.</param>
    /// <param name="nalUnit">The NAL unit.</param>
    /// <param name="timestamp">The RTP timestamp.</param>
    /// <param name="lastFrame">True if this is the last NAL of the frame.</param>
    Task SendH264NalAsRtpAsync(Client client, byte[] nalUnit, uint timestamp, bool lastFrame);

    /// <summary>
    /// Sends JPEG data as RTP packets.
    /// </summary>
    /// <param name="client">The client.</param>
    /// <param name="jpegData">The JPEG data.</param>
    Task SendJpegAsRtpAsync(Client client, byte[] jpegData);

    /// <summary>
    /// Converts an encoder timestamp to RTP timestamp.
    /// </summary>
    /// <param name="encoderTimestamp">The encoder timestamp.</param>
    /// <param name="client">The client.</param>
    /// <returns>The RTP timestamp.</returns>
    uint EncoderTimestampToRtp(ulong encoderTimestamp, ref Client client);

    /// <summary>
    /// Gets the standard JPEG quantization tables.
    /// </summary>
    /// <returns>The quantization tables.</returns>
    byte[] GetStandardQuantizationTables();
}
