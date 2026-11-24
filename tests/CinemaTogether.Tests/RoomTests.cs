using CinemaTogether.Core;
using CinemaTogether.Infrastructure;
using CinemaTogether.Server;
using Xunit;

namespace CinemaTogether.Tests;

public sealed class RoomTests
{
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(double seconds) => now = now.AddSeconds(seconds);
    }
    private static MediaItem Movie => new("film", "/unused.mp4", "Film", 120, 1024, "h264", "aac", 1280, 720, true, []);
    private static (Room Room, Clock Clock) Ready()
    {
        var clock = new Clock(); var room = new Room(clock); room.Join(new("host", "Host", true, true, false), 12); room.Join(new("friend", "Friend", false, false, false), 12);
        room.Select(Movie); room.Buffer("host", false); room.Buffer("friend", false); return (room, clock);
    }

    [Fact] public void ServerClockAdvancesAtSelectedRate()
    {
        var (room, clock) = Ready(); room.Control("seek", 10); room.Control("rate", 1.5); room.Control("play"); clock.Advance(4);
        Assert.Equal(16, room.Snapshot().Playback.Position, 6);
        room.Control("pause"); clock.Advance(10); Assert.Equal(16, room.Snapshot().Playback.Position, 6);
    }
    [Fact] public void BufferingFreezesEveryoneAndResumesAfterLastReady()
    {
        var (room, clock) = Ready(); room.Control("play"); clock.Advance(8); room.Buffer("friend", true); clock.Advance(20);
        Assert.False(room.Snapshot().Playback.IsPlaying); Assert.True(room.Snapshot().Playback.IsBuffering); Assert.Equal(8, room.Snapshot().Playback.Position);
        room.Buffer("host", true); room.Buffer("friend", false); Assert.False(room.Snapshot().Playback.IsPlaying);
        room.Buffer("host", false); clock.Advance(2); Assert.True(room.Snapshot().Playback.IsPlaying); Assert.Equal(10, room.Snapshot().Playback.Position);
    }
    [Fact] public void ExplicitPauseDuringBufferingCancelsAutomaticResume()
    {
        var (room, _) = Ready(); room.Control("play"); room.Buffer("friend", true); room.Control("pause"); room.Buffer("friend", false); Assert.False(room.Snapshot().Playback.IsPlaying);
    }
    [Fact] public void ServerStopsAtEndWithoutAController()
    {
        var (room, clock) = Ready(); room.Control("play"); clock.Advance(130); room.Tick(); Assert.False(room.Snapshot().Playback.IsPlaying); Assert.Equal(120, room.Snapshot().Playback.Position);
    }
    [Fact] public void DisconnectingBufferingParticipantUnblocksRoom()
    {
        var (room, _) = Ready(); room.Control("play"); room.Buffer("friend", true); room.Leave("friend"); Assert.True(room.Snapshot().Playback.IsPlaying);
    }
    [Fact] public void JoiningMidMovieWaitsForNewParticipantAndPreservesPosition()
    {
        var (room, clock) = Ready(); room.Control("play"); clock.Advance(15); room.Join(new("new", "New", false, false, false), 12);
        Assert.False(room.Snapshot().Playback.IsPlaying); Assert.Equal(15, room.Snapshot().Playback.Position); room.Buffer("new", false); Assert.True(room.Snapshot().Playback.IsPlaying);
    }
    [Fact] public void RejectsInvalidCommandsAndClampsSeek()
    {
        var (room, _) = Ready(); Assert.Throws<ArgumentException>(() => room.Control("seek", double.NaN)); Assert.Throws<ArgumentException>(() => room.Control("rate", 10));
        room.Control("seek", 999); Assert.Equal(120, room.Snapshot().Playback.Position); room.Control("seek", -10); Assert.Equal(0, room.Snapshot().Playback.Position);
    }
    [Fact] public void RightsAndCapacityAreExplicit()
    {
        var (room, _) = Ready(); Assert.False(room.CanControl("friend")); room.Grant("friend", true); Assert.True(room.CanControl("friend")); room.Grant("host", false); Assert.True(room.CanControl("host"));
        Assert.Throws<InvalidOperationException>(() => room.Join(new("third", "Third", false, false, false), 2));
    }
    [Fact] public void ChatAndQueueAreBounded()
    {
        var (room, _) = Ready(); for (var i = 0; i < 110; i++) room.Chat("friend", "Message " + i);
        Assert.Equal(100, room.Snapshot().Messages.Count); Assert.Throws<ArgumentException>(() => room.Chat("friend", new string('x', 1001)));
        room.Enqueue("one"); room.Enqueue("two"); Assert.Equal("one", room.Dequeue()); Assert.Equal("two", room.Dequeue()); Assert.Null(room.Dequeue());
    }
    [Fact] public void PasswordsUseSaltAndFixedLengthHash()
    {
        var first = SettingsStore.HashPassword("secret1"); var second = SettingsStore.HashPassword("secret1"); Assert.NotEqual(first, second); Assert.True(SettingsStore.VerifyPassword("secret1", first)); Assert.False(SettingsStore.VerifyPassword("wrong", first)); Assert.False(SettingsStore.VerifyPassword("secret1", "corrupt"));
    }
    [Fact] public void PublicParticipantIdIsNeverAnAuthenticationToken()
    {
        var sessions = new SessionStore(); var host = sessions.Create("Host", true); Assert.NotEqual(host.Id, host.Token); Assert.Null(sessions.FindToken(host.Id)); Assert.Same(host, sessions.FindToken(host.Token)); sessions.Revoke(host.Id); Assert.Null(sessions.FindToken(host.Token));
    }
}
