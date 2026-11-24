using CinemaTogether.Core;

namespace CinemaTogether.Infrastructure;

public static class MediaToolPaths
{
    public static bool RepairSettings(AppSettings settings, string? applicationDirectory = null)
    {
        var directory = Path.GetFullPath(applicationDirectory ?? AppContext.BaseDirectory);
        var ffmpeg = Normalize(settings.FfmpegPath, "ffmpeg", directory);
        var ffprobe = Normalize(settings.FfprobePath, "ffprobe", directory);
        var changed = settings.FfmpegPath != ffmpeg || settings.FfprobePath != ffprobe;
        settings.FfmpegPath = ffmpeg;
        settings.FfprobePath = ffprobe;
        return changed;
    }

    public static string ResolveExecutable(string configured, string? applicationDirectory = null)
    {
        var directory = Path.GetFullPath(applicationDirectory ?? AppContext.BaseDirectory);
        var value = Unquote(configured);
        var name = value.Replace('\\', '/').Split('/')[^1];
        if (name.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase) || name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
            value = Normalize(value, "ffmpeg", directory);
        else if (name.Equals("ffprobe", StringComparison.OrdinalIgnoreCase) || name.Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase))
            value = Normalize(value, "ffprobe", directory);
        return HasPath(value) ? Absolute(value, directory) : value;
    }

    private static string Normalize(string? configured, string tool, string directory)
    {
        var value = Unquote(configured ?? tool);
        if (value.Length == 0) value = tool;
        var portable = $"tools/{tool}.exe";
        var bundled = Absolute(portable, directory);
        if (HasPath(value))
        {
            var resolved = Absolute(value, directory);
            if (File.Exists(resolved))
            {
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                return resolved.Equals(bundled, comparison) ? portable : value;
            }
            // Migrate stale absolute paths left by an earlier portable installation.
            if (File.Exists(bundled)) return portable;
        }
        else if ((value.Equals(tool, StringComparison.OrdinalIgnoreCase) || value.Equals(tool + ".exe", StringComparison.OrdinalIgnoreCase))
            && File.Exists(bundled)) return portable;
        return value;
    }

    private static bool HasPath(string value) => value.Contains('/') || value.Contains('\\') || Path.IsPathRooted(value);
    private static string Absolute(string value, string directory) => Path.GetFullPath(value.Replace('\\', Path.DirectorySeparatorChar), directory);
    private static string Unquote(string value)
    {
        value = value.Trim();
        return value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1].Trim() : value;
    }
}
