namespace MoonMovie.Core.Net;

/// <summary>
/// Goes through the system proxy (Clash and the like) while it answers, and straight out when it does not. A proxy
/// that was switched off after MoonMovie started, or quit without clearing the system setting, would otherwise fail
/// every request with "connection refused (127.0.0.1:7890)". After a refusal the proxy is skipped for a while, then
/// tried again, so switching it back on mid-session is picked up too.
/// </summary>
public sealed class ProxyFallbackHandler : HttpMessageHandler
{
    private const long RetryProxyAfterMs = 30_000;

    private readonly HttpMessageInvoker _proxied;
    private readonly HttpMessageInvoker _direct;
    private long _skipProxyUntil;

    /// <param name="create">Builds the handler; the argument says whether it uses the system proxy.</param>
    public ProxyFallbackHandler(Func<bool, SocketsHttpHandler> create)
    {
        var proxied = create(true);
        proxied.UseProxy = true;
        var direct = create(false);
        direct.UseProxy = false;
        _proxied = new HttpMessageInvoker(proxied);
        _direct = new HttpMessageInvoker(direct);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (Environment.TickCount64 >= Volatile.Read(ref _skipProxyUntil) && UsesProxy(request.RequestUri))
        {
            try
            {
                return await _proxied.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.ProxyTunnelError
                                                  && !ct.IsCancellationRequested)
            {
                // With a proxy set, a connection that fails is the one to the proxy itself.
                Volatile.Write(ref _skipProxyUntil, Environment.TickCount64 + RetryProxyAfterMs);
            }
        }

        return await _direct.SendAsync(request, ct).ConfigureAwait(false);
    }

    private static bool UsesProxy(Uri? uri)
    {
        if (uri is null) return false;
        try
        {
            var proxy = HttpClient.DefaultProxy;
            return !proxy.IsBypassed(uri) && proxy.GetProxy(uri) is { } target && target != uri;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            return true;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _proxied.Dispose();
            _direct.Dispose();
        }

        base.Dispose(disposing);
    }
}
