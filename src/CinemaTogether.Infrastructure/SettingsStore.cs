using System.Security.Cryptography;
using System.Text.Json;
using CinemaTogether.Core;

namespace CinemaTogether.Infrastructure;

public sealed class SettingsStore(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    private string FilePath => Path.Combine(DirectoryPath, "settings.json");
    public async Task<AppSettings> LoadAsync()
    {
        Directory.CreateDirectory(DirectoryPath);
        if (!File.Exists(FilePath)) return new();
        await using var stream = File.OpenRead(FilePath);
        return await JsonSerializer.DeserializeAsync<AppSettings>(stream) ?? new();
    }

    public async Task SaveAsync(AppSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporary = FilePath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, FilePath, true);
    }

    public static string HashPassword(string password)
    {
        if (password.Length is < 6 or > 128) throw new ArgumentException("Пароль должен содержать от 6 до 128 символов.");
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210_000, HashAlgorithmName.SHA256, 32);
        return Convert.ToBase64String(salt) + "." + Convert.ToBase64String(hash);
    }

    public static bool VerifyPassword(string password, string encoded)
    {
        if (password.Length > 128 || string.IsNullOrEmpty(encoded)) return false;
        try
        {
            var parts = encoded.Split('.');
            if (parts.Length != 2) return false;
            var salt = Convert.FromBase64String(parts[0]);
            var expected = Convert.FromBase64String(parts[1]);
            if (salt.Length != 16 || expected.Length != 32) return false;
            return CryptographicOperations.FixedTimeEquals(expected,
                Rfc2898DeriveBytes.Pbkdf2(password, salt, 210_000, HashAlgorithmName.SHA256, 32));
        }
        catch (FormatException) { return false; }
    }
}
