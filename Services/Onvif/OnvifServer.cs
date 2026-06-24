using System.Net;
using System.Text;
using BaluMediaServer.Repositories;

namespace BaluMediaServer.Services.Onvif;

/// <summary>
/// HTTP transport shell for the ONVIF Device + Media SOAP services. Mirrors the
/// <see cref="MjpegServer"/> <see cref="HttpListener"/> pattern (including the Android
/// <c>0.0.0.0</c>→<c>*</c> prefix fix) and the <c>GetContextAsync</c> accept loop. Each request is
/// read as a SOAP envelope, the operation is parsed, the WS-UsernameToken is validated, and the
/// matching service builds the response. All response/SOAP *logic* lives in the Android-free
/// <see cref="OnvifDeviceService"/> / <see cref="OnvifMediaService"/> / <see cref="OnvifSecurity"/>
/// classes; this shell only does I/O.
///
/// Serves <c>http://&lt;ip&gt;:&lt;port&gt;/onvif/device_service</c> and <c>/onvif/media_service</c>.
/// </summary>
public sealed class OnvifServer : IDisposable
{
    private const string Tag = "ONVIF";

    private readonly HttpListener _listener;
    private readonly int _port;
    private readonly OnvifDeviceService _device;
    private readonly OnvifMediaService _media;
    private readonly OnvifSecurity _security;

    private CancellationTokenSource _cts = new();
    private Task? _listenTask;
    private volatile bool _running;

    public OnvifServer(
        int port,
        string bindAddress,
        OnvifDeviceContext ctx,
        Func<IReadOnlyList<OnvifProfile>> getProfiles,
        bool requireAuth,
        Func<string, string?> getPassword)
    {
        _port = port;
        _device = new OnvifDeviceService(ctx);
        _media = new OnvifMediaService(ctx, getProfiles);
        _security = new OnvifSecurity(requireAuth, getPassword);

        _listener = new HttpListener();
        // Same Android-compat prefix handling as MjpegServer: "0.0.0.0" must become "*".
        string prefix = bindAddress switch
        {
            "0.0.0.0" => "*",
            "127.0.0.1" => "localhost",
            _ => bindAddress,
        };
        _listener.Prefixes.Add($"http://{prefix}:{_port}/onvif/");
    }

    public bool IsRunning => _running;

    public void Start()
    {
        if (_running) return;
        _cts = new CancellationTokenSource();
        try
        {
            _listener.Start();
        }
        catch (Exception ex)
        {
            BaluLogger.Error(Tag, $"Failed to start ONVIF HTTP listener on port {_port}: {ex.Message}");
            return;
        }
        _running = true;
        _listenTask = Task.Run(ListenLoop, _cts.Token);
        BaluLogger.Info(Tag, $"ONVIF service listening on http://*:{_port}/onvif/");
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch (Exception ex) { BaluLogger.Warn(Tag, $"Stop error: {ex.Message}"); }
        BaluLogger.Info(Tag, "ONVIF service stopped");
    }

    public void Dispose()
    {
        Stop();
        try { _listener.Close(); } catch { }
    }

    private async Task ListenLoop()
    {
        while (_running && !_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (_cts.IsCancellationRequested || !_running)
            {
                break;
            }
            catch (Exception ex)
            {
                BaluLogger.Warn(Tag, $"GetContext error: {ex.Message}");
                continue;
            }
            _ = Task.Run(() => HandleRequest(context));
        }
    }

    private async Task HandleRequest(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            string requestBody;
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
                requestBody = await reader.ReadToEndAsync().ConfigureAwait(false);

            var (operation, body, header) = OnvifSoap.ParseOperation(requestBody);
            var path = request.Url?.AbsolutePath ?? string.Empty;
            bool isMediaPath = path.Contains("media", StringComparison.OrdinalIgnoreCase);

            string responseXml;
            bool isFault = false;

            if (string.IsNullOrEmpty(operation))
            {
                responseXml = OnvifSoap.Fault("Malformed or empty SOAP request");
                isFault = true;
            }
            else if (!_security.IsAuthorized(operation, header))
            {
                BaluLogger.Warn(Tag, $"Unauthorized ONVIF request: {operation}");
                responseXml = OnvifSoap.Fault("Sender not authorized", "NotAuthorized");
                isFault = true;
            }
            else
            {
                // Dispatch by endpoint, but fall back to the other service so minimal clients that
                // POST every operation to a single endpoint still work.
                var inner = isMediaPath ? _media.Handle(operation, body) : _device.Handle(operation, DateTime.UtcNow);
                inner ??= isMediaPath ? _device.Handle(operation, DateTime.UtcNow) : _media.Handle(operation, body);

                if (inner is null)
                {
                    BaluLogger.Warn(Tag, $"Unsupported ONVIF operation: {operation}");
                    responseXml = OnvifSoap.Fault($"Operation '{operation}' not supported", "ActionNotSupported");
                    isFault = true;
                }
                else
                {
                    BaluLogger.Debug(Tag, $"ONVIF {operation} -> {(isMediaPath ? "media" : "device")}");
                    responseXml = OnvifSoap.Envelope(inner);
                }
            }

            await WriteResponse(context.Response, responseXml, isFault).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            BaluLogger.Error(Tag, $"Request handling error: {ex.Message}");
            try { await WriteResponse(context.Response, OnvifSoap.Fault("Internal error"), isFault: true).ConfigureAwait(false); }
            catch { }
        }
    }

    private static async Task WriteResponse(HttpListenerResponse response, string xml, bool isFault)
    {
        var bytes = Encoding.UTF8.GetBytes(xml);
        // SOAP 1.2 sender faults map to HTTP 400; successful responses to 200.
        response.StatusCode = isFault ? 400 : 200;
        response.ContentType = "application/soap+xml; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        response.Headers.Add("Access-Control-Allow-Origin", "*");
        try
        {
            await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }
        finally
        {
            response.Close();
        }
    }
}
