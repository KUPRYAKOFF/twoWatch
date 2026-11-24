namespace CinemaTogether.Core;

public sealed record MediaTrack(int Index, string Type, string Codec, string? Language, string? Title);
public sealed record MediaItem(string Id, string Path, string Title, double Duration, long Size,
    string VideoCodec, string AudioCodec, int Width, int Height, bool DirectPlay,
    IReadOnlyList<MediaTrack> Tracks);
public sealed record Participant(string Id, string Name, bool IsHost, bool CanControl, bool Buffering, bool VoiceConnected = false);
public sealed record PlaybackState(string? VideoId, double Position, bool IsPlaying, double Rate,
    long UpdatedAt, long Revision, bool IsBuffering);
public sealed record ChatMessage(string Id, string Name, string Text, long SentAt);
public sealed record RoomSnapshot(PlaybackState Playback, IReadOnlyList<Participant> Participants,
    IReadOnlyList<ChatMessage> Messages, IReadOnlyList<string> Queue);
public sealed record HistoryEntry(string VideoId, string Title, long WatchedAt);

public sealed class AppSettings
{
    public string ListenAddress { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 8080;
    public string RoomName { get; set; } = "Наш кинозал";
    public string PasswordHash { get; set; } = "";
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
    public string HardwareEncoder { get; set; } = "libx264";
    public string? CertificatePath { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? CertificatePassword { get; set; }
    public int MaxParticipants { get; set; } = 12;
}
