using CinemaTogether.Core;
using CinemaTogether.Infrastructure;
using Xunit;

namespace CinemaTogether.Tests;

public sealed class MediaToolPathsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "cinema-paths-" + Guid.NewGuid().ToString("N"));

    private static void Bundle(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "tools"));
        File.WriteAllText(Path.Combine(root, "tools", "ffmpeg.exe"), "test fixture");
        File.WriteAllText(Path.Combine(root, "tools", "ffprobe.exe"), "test fixture");
    }

    [Fact] public void RepairsStaleWindowsPathsUsingCurrentBundle()
    {
        Bundle(directory);
        var settings = new AppSettings { FfmpegPath = @"C:\Old installation\tools\ffmpeg.exe", FfprobePath = @"C:\Old installation\tools\ffprobe.exe" };
        Assert.True(MediaToolPaths.RepairSettings(settings, directory));
        Assert.Equal("tools/ffmpeg.exe", settings.FfmpegPath); Assert.Equal("tools/ffprobe.exe", settings.FfprobePath);
        Assert.Equal(Path.Combine(directory, "tools", "ffprobe.exe"), MediaToolPaths.ResolveExecutable(settings.FfprobePath, directory));
    }

    [Fact] public async Task PortableSettingsSurviveMovingToFolderWithSpacesAndCyrillic()
    {
        var old = Path.Combine(directory, "old"); var moved = Path.Combine(directory, "Новая папка"); Bundle(old);
        var settings = new AppSettings(); MediaToolPaths.RepairSettings(settings, old);
        var store = new SettingsStore(Path.Combine(directory, "data")); await store.SaveAsync(settings);
        Directory.Move(old, moved); var restored = await store.LoadAsync();
        Assert.False(MediaToolPaths.RepairSettings(restored, moved));
        Assert.True(File.Exists(MediaToolPaths.ResolveExecutable(restored.FfmpegPath, moved)));
        Assert.Equal(Path.Combine(moved, "tools", "ffprobe.exe"), MediaToolPaths.ResolveExecutable(@"tools\ffprobe.exe", moved));
    }

    [Fact] public void PreservesExistingCustomToolsAndAcceptsQuotedPaths()
    {
        var custom = Path.Combine(directory, "custom tools"); Bundle(directory); Directory.CreateDirectory(custom);
        var encoder = Path.Combine(custom, "my-encoder.exe"); var probe = Path.Combine(custom, "ffprobe.exe");
        File.WriteAllText(encoder, "custom"); File.WriteAllText(probe, "custom");
        var settings = new AppSettings { FfmpegPath = '"' + encoder + '"', FfprobePath = probe };
        MediaToolPaths.RepairSettings(settings, directory);
        Assert.Equal(encoder, settings.FfmpegPath); Assert.Equal(probe, settings.FfprobePath);
        Assert.Equal(encoder, MediaToolPaths.ResolveExecutable('"' + encoder + '"', directory));
    }

    [Fact] public void SystemCommandsStillUsePathWhenBundleIsAbsent()
    {
        var settings = new AppSettings(); Assert.False(MediaToolPaths.RepairSettings(settings, directory));
        Assert.Equal("ffmpeg", MediaToolPaths.ResolveExecutable("ffmpeg", directory));
    }

    [Fact] public async Task MissingToolShowsRecoveryInstructions()
    {
        var error = await Assert.ThrowsAsync<FileNotFoundException>(() => MediaProcessor.RunAsync(Path.Combine(directory, "not-installed-probe.exe"), ["-version"]));
        Assert.Contains("Распакуйте архив", error.Message); Assert.Contains("Настройках", error.Message);
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
