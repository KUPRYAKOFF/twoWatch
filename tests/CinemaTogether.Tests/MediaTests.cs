using CinemaTogether.Core;
using CinemaTogether.Infrastructure;
using Xunit;

namespace CinemaTogether.Tests;

public sealed class MediaTests
{
    [Fact] public async Task ProbesRealVideoTranscodesAndExtractsSubtitles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cinema-media-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var subtitle = Path.Combine(directory, "captions.srt"); await File.WriteAllTextAsync(subtitle, "1\n00:00:00,000 --> 00:00:01,000\nHello together\n");
            var source = Path.Combine(directory, "movie.mkv");
            await MediaProcessor.RunAsync("ffmpeg", ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "color=c=blue:s=320x180:r=24:d=2", "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-i", subtitle, "-c:v", "mpeg4", "-c:a", "aac", "-c:s", "srt", "-shortest", source]);
            await using var processor = new MediaProcessor(new AppSettings(), directory); var item = await processor.ProbeAsync(source);
            Assert.False(item.DirectPlay); Assert.Equal(320, item.Width); Assert.Contains(item.Tracks, t => t.Type == "subtitle");
            var vtt = await processor.SubtitleAsync(item, item.Tracks.Single(t => t.Type == "subtitle").Index, CancellationToken.None); Assert.Contains("WEBVTT", vtt); Assert.Contains("Hello together", vtt);
            var status = processor.Prepare(item, "480", null); var timeout = DateTime.UtcNow.AddSeconds(30);
            while (status.State is "queued" or "converting" && DateTime.UtcNow < timeout) { await Task.Delay(100); status = processor.Status(status.Id)!; }
            Assert.Equal("ready", status.State); var converted = await processor.ProbeAsync(processor.CachePath(status.Id)); Assert.True(converted.DirectPlay); Assert.Equal("h264", converted.VideoCodec);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact] public async Task SettingsPersistWithoutCertificatePassword()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cinema-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(directory); await store.SaveAsync(new AppSettings { Port = 8888, CertificatePassword = "do-not-save" });
            Assert.Equal(8888, (await store.LoadAsync()).Port); Assert.DoesNotContain("do-not-save", await File.ReadAllTextAsync(Path.Combine(directory, "settings.json")));
        }
        finally { Directory.Delete(directory, true); }
    }
}
