using BaluMediaServer.Models;

namespace BaluMediaServer.RTSP.Streaming;

/// <summary>
/// Interface for main streaming orchestration.
/// </summary>
public interface IStreamingController
{
    /// <summary>
    /// Starts streaming to a client.
    /// </summary>
    /// <param name="client">The client.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task StreamToClientAsync(Client client, CancellationToken cancellationToken);

    /// <summary>
    /// Gets or sets whether streaming is currently active.
    /// </summary>
    bool IsStreaming { get; }

    /// <summary>
    /// Event raised when streaming state changes.
    /// </summary>
    event EventHandler<bool>? StreamingStateChanged;
}
