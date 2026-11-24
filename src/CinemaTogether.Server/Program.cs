using System.Security.Cryptography;
using CinemaTogether.Infrastructure;
using CinemaTogether.Server;

var directory = Environment.GetEnvironmentVariable("CINEMA_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CinemaTogether");
var store = new SettingsStore(directory);
var settings = await store.LoadAsync();
if (MediaToolPaths.RepairSettings(settings)) await store.SaveAsync(settings);
settings.CertificatePassword = Environment.GetEnvironmentVariable("CINEMA_CERT_PASSWORD");
var password = Environment.GetEnvironmentVariable("CINEMA_PASSWORD");
if (!string.IsNullOrEmpty(password)) { settings.PasswordHash = SettingsStore.HashPassword(password); await store.SaveAsync(settings); }
if (int.TryParse(Environment.GetEnvironmentVariable("CINEMA_PORT"), out var port)) settings.Port = port;
var secret = Environment.GetEnvironmentVariable("CINEMA_HOST_SECRET") ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
await using var app = ServerHost.Build(settings, directory, secret, args);
await app.Services.GetRequiredService<MediaLibrary>().InitializeAsync();
var import = Environment.GetEnvironmentVariable("CINEMA_IMPORT");
if (!string.IsNullOrWhiteSpace(import))
{
    var files = Directory.Exists(import)
        ? Directory.EnumerateFiles(import, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Where(p => MediaProcessor.Extensions.Contains(Path.GetExtension(p).ToLowerInvariant()))
        : [import];
    foreach (var file in files)
        await app.Services.GetRequiredService<MediaLibrary>().AddAsync(await app.Services.GetRequiredService<MediaProcessor>().ProbeAsync(file));
}
await app.RunAsync();
