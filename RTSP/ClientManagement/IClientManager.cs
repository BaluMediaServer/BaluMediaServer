using BaluMediaServer.Models;
using BaluMediaServer.Services;

namespace BaluMediaServer.RTSP.ClientManagement;

/// <summary>
/// Interface for managing client lifecycle.
/// </summary>
public interface IClientManager
{
    /// <summary>
    /// Event raised when client count changes.
    /// </summary>
    event EventHandler<int>? ClientCountChanged;

    /// <summary>
    /// Event raised when the client list changes.
    /// </summary>
    event Action<List<Client>>? OnClientsChange;

    /// <summary>
    /// Adds a new client.
    /// </summary>
    /// <param name="client">The client.</param>
    /// <returns>True if client was added.</returns>
    bool AddClient(Client client);

    /// <summary>
    /// Removes a client by ID.
    /// </summary>
    /// <param name="clientId">The client ID.</param>
    /// <returns>True if client was removed.</returns>
    bool RemoveClient(string clientId);

    /// <summary>
    /// Cleans up a client and releases resources.
    /// </summary>
    /// <param name="client">The client.</param>
    void CleanupClient(Client client);

    /// <summary>
    /// Gets all active clients.
    /// </summary>
    /// <returns>Collection of active clients.</returns>
    IReadOnlyCollection<Client> GetActiveClients();

    /// <summary>
    /// Gets a client by ID.
    /// </summary>
    /// <param name="clientId">The client ID.</param>
    /// <returns>The client, or null if not found.</returns>
    Client? GetClient(string clientId);

    /// <summary>
    /// Gets the current client count.
    /// </summary>
    int ClientCount { get; }

    /// <summary>
    /// Gets the count of actively playing clients.
    /// </summary>
    int PlayingClientCount { get; }

    /// <summary>
    /// Gets dead clients that should be cleaned up.
    /// </summary>
    /// <returns>List of dead clients.</returns>
    List<Client> GetDeadClients();

    /// <summary>
    /// Clears all clients.
    /// </summary>
    void ClearAllClients();

    /// <summary>
    /// Caches SPS/PPS for a client.
    /// </summary>
    /// <param name="clientId">The client ID.</param>
    /// <param name="sps">The SPS.</param>
    /// <param name="pps">The PPS.</param>
    void CacheSpsPps(string clientId, byte[]? sps, byte[]? pps);

    /// <summary>
    /// Gets cached SPS/PPS for a client.
    /// </summary>
    /// <param name="clientId">The client ID.</param>
    /// <returns>Tuple of SPS and PPS.</returns>
    (byte[]? sps, byte[]? pps) GetCachedSpsPps(string clientId);

    /// <summary>
    /// Checks if SPS/PPS is cached for a client.
    /// </summary>
    /// <param name="clientId">The client ID.</param>
    /// <returns>True if cached.</returns>
    bool HasCachedSpsPps(string clientId);

    /// <summary>
    /// Gets or creates a frame pacer for a client.
    /// </summary>
    /// <param name="clientId">The client ID.</param>
    /// <param name="targetFps">The target FPS.</param>
    /// <returns>The frame pacer.</returns>
    FramePacer GetOrCreatePacer(string clientId, int targetFps = 25);

    /// <summary>
    /// Gets whether any connected client is using MJPEG codec.
    /// </summary>
    bool HasMjpegClients { get; }
}
