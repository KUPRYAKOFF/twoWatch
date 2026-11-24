using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using CinemaTogether.Core;
using CinemaTogether.Infrastructure;
using CinemaTogether.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CinemaTogether.Tests;

public sealed class ServerTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "cinema-test-" + Guid.NewGuid().ToString("N"));
    private readonly string secret = new('a', 64);
    private WebApplication app = null!;
    private Uri url = null!;
    private MediaItem media = null!;
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(directory); using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        app = ServerHost.Build(new AppSettings { ListenAddress = "127.0.0.1", Port = port, PasswordHash = SettingsStore.HashPassword("secret1") }, directory, secret);
        var library = app.Services.GetRequiredService<MediaLibrary>(); await library.InitializeAsync();
        var path = Path.Combine(directory, "movie.mp4"); await File.WriteAllBytesAsync(path, Enumerable.Range(0, 2048).Select(i => (byte)(i % 256)).ToArray());
        media = await library.AddAsync(new("video", path, "Demo", 60, 2048, "h264", "aac", 640, 360, true, []));
        await app.StartAsync(); url = new Uri($"http://127.0.0.1:{port}");
    }
    public async Task DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    private (HttpClient Client, CookieContainer Cookies) Client() { var cookies = new CookieContainer(); return (new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = url }, cookies); }
    private async Task Host(HttpClient client) { using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/host"); request.Headers.Authorization = new("Bearer", secret); using var response = await client.SendAsync(request); response.EnsureSuccessStatusCode(); }
    private async Task Join(HttpClient client) { using var response = await client.PostAsJsonAsync("/api/auth/join", new { name = "Friend", password = "secret1" }); response.EnsureSuccessStatusCode(); }
    private HubConnection Connect(CookieContainer cookies) => new HubConnectionBuilder().WithUrl(new Uri(url, "/watch"), options => options.Cookies = cookies).Build();

    [Fact] public async Task FilesRequireAuthenticationAndRangeIsExact()
    {
        var (client, _) = Client(); using (client)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/stream/video")).StatusCode); await Join(client);
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/stream/video"); request.Headers.Range = new(100, 199); using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode); Assert.Equal(100, (await response.Content.ReadAsByteArrayAsync()).Length); Assert.Equal("bytes 100-199/2048", response.Content.Headers.ContentRange!.ToString());
            Assert.True(Interlocked.Read(ref app.Services.GetRequiredService<RoomCoordinator>().BytesSent) >= 100);
            var json = await client.GetStringAsync("/api/library"); Assert.DoesNotContain(media.Path, json); Assert.DoesNotContain("path", json, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/stream/not-in-library")).StatusCode);
        }
    }
    [Fact] public async Task SignalRChecksRightsRevocationAndBuffering()
    {
        var (hostClient, hostCookies) = Client(); var (viewerClient, viewerCookies) = Client(); using var h = hostClient; using var v = viewerClient;
        await Host(h); await Join(v); await using var host = Connect(hostCookies); await using var viewer = Connect(viewerCookies); await host.StartAsync(); await viewer.StartAsync();
        await host.InvokeAsync("Select", media.Id); await host.InvokeAsync("Buffer", false); await viewer.InvokeAsync("Buffer", false);
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() => viewer.InvokeAsync("Control", "play", 0d));
        var me = await v.GetFromJsonAsync<JsonElement>("/api/me"); var id = me.GetProperty("id").GetString()!;
        await host.InvokeAsync("Grant", id, true); await viewer.InvokeAsync("Control", "play", 0d); await viewer.InvokeAsync("Buffer", true);
        var state = await host.InvokeAsync<RoomSnapshot>("GetState"); Assert.True(state.Playback.IsBuffering); Assert.False(state.Playback.IsPlaying);
        await viewer.InvokeAsync("Buffer", false); Assert.True((await host.InvokeAsync<RoomSnapshot>("GetState")).Playback.IsPlaying);
        var hostInfo = await h.GetFromJsonAsync<JsonElement>("/api/me"); var hostId = hostInfo.GetProperty("id").GetString()!;
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() => viewer.InvokeAsync("VoiceSignal", hostId, "{}"));
        var signal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.On<string, string>("VoiceSignal", (_, payload) => signal.TrySetResult(payload));
        await host.InvokeAsync("Voice", true); await viewer.InvokeAsync("Voice", true); await viewer.InvokeAsync("VoiceSignal", hostId, "test-signal");
        Assert.Equal("test-signal", await signal.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        await host.InvokeAsync("Grant", id, false); await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() => viewer.InvokeAsync("Control", "pause", 0d));
        await host.InvokeAsync("Kick", id); Assert.Equal(HttpStatusCode.Unauthorized, (await v.GetAsync("/api/state")).StatusCode);
    }
    [Fact] public async Task RejectsForeignOriginAndLimitsPasswordAttempts()
    {
        var (client, _) = Client(); using (client)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/join") { Content = JsonContent.Create(new { name = "A", password = "secret1" }) }; request.Headers.Add("Origin", "https://attacker.example");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
            for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/join", new { name = "A", password = "wrong" })).StatusCode);
            Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/auth/join", new { name = "A", password = "secret1" })).StatusCode);
        }
    }
    [Fact] public async Task LibraryDeduplicatesAndPersistsHistory()
    {
        var library = app.Services.GetRequiredService<MediaLibrary>(); var duplicate = await library.AddAsync(media with { Id = "another" }); Assert.Equal(media.Id, duplicate.Id); Assert.Single(await library.ListAsync());
        await library.RecordHistoryAsync(media); Assert.Equal("Demo", Assert.Single(await library.HistoryAsync()).Title);
    }

    [Fact] public async Task HttpsProxyLoginUsesSecureCookieAndKeepsOriginProtection()
    {
        var (client, _) = Client(); using (client)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/join")
                { Content = JsonContent.Create(new { name = "Friend", password = "secret1" }) };
            request.Headers.Add("X-Forwarded-Proto", "https"); request.Headers.Add("X-Forwarded-Host", "cinema.example");
            request.Headers.Add("X-Forwarded-For", "203.0.113.42"); request.Headers.Add("Origin", "https://cinema.example");
            using var response = await client.SendAsync(request); response.EnsureSuccessStatusCode();
            var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
            Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
            using var foreign = new HttpRequestMessage(HttpMethod.Post, "/api/auth/join")
                { Content = JsonContent.Create(new { name = "Friend", password = "secret1" }) };
            foreign.Headers.Add("X-Forwarded-Proto", "https"); foreign.Headers.Add("X-Forwarded-Host", "cinema.example");
            foreign.Headers.Add("Origin", "https://attacker.example");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(foreign)).StatusCode);
        }
    }

    [Theory]
    [InlineData("X-Forwarded-Proto", "https")]
    [InlineData("X-Forwarded-For", "127.0.0.1")]
    public async Task ProxyCannotUseOrganizerBootstrapEvenWithKnownSecret(string header, string value)
    {
        var (client, _) = Client(); using (client)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/host");
            request.Headers.Authorization = new("Bearer", secret); request.Headers.Add(header, value);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(request)).StatusCode);
        }
    }
}
