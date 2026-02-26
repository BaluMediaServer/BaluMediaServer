using System.Collections.Concurrent;
using Android.Util;
using BaluMediaServer.Models;
using BaluMediaServer.RTSP.Transport;
using BaluMediaServer.Services;

namespace BaluMediaServer.RTSP.ClientManagement;

/// <summary>
/// Manages client lifecycle, connection tracking, and per-client caches.
/// </summary>
public class ClientManager : IClientManager
{
    private readonly ConcurrentDictionary<string, Client> _clients = new();
    private readonly Dictionary<string, byte[]?> _clientSpsCache = new();
    private readonly Dictionary<string, byte[]?> _clientPpsCache = new();
    private readonly ConcurrentDictionary<string, FramePacer> _clientPacers = new();
    private readonly ITransportManager _transportManager;

    /// <inheritdoc/>
    public event EventHandler<int>? ClientCountChanged;

    /// <inheritdoc/>
    public event Action<List<Client>>? OnClientsChange;

    /// <summary>
    /// Creates a new ClientManager.
    /// </summary>
    /// <param name="transportManager">The transport manager.</param>
    public ClientManager(ITransportManager transportManager)
    {
        _transportManager = transportManager;
    }

    /// <summary>
    /// Safely invokes client change events, catching any subscriber exceptions.
    /// </summary>
    private void SafeInvokeClientChange()
    {
        try
        {
            ClientCountChanged?.Invoke(this, _clients.Count);
        }
        catch (Exception ex)
        {
            Log.Error("[ClientManager]", $"ClientCountChanged subscriber error: {ex.Message}");
        }
        try
        {
            OnClientsChange?.Invoke(_clients.Values.ToList());
        }
        catch (Exception ex)
        {
            Log.Error("[ClientManager]", $"OnClientsChange subscriber error: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public int ClientCount => _clients.Count;

    /// <inheritdoc/>
    public int PlayingClientCount => _clients.Values.Count(p => p.IsPlaying);

    /// <inheritdoc/>
    public bool AddClient(Client client)
    {
        var result = _clients.TryAdd(client.Id, client);
        if (result)
        {
            SafeInvokeClientChange();
        }
        return result;
    }

    /// <inheritdoc/>
    public bool RemoveClient(string clientId)
    {
        var result = _clients.TryRemove(clientId, out _);
        if (result)
        {
            SafeInvokeClientChange();
        }
        return result;
    }

    /// <inheritdoc/>
    public void CleanupClient(Client client)
    {
        try
        {
            lock (client)
            {
                // Set IsPlaying=false FIRST so any running streaming loop exits
                // before we dispose resources it depends on (SendLock, sockets, etc.)
                client.IsPlaying = false;

                _transportManager.ReleaseClientPorts(client);
                _clientSpsCache.Remove(client.Id);
                _clientPpsCache.Remove(client.Id);
                _clientPacers.TryRemove(client.Id, out _);
                _clients.TryRemove(client.Id, out _);
                client.Dispose();
            }
            SafeInvokeClientChange();
            Log.Info("[ClientManager]", $"Client {client.Id} cleaned up");
        }
        catch (Exception ex)
        {
            Log.Error("[ClientManager]", $"Error cleaning up client: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<Client> GetActiveClients() => _clients.Values.ToList().AsReadOnly();

    /// <inheritdoc/>
    public Client? GetClient(string clientId)
    {
        _clients.TryGetValue(clientId, out var client);
        return client;
    }

    /// <inheritdoc/>
    public List<Client> GetDeadClients()
    {
        // DEBUG MODE: Disable all client cleanup to isolate streaming issues.
        // No clients are ever marked as dead — lifecycle management is fully disabled.
        return new List<Client>();
    }

    /// <inheritdoc/>
    public void ClearAllClients()
    {
        foreach (var client in _clients.Values.ToList())
        {
            try
            {
                // Cleanup without triggering events (we'll do it once at the end)
                lock (client)
                {
                    _transportManager.ReleaseClientPorts(client);
                    _clientSpsCache.Remove(client.Id);
                    _clientPpsCache.Remove(client.Id);
                    _clientPacers.TryRemove(client.Id, out _);
                    _clients.TryRemove(client.Id, out _);
                    client.Dispose();
                }
            }
            catch (Exception ex)
            {
                Log.Error("[ClientManager]", $"Error cleaning up client {client?.Id}: {ex.Message}");
            }
        }
        _clients.Clear();
        _clientSpsCache.Clear();
        _clientPpsCache.Clear();
        _clientPacers.Clear();
        SafeInvokeClientChange();
    }

    /// <inheritdoc/>
    public void CacheSpsPps(string clientId, byte[]? sps, byte[]? pps)
    {
        _clientSpsCache[clientId] = sps;
        _clientPpsCache[clientId] = pps;
    }

    /// <inheritdoc/>
    public (byte[]? sps, byte[]? pps) GetCachedSpsPps(string clientId)
    {
        _clientSpsCache.TryGetValue(clientId, out var sps);
        _clientPpsCache.TryGetValue(clientId, out var pps);
        return (sps, pps);
    }

    /// <inheritdoc/>
    public bool HasCachedSpsPps(string clientId)
    {
        return _clientSpsCache.ContainsKey(clientId);
    }

    /// <inheritdoc/>
    public FramePacer GetOrCreatePacer(string clientId, int targetFps = 25)
    {
        return _clientPacers.GetOrAdd(clientId, _ => new FramePacer(targetFps));
    }

    /// <summary>
    /// Notifies listeners about client changes.
    /// </summary>
    public void NotifyClientsChanged()
    {
        try
        {
            OnClientsChange?.Invoke(_clients.Values.ToList());
        }
        catch (Exception ex)
        {
            Log.Error("[ClientManager]", $"NotifyClientsChanged subscriber error: {ex.Message}");
        }
    }
}
