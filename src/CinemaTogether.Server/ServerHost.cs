using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using CinemaTogether.Core;
using CinemaTogether.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;

namespace CinemaTogether.Server;

public sealed record JoinRequest(string Name, string Password);
public sealed record PrepareRequest(string Quality, int? Audio);

public static class ServerHost
{
    public static object PublicMedia(MediaItem item) => new { item.Id, item.Title, item.Duration, item.Size,
        item.VideoCodec, item.AudioCodec, item.Width, item.Height, item.DirectPlay, item.Tracks };

    public static WebApplication Build(AppSettings settings, string directory, string hostSecret, string[]? args = null)
    {
        if (!IPAddress.TryParse(settings.ListenAddress, out var address) || settings.Port is < 1 or > 65535)
            throw new ArgumentException("Укажите корректный IP-адрес и порт от 1 до 65535.");
        if (string.IsNullOrEmpty(settings.PasswordHash)) throw new ArgumentException("Сначала задайте пароль комнаты.");
        if (settings.MaxParticipants is < 1 or > 50 || hostSecret.Length < 32) throw new ArgumentException("Некорректные параметры комнаты.");
        var root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (!Directory.Exists(root)) root = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args ?? [], WebRootPath = root, ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(directory, "logs")));
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 16 * 1024;
            options.Listen(address, settings.Port, listen =>
            {
                if (!string.IsNullOrWhiteSpace(settings.CertificatePath))
                    listen.UseHttps(settings.CertificatePath, settings.CertificatePassword);
            });
            if (!IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any))
                options.Listen(IPAddress.Loopback, settings.Port, listen =>
                {
                    if (!string.IsNullOrWhiteSpace(settings.CertificatePath)) listen.UseHttps(settings.CertificatePath, settings.CertificatePassword);
                });
        });
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<Room>(); builder.Services.AddSingleton<SessionStore>();
        builder.Services.AddSingleton(new MediaLibrary(directory));
        builder.Services.AddSingleton(provider => new MediaProcessor(settings, directory,
            error => provider.GetRequiredService<ILogger<MediaProcessor>>().LogError(error, "Video preparation failed")));
        builder.Services.AddSingleton<RoomCoordinator>();
        builder.Services.AddHostedService<Heartbeat>();
        builder.Services.AddAuthentication("Session").AddScheme<AuthenticationSchemeOptions, SessionAuthentication>("Session", _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("Host", p => p.RequireRole("Host")));
        builder.Services.AddSignalR(options => { options.MaximumReceiveMessageSize = 8 * 1024; options.MaximumParallelInvocationsPerClient = 1; });
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy("prepare", context => RateLimitPartition.GetFixedWindowLimiter(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 12, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; media-src 'self' blob:; connect-src 'self' ws: wss:; object-src 'none'; frame-ancestors 'self'; base-uri 'self'";
            if (context.Request.Headers.Origin is { Count: > 0 } origin &&
                (!Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var uri) || uri.Authority != context.Request.Host.Value || uri.Scheme != context.Request.Scheme))
            { context.Response.StatusCode = 403; return; }
            try { await next(); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FileNotFoundException or IOException or System.ComponentModel.Win32Exception)
            {
                app.Logger.LogWarning(ex, "Request failed: {Path}", context.Request.Path);
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = ex is FileNotFoundException ? 404 : 400;
                    await context.Response.WriteAsJsonAsync(new { error = ex is ArgumentException or InvalidOperationException ? ex.Message : "Файл или внешняя программа недоступны. Проверьте журнал сервера." });
                }
            }
        });
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/api/stream")) { await next(); return; }
            var original = context.Features.Get<IHttpResponseBodyFeature>()!;
            var coordinator = app.Services.GetRequiredService<RoomCoordinator>();
            var stream = new CountingStream(original.Stream, length => Interlocked.Add(ref coordinator.BytesSent, length));
            var feature = new StreamResponseBodyFeature(stream, original);
            context.Features.Set<IHttpResponseBodyFeature>(feature);
            try { await next(); await feature.CompleteAsync(); }
            finally { context.Features.Set(original); }
        });
        app.UseDefaultFiles(); app.UseStaticFiles(); app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter();
        app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
        app.MapPost("/api/auth/join", (JoinRequest request, HttpContext context, SessionStore sessions) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 40 || request.Password is null || !SettingsStore.VerifyPassword(request.Password, settings.PasswordHash))
                return Results.Json(new { error = "Проверьте имя и пароль комнаты." }, statusCode: 401);
            if (sessions.FindToken(context.Request.Cookies[SessionAuthentication.CookieName]) is { } old) sessions.Revoke(old.Id);
            var session = sessions.Create(request.Name.Trim(), false);
            SetCookie(context, session); return Results.Ok(new { session.Name, session.IsHost });
        }).RequireRateLimiting("login");
        app.MapPost("/api/auth/host", (HttpContext context, SessionStore sessions) =>
        {
            var supplied = context.Request.Headers.Authorization.ToString();
            if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote) ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes("Bearer " + hostSecret)))
                return Results.Unauthorized();
            var session = sessions.Create("Организатор", true); SetCookie(context, session); return Results.Ok();
        }).RequireRateLimiting("login");
        app.MapPost("/api/auth/logout", async (HttpContext context, RoomCoordinator coordinator) =>
        {
            var id = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            // Hosts may log out of their local player without ending the room.
            var sessions = context.RequestServices.GetRequiredService<SessionStore>();
            sessions.Revoke(id); await coordinator.KickAsync(id);
            context.Response.Cookies.Delete(SessionAuthentication.CookieName); return Results.Ok();
        }).RequireAuthorization();
        app.MapGet("/api/me", (HttpContext context, SessionStore sessions) =>
        {
            var session = sessions.Find(context.User.FindFirstValue(ClaimTypes.NameIdentifier))!;
            return new { session.Id, session.Name, session.IsHost, session.CanControl, settings.RoomName };
        }).RequireAuthorization();
        app.MapGet("/api/library", async (MediaLibrary library) => (await library.ListAsync()).Select(PublicMedia)).RequireAuthorization();
        app.MapGet("/api/history", (MediaLibrary library) => library.HistoryAsync()).RequireAuthorization();
        app.MapGet("/api/state", (Room room) => room.Snapshot()).RequireAuthorization();
        app.MapGet("/api/stream/{id}", async (string id, MediaLibrary library) =>
        {
            var item = await library.FindAsync(id);
            if (item is null || !MediaLibrary.IsSafeFile(item.Path)) return Results.NotFound();
            if (!item.DirectPlay) return Results.BadRequest(new { error = "Это видео нужно подготовить для браузера." });
            return Results.File(item.Path, Path.GetExtension(item.Path).Equals(".webm", StringComparison.OrdinalIgnoreCase) ? "video/webm" : "video/mp4", enableRangeProcessing: true);
        }).RequireAuthorization();
        app.MapPost("/api/prepare/{id}", async (string id, PrepareRequest request, MediaLibrary library, MediaProcessor processor) =>
        {
            var item = await library.FindAsync(id);
            return item is null ? Results.NotFound() : Results.Ok(processor.Prepare(item, request.Quality, request.Audio));
        }).RequireAuthorization().RequireRateLimiting("prepare");
        app.MapGet("/api/conversions/{id}", (string id, MediaProcessor processor) => processor.Status(id) is { } status ? Results.Ok(status) : Results.NotFound()).RequireAuthorization();
        app.MapGet("/api/stream/converted/{id}", (string id, MediaProcessor processor) =>
            processor.Status(id)?.State == "ready" && File.Exists(processor.CachePath(id))
                ? Results.File(processor.CachePath(id), "video/mp4", enableRangeProcessing: true) : Results.NotFound()).RequireAuthorization();
        app.MapGet("/api/subtitles/{id}/{track:int}", async (string id, int track, MediaLibrary library, MediaProcessor processor, CancellationToken cancellation) =>
        {
            var item = await library.FindAsync(id);
            return item is null ? Results.NotFound() : Results.Text(await processor.SubtitleAsync(item, track, cancellation), "text/vtt; charset=utf-8");
        }).RequireAuthorization().RequireRateLimiting("prepare");
        app.MapGet("/api/diagnostics", async (RoomCoordinator coordinator) =>
        {
            var ffmpeg = false; var ffprobe = false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await MediaProcessor.RunAsync(settings.FfmpegPath, ["-version"], timeout.Token); ffmpeg = true; } catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or OperationCanceledException) { }
            try { await MediaProcessor.RunAsync(settings.FfprobePath, ["-version"], timeout.Token); ffprobe = true; } catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or OperationCanceledException) { }
            return new { settings.ListenAddress, settings.Port, Https = !string.IsNullOrEmpty(settings.CertificatePath), BytesSent = Interlocked.Read(ref coordinator.BytesSent), Participants = coordinator.Room.Snapshot().Participants.Count, Ffmpeg = ffmpeg, Ffprobe = ffprobe };
        }).RequireAuthorization("Host");
        app.MapHub<WatchHub>("/watch");
        return app;
    }

    private static void SetCookie(HttpContext context, Session session) => context.Response.Cookies.Append(SessionAuthentication.CookieName, session.Token,
        new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = context.Request.IsHttps, IsEssential = true, Path = "/", Expires = session.Expires });
}
