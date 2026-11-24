using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace CinemaTogether.Server;

public sealed class Session(string id, string name, bool host)
{
    public string Id { get; } = id;
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    public string Name { get; } = name;
    public bool IsHost { get; } = host;
    public bool CanControl { get; set; } = host;
    public DateTimeOffset Expires { get; } = DateTimeOffset.UtcNow.AddHours(24);
}

public sealed class SessionStore
{
    private readonly ConcurrentDictionary<string, Session> sessions = new();
    private readonly ConcurrentDictionary<string, string> tokens = new();
    public Session Create(string name, bool host)
    {
        if (sessions.Count >= 1000) { Prune(); if (sessions.Count >= 1000) throw new InvalidOperationException("Слишком много сессий. Повторите позже."); }
        var session = new Session(Guid.NewGuid().ToString("N"), name, host);
        sessions[session.Id] = session;
        tokens[session.Token] = session.Id;
        return session;
    }
    public Session? Find(string? id)
    {
        if (id is null || !sessions.TryGetValue(id, out var value)) return null;
        if (value.Expires > DateTimeOffset.UtcNow) return value;
        Revoke(id);
        return null;
    }
    public Session? FindToken(string? token) => token is not null && tokens.TryGetValue(token, out var id) ? Find(id) : null;
    public void Revoke(string id) { if (sessions.TryRemove(id, out var session)) tokens.TryRemove(session.Token, out _); }
    public void Prune() { foreach (var id in sessions.Keys) Find(id); }
}

public sealed class SessionAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, SessionStore sessions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string CookieName = "cinema.session";
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var session = sessions.FindToken(Request.Cookies[CookieName]);
        if (session is null) return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, session.Id), new Claim(ClaimTypes.Name, session.Name), new Claim(ClaimTypes.Role, session.IsHost ? "Host" : "Viewer") };
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
    }
}
