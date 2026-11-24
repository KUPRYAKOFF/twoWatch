using System.Net;
using System.Net.Http.Json;
using CinemaTogether.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace CinemaTogether.Tests;

public sealed class ProxyHeadersTests
{
    private sealed record ObservedRequest(string Scheme, string Host, string? Remote, bool SameOrigin, bool Organizer);

    private static async Task<WebApplication> Server(string remote)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
            return next(context);
        });
        app.UseLocalProxyHeaders();
        app.Run(context => context.Response.WriteAsJsonAsync(new ObservedRequest(context.Request.Scheme,
            context.Request.Host.ToString(), context.Connection.RemoteIpAddress?.ToString(),
            LocalProxyHeaders.HasSameOrigin(context.Request), LocalProxyHeaders.IsLocalOrganizerRequest(context, 8080))));
        await app.StartAsync();
        return app;
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public async Task LocalProxyRestoresHttpsOriginAndRealClient(string remote)
    {
        await using var server = await Server(remote); using var client = server.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:8080/");
        request.Headers.Add("X-Forwarded-Proto", "https"); request.Headers.Add("X-Forwarded-Host", "cinema.example");
        request.Headers.Add("X-Forwarded-For", "203.0.113.42"); request.Headers.Add("Origin", "https://cinema.example");
        using var response = await client.SendAsync(request);
        var seen = (await response.Content.ReadFromJsonAsync<ObservedRequest>())!;
        Assert.Equal("https", seen.Scheme); Assert.Equal("cinema.example", seen.Host);
        Assert.Equal("203.0.113.42", seen.Remote); Assert.True(seen.SameOrigin); Assert.False(seen.Organizer);
    }

    [Fact]
    public async Task RemoteClientCannotSpoofProxyHeaders()
    {
        await using var server = await Server("198.51.100.20"); using var client = server.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://192.168.0.11:8080/");
        request.Headers.Add("X-Forwarded-Proto", "https"); request.Headers.Add("X-Forwarded-Host", "cinema.example");
        request.Headers.Add("X-Forwarded-For", "127.0.0.1"); request.Headers.Add("Origin", "https://cinema.example");
        using var response = await client.SendAsync(request);
        var seen = (await response.Content.ReadFromJsonAsync<ObservedRequest>())!;
        Assert.Equal("http", seen.Scheme); Assert.Equal("192.168.0.11:8080", seen.Host);
        Assert.Equal("198.51.100.20", seen.Remote); Assert.False(seen.SameOrigin); Assert.False(seen.Organizer);
    }

    [Theory]
    [InlineData("http://localhost:8080/", null, null, true)]
    [InlineData("http://127.0.0.1:8080/", null, null, true)]
    [InlineData("http://cinema.example:8080/", null, null, false)]
    [InlineData("http://127.0.0.1:8080/", "https", null, false)]
    [InlineData("http://127.0.0.1:8080/", null, "127.0.0.1", false)]
    public async Task OrganizerBootstrapRequiresDirectLocalRequest(string uri, string? proto, string? forwardedFor, bool allowed)
    {
        await using var server = await Server("127.0.0.1"); using var client = server.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (proto is not null) request.Headers.Add("X-Forwarded-Proto", proto);
        if (forwardedFor is not null) request.Headers.Add("X-Forwarded-For", forwardedFor);
        using var response = await client.SendAsync(request);
        Assert.Equal(allowed, (await response.Content.ReadFromJsonAsync<ObservedRequest>())!.Organizer);
    }

    [Theory]
    [InlineData("https://cinema.example", true)]
    [InlineData("https://CINEMA.example:443", true)]
    [InlineData("https://cinema.example:8443", false)]
    [InlineData("http://cinema.example", false)]
    [InlineData("https://other.example", false)]
    [InlineData("null", false)]
    public void OriginComparisonPreservesSchemeHostAndPort(string origin, bool allowed)
    {
        var context = new DefaultHttpContext(); context.Request.Scheme = "https";
        context.Request.Host = new HostString("cinema.example", 443); context.Request.Headers.Origin = origin;
        Assert.Equal(allowed, LocalProxyHeaders.HasSameOrigin(context.Request));
    }
}
