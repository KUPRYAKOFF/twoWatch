using System.Text.Json;
using CinemaTogether.Core;
using Microsoft.Data.Sqlite;

namespace CinemaTogether.Infrastructure;

public sealed class MediaLibrary(string directory)
{
    private readonly string connectionString = new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(Path.GetFullPath(directory), "library.db") }.ToString();

    private async Task<SqliteConnection> OpenAsync()
    {
        Directory.CreateDirectory(directory);
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    public async Task InitializeAsync()
    {
        await using var connection = await OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS media(id TEXT PRIMARY KEY, path TEXT NOT NULL UNIQUE, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS history(id INTEGER PRIMARY KEY AUTOINCREMENT, videoId TEXT NOT NULL, title TEXT NOT NULL, watchedAt INTEGER NOT NULL);
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<MediaItem> AddAsync(MediaItem item)
    {
        await using var connection = await OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id FROM media WHERE path=$path";
        cmd.Parameters.AddWithValue("$path", item.Path);
        if (await cmd.ExecuteScalarAsync() is string id) item = item with { Id = id };
        cmd.CommandText = "INSERT INTO media(id,path,json) VALUES($id,$path,$json) ON CONFLICT(id) DO UPDATE SET json=excluded.json";
        cmd.Parameters.AddWithValue("$id", item.Id);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(item));
        await cmd.ExecuteNonQueryAsync();
        return item;
    }

    public async Task<IReadOnlyList<MediaItem>> ListAsync()
    {
        await using var connection = await OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT json FROM media ORDER BY path COLLATE NOCASE";
        await using var reader = await cmd.ExecuteReaderAsync();
        List<MediaItem> items = [];
        while (await reader.ReadAsync()) items.Add(JsonSerializer.Deserialize<MediaItem>(reader.GetString(0))!);
        return items;
    }

    public async Task<MediaItem?> FindAsync(string id)
    {
        await using var connection = await OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT json FROM media WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        return await cmd.ExecuteScalarAsync() is string json ? JsonSerializer.Deserialize<MediaItem>(json) : null;
    }

    public async Task RemoveAsync(string id)
    {
        await using var connection = await OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM media WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task RecordHistoryAsync(MediaItem item)
    {
        await using var connection = await OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO history(videoId,title,watchedAt) VALUES($id,$title,$time); DELETE FROM history WHERE id NOT IN (SELECT id FROM history ORDER BY id DESC LIMIT 100);";
        cmd.Parameters.AddWithValue("$id", item.Id);
        cmd.Parameters.AddWithValue("$title", item.Title);
        cmd.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<HistoryEntry>> HistoryAsync()
    {
        await using var connection = await OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT videoId,title,watchedAt FROM history ORDER BY id DESC LIMIT 100";
        await using var reader = await cmd.ExecuteReaderAsync();
        List<HistoryEntry> result = [];
        while (await reader.ReadAsync()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        return result;
    }

    public static bool IsSafeFile(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) return false;
        for (string? part = path; part is not null; part = Path.GetDirectoryName(part))
            if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }
}
