using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CinemaTogether.Core;

namespace CinemaTogether.Infrastructure;

public sealed record ConversionStatus(string Id, string State, string? Error);

public sealed class MediaProcessor(AppSettings settings, string directory) : IAsyncDisposable
{
    public static readonly string[] Extensions = [".mp4", ".webm", ".mkv", ".avi", ".mov", ".m4v"];
    private readonly ConcurrentDictionary<string, ConversionStatus> jobs = new();
    private readonly ConcurrentDictionary<string, Lazy<Task>> tasks = new();
    private readonly SemaphoreSlim encoderLock = new(1);
    private readonly CancellationTokenSource shutdown = new();
    private readonly string cache = Path.Combine(directory, "cache");

    public static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellation = default)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить " + executable);
        using var registration = cancellation.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
        var output = process.StandardOutput.ReadToEndAsync(cancellation);
        var error = process.StandardError.ReadToEndAsync(cancellation);
        await process.WaitForExitAsync(cancellation);
        var errorText = await error;
        if (process.ExitCode != 0) throw new IOException($"{Path.GetFileName(executable)}: {errorText[..Math.Min(2000, errorText.Length)]}");
        return await output;
    }

    public async Task<MediaItem> ProbeAsync(string path, CancellationToken cancellation = default)
    {
        path = Path.GetFullPath(path);
        if (!Extensions.Contains(Path.GetExtension(path).ToLowerInvariant()) || !MediaLibrary.IsSafeFile(path))
            throw new ArgumentException("Нужен существующий видеофайл; символические ссылки не поддерживаются.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var json = await RunAsync(settings.FfprobePath, ["-v", "error", "-show_streams", "-show_format", "-of", "json", path], timeout.Token);
        using var doc = JsonDocument.Parse(json);
        var streams = doc.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "video");
        if (video.ValueKind == JsonValueKind.Undefined) throw new ArgumentException("В файле нет видеодорожки.");
        var audio = streams.FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "audio");
        static string Get(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) ? value.ToString() : "";
        var vc = Get(video, "codec_name"); var ac = Get(audio, "codec_name");
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var direct = (extension is ".mp4" or ".m4v" or ".mov" && vc == "h264" && ac is "aac" or "mp3" or "")
            || (extension == ".webm" && vc is "vp8" or "vp9" or "av1" && ac is "opus" or "vorbis" or "");
        var tracks = streams.Where(s => Get(s, "codec_type") is "audio" or "subtitle").Select(s =>
        {
            s.TryGetProperty("tags", out var tags);
            return new MediaTrack(s.GetProperty("index").GetInt32(), Get(s, "codec_type"), Get(s, "codec_name"), Get(tags, "language"), Get(tags, "title"));
        }).ToArray();
        var format = doc.RootElement.GetProperty("format");
        double.TryParse(Get(format, "duration"), CultureInfo.InvariantCulture, out var duration);
        if (!double.IsFinite(duration) || duration <= 0) throw new ArgumentException("FFprobe не смог определить длительность видео.");
        int.TryParse(Get(video, "width"), out var width); int.TryParse(Get(video, "height"), out var height);
        return new(Guid.NewGuid().ToString("N"), path, Path.GetFileNameWithoutExtension(path), duration,
            new FileInfo(path).Length, vc, ac, width, height, direct, tracks);
    }

    public ConversionStatus Prepare(MediaItem item, string quality, int? audio)
    {
        if (quality is not ("original" or "1080" or "720" or "480")) throw new ArgumentException("Неизвестное качество.");
        if (audio.HasValue && !item.Tracks.Any(t => t.Type == "audio" && t.Index == audio)) throw new ArgumentException("Нет такой звуковой дорожки.");
        if (!MediaLibrary.IsSafeFile(item.Path)) throw new FileNotFoundException("Видео перемещено или удалено.");
        Directory.CreateDirectory(cache);
        var info = new FileInfo(item.Path);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{item.Id}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{quality}|{audio}|{settings.HardwareEncoder}")))[..32].ToLowerInvariant();
        if (!jobs.ContainsKey(key) && jobs.Values.Count(j => j.State is "queued" or "converting") >= 4)
            throw new InvalidOperationException("Очередь преобразования заполнена. Дождитесь завершения текущих задач.");
        var status = jobs.GetOrAdd(key, _ => new(key, File.Exists(CachePath(key)) ? "ready" : "queued", null));
        if (status.State == "queued") _ = tasks.GetOrAdd(key, _ => new Lazy<Task>(() => ConvertAsync(key, item, quality, audio))).Value;
        return jobs[key];
    }

    public string CachePath(string id) => Path.Combine(cache, id + ".mp4");
    public ConversionStatus? Status(string id) => jobs.GetValueOrDefault(id);

    private async Task ConvertAsync(string key, MediaItem item, string quality, int? audio)
    {
        var acquired = false;
        var temporary = Path.Combine(cache, key + ".partial.mp4");
        try
        {
            await encoderLock.WaitAsync(shutdown.Token); acquired = true;
            jobs[key] = new(key, "converting", null);
            // Bound persisted cache before a new conversion. Never expose partial files.
            var files = new DirectoryInfo(cache).GetFiles("*.mp4").Where(f => !f.Name.Contains(".partial.")).OrderBy(f => f.LastWriteTimeUtc).ToList();
            var total = files.Sum(f => f.Length);
            foreach (var file in files)
            {
                if (total < 20L * 1024 * 1024 * 1024) break;
                try { file.Delete(); total -= file.Length; var id = Path.GetFileNameWithoutExtension(file.Name); jobs.TryRemove(id, out _); tasks.TryRemove(id, out _); } catch (IOException) { }
            }
            async Task Encode(string encoder)
            {
                List<string> args = ["-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", item.Path, "-map", "0:v:0", "-map", audio.HasValue ? $"0:{audio}" : "0:a:0?"];
                if (quality == "original" && item.VideoCodec == "h264") args.AddRange(["-c:v", "copy"]);
                else
                {
                    args.AddRange(["-c:v", encoder, "-pix_fmt", "yuv420p"]);
                    if (encoder == "libx264") args.AddRange(["-preset", "veryfast", "-crf", "21", "-threads", "2"]);
                    if (quality != "original") args.AddRange(["-vf", $"scale=-2:'min({quality},ih)'"]);
                }
                args.AddRange(["-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", temporary]);
                await RunAsync(settings.FfmpegPath, args, shutdown.Token);
            }
            var encoder = settings.HardwareEncoder;
            if (encoder is not ("libx264" or "h264_nvenc" or "h264_qsv" or "h264_amf")) encoder = "libx264";
            try { await Encode(encoder); }
            catch (IOException) when (encoder != "libx264") { await Encode("libx264"); }
            File.Move(temporary, CachePath(key), true);
            jobs[key] = new(key, "ready", null);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or System.ComponentModel.Win32Exception)
        { jobs[key] = new(key, "failed", ex is OperationCanceledException ? "Преобразование отменено." : "FFmpeg не смог подготовить видео. Проверьте путь к программе и свободное место."); }
        finally
        {
            if (acquired) encoderLock.Release();
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { }
        }
    }

    public async Task<string> SubtitleAsync(MediaItem item, int track, CancellationToken cancellation)
    {
        if (!item.Tracks.Any(t => t.Type == "subtitle" && t.Index == track)) throw new ArgumentException("Нет такой дорожки субтитров.");
        if (!MediaLibrary.IsSafeFile(item.Path)) throw new FileNotFoundException();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        return await RunAsync(settings.FfmpegPath, ["-nostdin", "-v", "error", "-i", item.Path, "-map", $"0:{track}", "-f", "webvtt", "-"], timeout.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await shutdown.CancelAsync();
        await Task.WhenAll(tasks.Values.Where(t => t.IsValueCreated).Select(t => t.Value));
        shutdown.Dispose(); encoderLock.Dispose();
    }
}
