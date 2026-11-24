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
            Assert.Equal("ready", status.State); Assert.Equal(100, status.Percent); var converted = await processor.ProbeAsync(processor.CachePath(status.Id)); Assert.True(converted.DirectPlay); Assert.Equal("h264", converted.VideoCodec);
            async Task<string?[]> AudioHashes(string path)
            {
                var json = await MediaProcessor.RunAsync("ffprobe", ["-v", "error", "-select_streams", "a:0", "-show_packets", "-show_entries", "packet=data_hash", "-show_data_hash", "sha256", "-of", "json", path]);
                using var document = System.Text.Json.JsonDocument.Parse(json);
                return document.RootElement.GetProperty("packets").EnumerateArray().Select(packet => packet.GetProperty("data_hash").GetString()).ToArray();
            }
            // Compatible AAC audio must be preserved byte-for-byte, even when video is resized.
            Assert.Equal(await AudioHashes(source), await AudioHashes(converted.Path));
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

    [Fact] public async Task ReadsFfmpegProgressBeforeProcessCompletes()
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var running = MediaProcessor.RunAsync("ffmpeg", ["-nostdin", "-v", "error", "-re", "-f", "lavfi", "-i",
            "color=s=32x32:r=10:d=3", "-progress", "pipe:1", "-nostats", "-f", "null", "-"], onOutputLine: line =>
            { lines.Enqueue(line); if (line == "progress=continue") first.TrySetResult(); });
        await first.Task.WaitAsync(TimeSpan.FromSeconds(8)); Assert.False(running.IsCompleted);
        Assert.Equal("", await running); Assert.Contains("progress=end", lines); Assert.Contains(lines, line => line.StartsWith("out_time_us="));
    }
}
