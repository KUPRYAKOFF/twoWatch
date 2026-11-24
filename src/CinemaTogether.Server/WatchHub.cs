using System.Collections.Concurrent;
using System.Security.Claims;
using CinemaTogether.Core;
using CinemaTogether.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace CinemaTogether.Server;

public sealed class RoomCoordinator(Room room, SessionStore sessions, IHubContext<WatchHub> hub, AppSettings settings, MediaLibrary library)
{
    private readonly object gate = new();
    private readonly Dictionary<string, Dictionary<string, (HubCallerContext Context, bool Buffering)>> connections = [];
    private readonly ConcurrentDictionary<string, DateTimeOffset> lastChat = new();
    private readonly ConcurrentDictionary<string, (long Window, int Count)> signalRate = new();
    public Room Room => room;
    public long BytesSent;

    public async Task ConnectAsync(Session session, HubCallerContext context)
    {
        lock (gate)
        {
            if (!connections.TryGetValue(session.Id, out var group))
            {
                room.Join(new(session.Id, session.Name, session.IsHost, session.CanControl, false), settings.MaxParticipants);
                connections[session.Id] = group = [];
            }
            group[context.ConnectionId] = (context, room.Snapshot().Playback.VideoId is not null);
        }
        await BroadcastAsync();
    }

    public async Task DisconnectAsync(string id, string connection)
    {
        lock (gate)
        {
            if (connections.TryGetValue(id, out var group))
            {
                group.Remove(connection);
                if (group.Count == 0) { connections.Remove(id); room.Leave(id); lastChat.TryRemove(id, out _); signalRate.TryRemove(id, out _); }
                else room.Buffer(id, group.Values.Any(v => v.Buffering));
            }
        }
        await BroadcastAsync();
    }

    public Task BroadcastAsync() => hub.Clients.All.SendAsync("State", room.Snapshot());

    public async Task BufferAsync(string id, string connection, bool buffering)
    {
        lock (gate)
        {
            if (connections.TryGetValue(id, out var group) && group.TryGetValue(connection, out var value))
            {
                group[connection] = (value.Context, buffering);
                room.Buffer(id, group.Values.Any(v => v.Buffering));
            }
        }
        await BroadcastAsync();
    }

    public async Task SelectAsync(string id)
    {
        var media = await library.FindAsync(id) ?? throw new ArgumentException("Видео не найдено.");
        if (!MediaLibrary.IsSafeFile(media.Path)) throw new FileNotFoundException("Видео перемещено или удалено.");
        lock (gate)
        {
            room.Select(media);
            foreach (var group in connections.Values)
                foreach (var (key, value) in group.ToArray()) group[key] = (value.Context, true);
        }
        await library.RecordHistoryAsync(media);
        await BroadcastAsync();
    }

    public async Task GrantAsync(string id, bool allowed)
    {
        if (sessions.Find(id) is { IsHost: false } session) { session.CanControl = allowed; room.Grant(id, allowed); }
        await BroadcastAsync();
    }

    public async Task KickAsync(string id)
    {
        if (sessions.Find(id) is { IsHost: true }) throw new ArgumentException("Организатора нельзя отключить.");
        sessions.Revoke(id);
        lock (gate)
        {
            if (connections.Remove(id, out var group)) foreach (var value in group.Values) value.Context.Abort();
            room.Leave(id); lastChat.TryRemove(id, out _); signalRate.TryRemove(id, out _);
        }
        await BroadcastAsync();
    }

    public async Task ChatAsync(Session session, string text)
    {
        var now = DateTimeOffset.UtcNow;
        if (lastChat.TryGetValue(session.Id, out var previous) && now - previous < TimeSpan.FromMilliseconds(600))
            throw new HubException("Подождите перед отправкой следующего сообщения.");
        lastChat[session.Id] = now;
        var message = room.Chat(session.Id, text);
        await hub.Clients.All.SendAsync("Message", message);
    }

    public async Task VoiceAsync(string id, bool connected)
    {
        room.Voice(id, connected); await BroadcastAsync();
    }

    public Task RelayVoiceAsync(string sender, string target, string signal)
    {
        if (signal.Length > 6000) throw new HubException("Сигнал слишком большой.");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 5;
        var rate = signalRate.AddOrUpdate(sender, (now, 1), (_, value) => value.Window == now ? (now, value.Count + 1) : (now, 1));
        if (rate.Count > 100) throw new HubException("Слишком много голосовых сигналов.");
        lock (gate)
        {
            var people = room.Snapshot().Participants;
            if (!people.Any(p => p.Id == sender && p.VoiceConnected) || !people.Any(p => p.Id == target && p.VoiceConnected)) throw new HubException("Участник не в голосовом чате.");
            return connections.TryGetValue(target, out var group) ? hub.Clients.Clients(group.Keys.ToArray()).SendAsync("VoiceSignal", sender, signal) : Task.CompletedTask;
        }
    }

    public async Task PruneAsync()
    {
        string[] ids; lock (gate) ids = connections.Keys.ToArray();
        foreach (var id in ids) if (sessions.Find(id) is null) await KickAsync(id);
        sessions.Prune();
    }

    public async Task NextAsync()
    {
        var id = room.Dequeue();
        if (id is not null) await SelectAsync(id);
        else { room.Control("pause"); await BroadcastAsync(); }
    }
}

[Authorize]
public sealed class WatchHub(RoomCoordinator coordinator, SessionStore sessions, MediaLibrary library) : Hub
{
    private Session Session => sessions.Find(Context.User?.FindFirstValue(ClaimTypes.NameIdentifier)) ?? throw new HubException("Сессия завершена. Войдите снова.");
    private void RequireControl() { if (!coordinator.Room.CanControl(Session.Id)) throw new HubException("Нет права управления воспроизведением."); }
    private void RequireHost() { if (!Session.IsHost) throw new HubException("Доступно только организатору."); }
    public override async Task OnConnectedAsync()
    {
        try { await coordinator.ConnectAsync(Session, Context); }
        catch (InvalidOperationException ex) { Context.Abort(); throw new HubException(ex.Message); }
    }
    public override Task OnDisconnectedAsync(Exception? exception)
        => coordinator.DisconnectAsync(Context.User!.FindFirstValue(ClaimTypes.NameIdentifier)!, Context.ConnectionId);
    public long Ping() { _ = Session; return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
    public RoomSnapshot GetState() { _ = Session; return coordinator.Room.Snapshot(); }
    public async Task Control(string action, double value)
    {
        RequireControl(); coordinator.Room.Control(action, value); await coordinator.BroadcastAsync();
    }
    public Task Buffer(bool buffering) => coordinator.BufferAsync(Session.Id, Context.ConnectionId, buffering);
    public Task Chat(string text) => coordinator.ChatAsync(Session, text);
    public Task Voice(bool connected) => coordinator.VoiceAsync(Session.Id, connected);
    public Task VoiceSignal(string target, string signal) => coordinator.RelayVoiceAsync(Session.Id, target, signal);
    public Task Select(string id) { RequireHost(); return coordinator.SelectAsync(id); }
    public Task Grant(string id, bool allowed) { RequireHost(); return coordinator.GrantAsync(id, allowed); }
    public Task Kick(string id) { RequireHost(); return coordinator.KickAsync(id); }
    public async Task Enqueue(string id)
    {
        RequireHost();
        if (await library.FindAsync(id) is null) throw new HubException("Видео не найдено.");
        coordinator.Room.Enqueue(id); await coordinator.BroadcastAsync();
    }
    public async Task RemoveQueued(int index) { RequireHost(); coordinator.Room.RemoveQueued(index); await coordinator.BroadcastAsync(); }
    public Task Next() { RequireHost(); return coordinator.NextAsync(); }
}

public sealed class Heartbeat(RoomCoordinator coordinator, ILogger<Heartbeat> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { coordinator.Room.Tick(); await coordinator.PruneAsync(); await coordinator.BroadcastAsync(); }
                catch (Exception ex) { logger.LogError(ex, "Room heartbeat failed"); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
