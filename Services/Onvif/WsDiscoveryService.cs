using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using BaluMediaServer.Repositories;
#if ANDROID
using Android.Content;
using Android.Net.Wifi;
#endif

namespace BaluMediaServer.Services.Onvif;

/// <summary>
/// WS-Discovery transport for ONVIF auto-discovery. Joins the multicast group
/// 239.255.255.250:3702, replies to client Probes with a ProbeMatch, and announces the device with
/// Hello on start / Bye on stop. The message content is built by the Android-free
/// <see cref="WsDiscoveryMessages"/>; this shell only owns the socket and (on Android) the
/// multicast lock.
///
/// Android note: receiving multicast requires a <see cref="WifiManager.MulticastLock"/> and the
/// <c>android.permission.CHANGE_WIFI_MULTICAST_STATE</c> permission in the host manifest. Without
/// it, outbound Hello/ProbeMatch still go out but inbound Probes are dropped by the OS.
/// </summary>
public sealed class WsDiscoveryService : IDisposable
{
    private const string Tag = "ONVIF-WSD";
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("239.255.255.250");
    private const int DiscoveryPort = 3702;

    private readonly OnvifDeviceContext _ctx;
    private readonly IReadOnlyList<string> _scopes;
    private readonly string _deviceUuid;

    private UdpClient? _udp;
    private CancellationTokenSource _cts = new();
    private Task? _listenTask;
    private volatile bool _running;

#if ANDROID
    private WifiManager.MulticastLock? _multicastLock;
#endif

    public WsDiscoveryService(OnvifDeviceContext ctx)
    {
        _ctx = ctx;
        _scopes = OnvifDeviceService.BuildScopes(ctx);
        _deviceUuid = StableUuid(ctx.SerialNumber, ctx.HardwareId);
    }

    /// <summary>Stable urn:uuid for this device (Endpoint Reference address). Persists across restarts.</summary>
    public string DeviceUuid => _deviceUuid;

    public void Start()
    {
        if (_running) return;
        _cts = new CancellationTokenSource();

        try
        {
            AcquireMulticastLock();

            _udp = new UdpClient();
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            _udp.JoinMulticastGroup(MulticastAddress);

            _running = true;
            _listenTask = Task.Run(ListenLoop, _cts.Token);

            SendMulticast(WsDiscoveryMessages.BuildHello(_deviceUuid, _ctx.DeviceServiceUri, _scopes, NewMessageId()));
            BaluLogger.Info(Tag, $"WS-Discovery started (uuid={_deviceUuid}, xaddr={_ctx.DeviceServiceUri})");
        }
        catch (Exception ex)
        {
            BaluLogger.Error(Tag, $"Failed to start WS-Discovery: {ex.Message}");
            Stop();
        }
    }

    public void Stop()
    {
        if (!_running && _udp is null) return;
        _running = false;

        try { SendMulticast(WsDiscoveryMessages.BuildBye(_deviceUuid, _ctx.DeviceServiceUri, _scopes, NewMessageId())); }
        catch (Exception ex) { BaluLogger.Warn(Tag, $"Bye send error: {ex.Message}"); }

        try { _cts.Cancel(); } catch { }
        try { _udp?.DropMulticastGroup(MulticastAddress); } catch { }
        try { _udp?.Close(); } catch { }
        _udp = null;

        ReleaseMulticastLock();
        BaluLogger.Info(Tag, "WS-Discovery stopped");
    }

    public void Dispose() => Stop();

    private async Task ListenLoop()
    {
        while (_running && !_cts.IsCancellationRequested && _udp is not null)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udp.ReceiveAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception) when (_cts.IsCancellationRequested || !_running)
            {
                break;
            }
            catch (Exception ex)
            {
                BaluLogger.Warn(Tag, $"Receive error: {ex.Message}");
                continue;
            }

            try
            {
                var xml = Encoding.UTF8.GetString(result.Buffer);
                var (action, messageId) = WsDiscoveryMessages.Parse(xml);
                if (action == WsDiscoveryMessages.ActionProbe)
                {
                    var reply = WsDiscoveryMessages.BuildProbeMatch(_deviceUuid, _ctx.DeviceServiceUri, _scopes, messageId, NewMessageId());
                    SendTo(reply, result.RemoteEndPoint);
                    BaluLogger.Debug(Tag, $"ProbeMatch -> {result.RemoteEndPoint}");
                }
            }
            catch (Exception ex)
            {
                BaluLogger.Warn(Tag, $"Probe handling error: {ex.Message}");
            }
        }
    }

    private void SendMulticast(string xml) => SendTo(xml, new IPEndPoint(MulticastAddress, DiscoveryPort));

    private void SendTo(string xml, IPEndPoint endpoint)
    {
        var bytes = Encoding.UTF8.GetBytes(xml);
        _udp?.Send(bytes, bytes.Length, endpoint);
    }

    private static string NewMessageId() => Guid.NewGuid().ToString();

    /// <summary>
    /// Derives a stable device UUID from the configured identity so the device keeps the same
    /// Endpoint Reference across restarts. Falls back to a random UUID if no serial/hardware id.
    /// </summary>
    private static string StableUuid(string serial, string hardwareId)
    {
        var seed = $"{serial}|{hardwareId}";
        if (string.IsNullOrWhiteSpace(serial) && string.IsNullOrWhiteSpace(hardwareId))
            return Guid.NewGuid().ToString();

        var hash = MD5.HashData(Encoding.UTF8.GetBytes("balu-onvif:" + seed));
        return new Guid(hash).ToString();
    }

    private void AcquireMulticastLock()
    {
#if ANDROID
        try
        {
            var wifi = (WifiManager?)Android.App.Application.Context.GetSystemService(Context.WifiService);
            _multicastLock = wifi?.CreateMulticastLock("BaluOnvifDiscovery");
            _multicastLock?.SetReferenceCounted(true);
            _multicastLock?.Acquire();
            if (_multicastLock is null)
                BaluLogger.Warn(Tag, "Could not obtain WifiManager multicast lock — inbound Probes may be dropped");
        }
        catch (Exception ex)
        {
            BaluLogger.Warn(Tag, $"Multicast lock acquire failed (need CHANGE_WIFI_MULTICAST_STATE?): {ex.Message}");
        }
#endif
    }

    private void ReleaseMulticastLock()
    {
#if ANDROID
        try { if (_multicastLock?.IsHeld == true) _multicastLock.Release(); }
        catch (Exception ex) { BaluLogger.Warn(Tag, $"Multicast lock release failed: {ex.Message}"); }
        _multicastLock = null;
#endif
    }
}
