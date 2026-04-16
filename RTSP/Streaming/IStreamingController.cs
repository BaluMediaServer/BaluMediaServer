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
    /// Gets whether streaming is currently active.
    /// Backed by an <see cref="System.Threading.Interlocked"/>-managed integer flag;
    /// reading this property is safe from any thread.
    /// </summary>
    bool IsStreaming { get; }

    /// <summary>
    /// Event raised when streaming state changes.
    /// </summary>
    event EventHandler<bool>? StreamingStateChanged;
}
