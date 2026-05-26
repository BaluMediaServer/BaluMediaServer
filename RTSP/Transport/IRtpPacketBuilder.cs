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
    /// Builds RTP packets for an H.264 NAL unit into the provided list (no send).
    /// </summary>
    void BuildH264NalRtpPackets(Client client, byte[] nalUnit, uint nalTimestamp, bool lastFrame, List<byte[]> outPackets);

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
    /// Builds a single RFC 3640 mpeg4-generic AAC-hbr RTP packet wrapping one access unit.
    /// One AU per RTP packet (no fragmentation): the AAC frame is always &lt; MTU at typical
    /// rates. Updates the client's audio sequence number and audio packet/octet counters.
    /// </summary>
    byte[] BuildAacRtpPacket(Client client, byte[] accessUnit, uint timestamp, byte payloadType = 97);

    /// <summary>
    /// Converts an audio encoder presentation timestamp (microseconds) into the audio RTP
    /// timestamp space (clock = <paramref name="sampleRateHz"/>). Stores the baseline on the
    /// client on first call so subsequent timestamps are deltas from a stable origin.
    /// </summary>
    uint EncoderTimestampToAudioRtp(long encoderTimestampUs, int sampleRateHz, ref Client client);

    /// <summary>
    /// Gets the standard JPEG quantization tables.
    /// </summary>
    /// <returns>The quantization tables.</returns>
    byte[] GetStandardQuantizationTables();
}
