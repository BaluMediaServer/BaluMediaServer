using BaluMediaServer.Models;

namespace BaluMediaServer.RTSP.Transport;

/// <summary>
/// Interface for managing RTCP sender reports and receiver feedback.
/// </summary>
public interface IRtcpManager
{
    /// <summary>
    /// Sends an RTCP Sender Report to the client.
    /// </summary>
    /// <param name="client">The client.</param>
    Task SendSenderReportAsync(Client client);

    /// <summary>
    /// Listens for RTCP packets on the client's RTCP port.
    /// </summary>
    /// <param name="client">The client.</param>
    Task ListenRtcpPortAsync(Client client);

    /// <summary>
    /// Handles an RTCP report from a client.
    /// </summary>
    /// <param name="client">The client.</param>
    /// <param name="rtcpData">The RTCP packet data.</param>
    void HandleRtcpReport(Client client, byte[] rtcpData);

    /// <summary>
    /// Event raised when a client should be cleaned up due to RTCP timeout or BYE.
    /// </summary>
    event EventHandler<Client>? ClientCleanupRequired;
}
