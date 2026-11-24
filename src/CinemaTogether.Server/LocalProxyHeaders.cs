using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace CinemaTogether.Server;

/// <summary>Accept TLS termination metadata only from a proxy on this computer.</summary>
public static class LocalProxyHeaders
{
    private static readonly object ProxiedRequest = new();

    public static IApplicationBuilder UseLocalProxyHeaders(this IApplicationBuilder app)
    {
        // Record this before ForwardedHeaders consumes the original headers. Even a proxy
        // that reports a loopback client must never expose the organizer bootstrap route.
        app.Use((context, next) =>
        {
            context.Items[ProxiedRequest] = context.Request.Headers.ContainsKey("X-Forwarded-For")
                || context.Request.Headers.ContainsKey("X-Forwarded-Host")
                || context.Request.Headers.ContainsKey("X-Forwarded-Proto")
                || context.Request.Headers.ContainsKey("Forwarded");
            return next(context);
        });

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1
        };
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        options.KnownProxies.Add(IPAddress.Loopback);
        options.KnownProxies.Add(IPAddress.IPv6Loopback);
        options.KnownProxies.Add(IPAddress.Loopback.MapToIPv6());
        return app.UseForwardedHeaders(options);
    }

    public static bool HasSameOrigin(HttpRequest request)
    {
        if (request.Headers.Origin.Count == 0) return true;
        if (!Uri.TryCreate(request.Headers.Origin.ToString(), UriKind.Absolute, out var origin)
            || !Uri.TryCreate($"{request.Scheme}://{request.Host}", UriKind.Absolute, out var target)) return false;
        return origin.UserInfo.Length == 0 && origin.AbsolutePath == "/" && origin.Query.Length == 0 && origin.Fragment.Length == 0
            && origin.Scheme.Equals(target.Scheme, StringComparison.OrdinalIgnoreCase)
            && origin.IdnHost.Equals(target.IdnHost, StringComparison.OrdinalIgnoreCase)
            && origin.Port == target.Port;
    }

    public static bool IsLocalOrganizerRequest(HttpContext context, int serverPort)
    {
        if (context.Items.TryGetValue(ProxiedRequest, out var proxied) && proxied is true) return false;
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote)) return false;
        var host = context.Request.Host.Host.Trim('[', ']');
        var isLocalHost = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address));
        var port = context.Request.Host.Port ?? (context.Request.IsHttps ? 443 : 80);
        return isLocalHost && port == serverPort;
    }
}
