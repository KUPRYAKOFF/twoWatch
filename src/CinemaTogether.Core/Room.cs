namespace CinemaTogether.Core;

/// <summary>All playback transitions are serialized here; clients never own the clock.</summary>
public sealed class Room(TimeProvider? clock = null)
{
    private readonly object gate = new();
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly Dictionary<string, Participant> participants = [];
    private readonly List<ChatMessage> messages = [];
    private readonly List<string> queue = [];
    private PlaybackState state = new(null, 0, false, 1, 0, 0, false);
    private bool playRequested;
    private double duration;
    private long Now => time.GetUtcNow().ToUnixTimeMilliseconds();

    public RoomSnapshot Snapshot()
    {
        lock (gate) return new(Current(), participants.Values.ToArray(), messages.ToArray(), queue.ToArray());
    }

    private PlaybackState Current()
    {
        var position = state.Position + (state.IsPlaying ? Math.Max(0, Now - state.UpdatedAt) / 1000d * state.Rate : 0);
        return state with { Position = Math.Clamp(position, 0, duration), UpdatedAt = Now };
    }

    private void Commit(PlaybackState next)
        => state = next with { UpdatedAt = Now, Revision = state.Revision + 1 };

    private void Reconcile()
    {
        var buffering = state.VideoId is not null && participants.Values.Any(p => p.Buffering);
        Commit(Current() with { IsBuffering = buffering, IsPlaying = playRequested && !buffering && state.VideoId is not null });
    }

    public void Join(Participant participant, int maximum)
    {
        lock (gate)
        {
            if (!participants.ContainsKey(participant.Id) && participants.Count >= maximum)
                throw new InvalidOperationException("Комната заполнена.");
            participants[participant.Id] = participant with { Buffering = state.VideoId is not null };
            Reconcile();
        }
    }

    public void Leave(string id)
    {
        lock (gate) { participants.Remove(id); Reconcile(); }
    }

    public void Tick()
    {
        lock (gate)
        {
            if (state.IsPlaying && Current().Position >= duration)
            { playRequested = false; Commit(Current() with { IsPlaying = false }); }
        }
    }

    public void Grant(string id, bool allowed)
    {
        lock (gate)
        {
            if (participants.TryGetValue(id, out var p) && !p.IsHost && p.CanControl != allowed)
            { participants[id] = p with { CanControl = allowed }; Commit(Current()); }
        }
    }

    public bool CanControl(string id)
    {
        lock (gate) return participants.TryGetValue(id, out var p) && (p.IsHost || p.CanControl);
    }

    public void Voice(string id, bool connected)
    {
        lock (gate) { if (participants.TryGetValue(id, out var p) && p.VoiceConnected != connected) { participants[id] = p with { VoiceConnected = connected }; Commit(Current()); } }
    }

    public void Select(MediaItem media)
    {
        lock (gate)
        {
            duration = Math.Max(0, media.Duration);
            playRequested = false;
            foreach (var (id, p) in participants.ToArray()) participants[id] = p with { Buffering = true };
            Commit(new(media.Id, 0, false, 1, Now, state.Revision, participants.Count > 0));
        }
    }

    public void Control(string action, double value = 0)
    {
        lock (gate)
        {
            if (state.VideoId is null) throw new InvalidOperationException("Сначала выберите видео.");
            if (!double.IsFinite(value)) throw new ArgumentException("Некорректное значение.");
            switch (action)
            {
                case "play": playRequested = true; break;
                case "pause": playRequested = false; break;
                case "seek": Commit(Current() with { Position = Math.Clamp(value, 0, duration) }); break;
                case "rate" when value is >= 0.25 and <= 2: Commit(Current() with { Rate = value }); break;
                default: throw new ArgumentException("Неизвестная команда или скорость.");
            }
            Reconcile();
        }
    }

    public void Buffer(string id, bool buffering)
    {
        lock (gate)
        {
            if (participants.TryGetValue(id, out var p) && p.Buffering != buffering)
            { participants[id] = p with { Buffering = buffering }; Reconcile(); }
        }
    }

    public ChatMessage Chat(string id, string text)
    {
        lock (gate)
        {
            if (!participants.TryGetValue(id, out var p)) throw new InvalidOperationException("Подключитесь к комнате.");
            text = text.Trim();
            if (text.Length is < 1 or > 1000) throw new ArgumentException("Сообщение должно содержать от 1 до 1000 символов.");
            var message = new ChatMessage(Guid.NewGuid().ToString("N"), p.Name, text, Now);
            messages.Add(message);
            if (messages.Count > 100) messages.RemoveAt(0);
            return message;
        }
    }

    public void Enqueue(string id) { lock (gate) { if (queue.Count >= 100) throw new InvalidOperationException("Очередь заполнена."); queue.Add(id); Commit(Current()); } }
    public void RemoveQueued(int index) { lock (gate) { if (index >= 0 && index < queue.Count) { queue.RemoveAt(index); Commit(Current()); } } }
    public string? Dequeue() { lock (gate) { if (queue.Count == 0) return null; var id = queue[0]; queue.RemoveAt(0); Commit(Current()); return id; } }
}
