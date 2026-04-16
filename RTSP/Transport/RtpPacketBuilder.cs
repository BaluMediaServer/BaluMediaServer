using System.Diagnostics;
using BaluMediaServer.Models;

namespace BaluMediaServer.RTSP.Transport;

/// <summary>
/// Builds RTP packets and handles H.264 NAL unit fragmentation and JPEG RTP encapsulation.
/// </summary>
public class RtpPacketBuilder : IRtpPacketBuilder
{
    private const int MaxPayloadSize = 1400;
    private readonly ITransportManager _transportManager;
    private static readonly byte[] _standardQuantizationTables = CreateStandardQuantizationTables();

    /// <summary>
    /// Creates a new RtpPacketBuilder.
    /// </summary>
    /// <param name="transportManager">The transport manager.</param>
    public RtpPacketBuilder(ITransportManager transportManager)
    {
        _transportManager = transportManager;
    }

    /// <inheritdoc/>
    public byte[] CreateRtpPacket(Client client, byte[] payload, uint timestamp, bool marker, byte payloadType)
    {
        var packet = new byte[12 + payload.Length];
        WriteRtpHeader(client, packet, timestamp, marker, payloadType, payload.Length);
        Buffer.BlockCopy(payload, 0, packet, 12, payload.Length);
        return packet;
    }

    /// <inheritdoc/>
    public async Task SendH264NalAsRtpAsync(Client client, byte[] nalUnit, uint nalTimestamp, bool lastFrame = false)
    {
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
        if (nalLength <= 0) return;

        if (nalLength <= MaxPayloadSize)
        {
            // Single NAL unit — build RTP packet directly from source, no intermediate payload copy
            var rtpPacket = BuildRtpPacket(client, nalUnit, nalStart, nalLength, nalTimestamp, lastFrame, 96);
            await _transportManager.SendDataAsync(client, rtpPacket).ConfigureAwait(false);
        }
        else
        {
            // FU-A fragmentation — build each RTP packet inline, no intermediate payload buffer
            byte nalHeader = nalUnit[nalStart];
            byte nalType = (byte)(nalHeader & 0x1F);
            byte fuIndicator = (byte)((nalHeader & 0x60) | 28); // NRI preserved, type=28 (FU-A)

            int dataOffset = nalStart + 1;
            int remainingData = nalLength - 1;
            bool isFirstFragment = true;

            while (remainingData > 0 && !_transportManager.CancellationToken.IsCancellationRequested)
            {
                int fragmentSize = Math.Min(MaxPayloadSize - 2, remainingData);
                bool isLastFragment = fragmentSize == remainingData;
                bool marker = lastFrame && isLastFragment;

                byte fuHeader = nalType;
                if (isFirstFragment) fuHeader |= 0x80; // Start bit
                if (isLastFragment) fuHeader |= 0x40;  // End bit

                // Allocate one packet: 12 (RTP header) + 2 (FU indicator + FU header) + fragment
                var rtpPacket = new byte[14 + fragmentSize];
                WriteRtpHeader(client, rtpPacket, nalTimestamp, marker, 96, 2 + fragmentSize);
                rtpPacket[12] = fuIndicator;
                rtpPacket[13] = fuHeader;
                Buffer.BlockCopy(nalUnit, dataOffset, rtpPacket, 14, fragmentSize);

                await _transportManager.SendDataAsync(client, rtpPacket).ConfigureAwait(false);
                dataOffset += fragmentSize;
                remainingData -= fragmentSize;
                isFirstFragment = false;
            }
        }
    }

    /// <summary>
    /// Builds RTP packets for an H.264 NAL unit and appends them to the provided list
    /// instead of sending immediately. This enables batch sending (single syscall per frame).
    /// </summary>
    public void BuildH264NalRtpPackets(Client client, byte[] nalUnit, uint nalTimestamp, bool lastFrame, List<byte[]> outPackets)
    {
        int nalStart = 0;
        if (nalUnit.Length >= 4 && nalUnit[0] == 0 && nalUnit[1] == 0 && nalUnit[2] == 0 && nalUnit[3] == 1)
            nalStart = 4;
        else if (nalUnit.Length >= 3 && nalUnit[0] == 0 && nalUnit[1] == 0 && nalUnit[2] == 1)
            nalStart = 3;

        int nalLength = nalUnit.Length - nalStart;
        if (nalLength <= 0) return;

        if (nalLength <= MaxPayloadSize)
        {
            outPackets.Add(BuildRtpPacket(client, nalUnit, nalStart, nalLength, nalTimestamp, lastFrame, 96));
        }
        else
        {
            byte nalHeader = nalUnit[nalStart];
            byte nalType = (byte)(nalHeader & 0x1F);
            byte fuIndicator = (byte)((nalHeader & 0x60) | 28);

            int dataOffset = nalStart + 1;
            int remainingData = nalLength - 1;
            bool isFirstFragment = true;

            while (remainingData > 0)
            {
                int fragmentSize = Math.Min(MaxPayloadSize - 2, remainingData);
                bool isLastFragment = fragmentSize == remainingData;
                bool marker = lastFrame && isLastFragment;

                byte fuHeader = nalType;
                if (isFirstFragment) fuHeader |= 0x80;
                if (isLastFragment) fuHeader |= 0x40;

                var rtpPacket = new byte[14 + fragmentSize];
                WriteRtpHeader(client, rtpPacket, nalTimestamp, marker, 96, 2 + fragmentSize);
                rtpPacket[12] = fuIndicator;
                rtpPacket[13] = fuHeader;
                Buffer.BlockCopy(nalUnit, dataOffset, rtpPacket, 14, fragmentSize);

                outPackets.Add(rtpPacket);
                dataOffset += fragmentSize;
                remainingData -= fragmentSize;
                isFirstFragment = false;
            }
        }
    }

    /// <summary>
    /// Builds an RTP packet by copying directly from a source byte array at a given offset,
    /// eliminating the need for an intermediate payload buffer.
    /// </summary>
    private byte[] BuildRtpPacket(Client client, byte[] sourceData, int srcOffset, int srcLength, uint timestamp, bool marker, byte payloadType)
    {
        var packet = new byte[12 + srcLength];
        WriteRtpHeader(client, packet, timestamp, marker, payloadType, srcLength);
        Buffer.BlockCopy(sourceData, srcOffset, packet, 12, srcLength);
        return packet;
    }

    /// <summary>
    /// Writes the 12-byte RTP fixed header into a pre-allocated buffer and updates per-packet counters.
    /// Caller is responsible for writing the payload starting at offset 12.
    /// </summary>
    private void WriteRtpHeader(Client client, byte[] packet, uint timestamp, bool marker, byte payloadType, int payloadLength)
    {
        packet[0] = 0x80; // V=2, P=0, X=0, CC=0
        packet[1] = (byte)(marker ? 0x80 | payloadType : payloadType);

        ushort seqNum;
        lock (client)
        {
            seqNum = client.SequenceNumber++;
            client.LastRtpTimestampSent = timestamp;
            client.PacketCount++;
            client.OctetCount += (uint)payloadLength;
        }
        packet[2] = (byte)(seqNum >> 8);
        packet[3] = (byte)(seqNum & 0xFF);
        packet[4] = (byte)(timestamp >> 24);
        packet[5] = (byte)(timestamp >> 16);
        packet[6] = (byte)(timestamp >> 8);
        packet[7] = (byte)(timestamp & 0xFF);
        packet[8]  = (byte)(client.SsrcId >> 24);
        packet[9]  = (byte)(client.SsrcId >> 16);
        packet[10] = (byte)(client.SsrcId >> 8);
        packet[11] = (byte)(client.SsrcId & 0xFF);
    }

    /// <inheritdoc/>
    public async Task SendJpegAsRtpAsync(Client client, byte[] jpegData)
    {
        int offset = 0;

        while (offset < jpegData.Length && !_transportManager.CancellationToken.IsCancellationRequested)
        {
            // Determine header size based on whether this is the first fragment
            int headerSize = (offset == 0) ? 140 : 8; // 8 + 4 + 128 for first fragment

            // Calculate how much JPEG data we can fit in this packet
            int jpegDataSize = Math.Min(MaxPayloadSize - headerSize, jpegData.Length - offset);

            // Total payload size is header + jpeg data
            int totalPayloadSize = headerSize + jpegDataSize;

            bool isLastFragment = (offset + jpegDataSize) >= jpegData.Length;

            // Create buffer for the exact payload size needed
            var payload = new byte[totalPayloadSize];

            // Width and height in 8-pixel blocks
            var widthInBlocks = (byte)(client.Width / 8);
            var heightInBlocks = (byte)(client.Height / 8);

            // Ensure we don't send 0 dimensions
            if (widthInBlocks == 0) widthInBlocks = 160; // Default 1280/8
            if (heightInBlocks == 0) heightInBlocks = 90;  // Default 720/8

            // Type-specific header (first 8 bytes) - common for all fragments
            payload[0] = 0; // Type-specific
            payload[1] = (byte)((offset >> 16) & 0xFF);
            payload[2] = (byte)((offset >> 8) & 0xFF);
            payload[3] = (byte)(offset & 0xFF);

            if (offset == 0)
            {
                // First fragment - includes quantization tables
                payload[4] = 0;   // Type (0 = includes quantization tables)
                payload[5] = 255; // Q value (255 = dynamic tables)
                payload[6] = widthInBlocks;
                payload[7] = heightInBlocks;

                // Quantization table header (4 bytes)
                payload[8] = 0;   // MBZ
                payload[9] = 0;   // Precision
                payload[10] = 0;  // Length MSB
                payload[11] = 128; // Length LSB (128 bytes of quant tables)

                // Copy quantization tables (128 bytes)
                Buffer.BlockCopy(_standardQuantizationTables, 0, payload, 12, 128);

                // Copy JPEG data after the quantization tables
                Buffer.BlockCopy(jpegData, offset, payload, 140, jpegDataSize);
            }
            else
            {
                // Subsequent fragments - no quantization tables
                payload[4] = 1;   // Type (1 = no quantization tables)
                payload[5] = 255; // Q value
                payload[6] = widthInBlocks;
                payload[7] = heightInBlocks;

                // Copy JPEG data directly after the 8-byte header
                Buffer.BlockCopy(jpegData, offset, payload, 8, jpegDataSize);
            }

            // Create RTP packet with old timestamp method (uses client.RtpTimestamp)
            var rtpPacket = CreateRtpPacketOld(client, payload, isLastFragment, 26);

            // Send via appropriate transport
            await _transportManager.SendDataAsync(client, rtpPacket).ConfigureAwait(false);

            // Move offset by the amount of JPEG data we just sent
            offset += jpegDataSize;
        }
    }

    /// <summary>
    /// Creates an RTP packet using the client's RtpTimestamp property (used by MJPEG).
    /// </summary>
    private byte[] CreateRtpPacketOld(Client client, byte[] payload, bool marker, byte payloadType)
    {
        uint ts;
        lock (client) { ts = client.RtpTimestamp; }
        var packet = new byte[12 + payload.Length];
        WriteRtpHeader(client, packet, ts, marker, payloadType, payload.Length);
        Buffer.BlockCopy(payload, 0, packet, 12, payload.Length);
        return packet;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Uses wall-clock time (Stopwatch) instead of encoder timestamps for RTP clock
    /// derivation. MediaTek MT6768 (and possibly other SoCs) report PresentationTimeUs
    /// in units ~1000x larger than documented microseconds, causing RTP timestamp deltas
    /// of ~3,000,000 per frame instead of the expected ~3,600 (at 25fps/90kHz).
    /// Wall-clock based timestamps are robust regardless of encoder timestamp units.
    /// BaseEncoderTimestamp is repurposed to store the Stopwatch start tick.
    /// </remarks>
    public uint EncoderTimestampToRtp(ulong encoderTimestamp, ref Client client)
    {
        lock (client)
        {
            if (client.BaseEncoderTimestamp == 0)
            {
                client.BaseEncoderTimestamp = (ulong)Stopwatch.GetTimestamp();
                client.BaseRtpTimestamp = client.RtpTimestamp;
                return client.BaseRtpTimestamp;
            }

            long elapsedTicks = Stopwatch.GetTimestamp() - (long)client.BaseEncoderTimestamp;
            double seconds = (double)elapsedTicks / Stopwatch.Frequency;
            ulong rtpAdd = (ulong)(seconds * 90000.0 + 0.5);
            return (uint)(client.BaseRtpTimestamp + rtpAdd);
        }
    }

    /// <inheritdoc/>
    public byte[] GetStandardQuantizationTables() => _standardQuantizationTables;

    private static byte[] CreateStandardQuantizationTables()
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
}
