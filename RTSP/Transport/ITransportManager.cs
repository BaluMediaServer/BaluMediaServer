using System.Net.Sockets;
using BaluMediaServer.Models;

namespace BaluMediaServer.RTSP.Transport;

/// <summary>
/// Interface for managing RTP/RTCP transport (UDP and TCP interleaved).
/// </summary>
public interface ITransportManager
{
    /// <summary>
    /// Sends data via the appropriate transport (UDP or TCP interleaved).
    /// </summary>
    /// <param name="client">The client.</param>
    /// <param name="data">The data to send.</param>
    /// <returns>True if send was successful.</returns>
    Task<bool> SendDataAsync(Client client, byte[] data);

    /// <summary>
    /// Sends multiple RTP packets in a single batch for reduced syscall/lock overhead.
    /// </summary>
    Task<bool> SendBatchAsync(Client client, List<byte[]> packets);

    /// <summary>
    /// Synchronous version of <see cref="SendBatchAsync"/> for use on dedicated OS threads.
    /// Acquires the SendLock with <c>SemaphoreSlim.Wait()</c> and sends via <c>Socket.Send()</c>
    /// so the calling thread never yields to the thread pool — eliminating the 30–40ms async
    /// continuation scheduling delay seen on Android.
    /// </summary>
    bool SendBatchSync(Client client, List<byte[]> packets);

    /// <summary>
    /// Synchronously sends RTP packets for the <em>audio</em> track (trackID=1). Uses
    /// <see cref="Client.AudioRtpChannel"/> for TCP interleaved sessions and
    /// <see cref="Client.AudioUdpSocket"/> / <see cref="Client.AudioRtpEndPoint"/> for UDP.
    /// Shares the per-client <see cref="Client.SendLock"/> so audio and video sends are
    /// serialized — required to prevent interleaved-framing corruption over TCP.
    /// </summary>
    bool SendAudioBatchSync(Client client, List<byte[]> packets);

    /// <summary>
    /// Sends data via TCP interleaved transport.
    /// </summary>
    /// <param name="socket">The socket.</param>
    /// <param name="channel">The channel number.</param>
    /// <param name="rtpPacket">The RTP packet.</param>
    /// <param name="client">The client (optional).</param>
    /// <returns>True if send was successful.</returns>
    Task<bool> SendInterleavedDataAsync(Socket socket, byte channel, byte[] rtpPacket, Client? client = null);

    /// <summary>
    /// Sends data via UDP transport.
    /// </summary>
    /// <param name="client">The client.</param>
    /// <param name="rtpPacket">The RTP packet.</param>
    /// <param name="isRtcp">True if sending RTCP data.</param>
    /// <returns>True if send was successful.</returns>
    Task<bool> SendUdpDataAsync(Client client, byte[] rtpPacket, bool isRtcp);

    /// <summary>
    /// Gets an available port for UDP transport.
    /// </summary>
    /// <returns>The available port number.</returns>
    int GetAvailablePort();

    /// <summary>
    /// Releases ports used by a client.
    /// </summary>
    /// <param name="client">The client.</param>
    void ReleaseClientPorts(Client client);

    /// <summary>
    /// Parses a Transport header.
    /// </summary>
    /// <param name="transport">The Transport header value.</param>
    /// <returns>Dictionary of transport parameters.</returns>
    Dictionary<string, string> ParseTransport(string transport);

    /// <summary>
    /// Checks if a socket is connected.
    /// </summary>
    /// <param name="socket">The socket.</param>
    /// <returns>True if socket is connected.</returns>
    bool IsSocketConnected(Socket? socket);

    /// <summary>
    /// Gets the cancellation token for transport operations.
    /// </summary>
    CancellationToken CancellationToken { get; }
}
