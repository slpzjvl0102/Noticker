using Microsoft.Data.Sqlite;
using Noticker.Infrastructure;
using Noticker.Models;

namespace Noticker.Data;

public class SettingsRepository
{
    private readonly string _connectionString;

    public SettingsRepository(string dbPath)
    {
        _connectionString = $"Data Source={dbPath}";
        EnsureTable();
    }

    private void EnsureTable()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public string? Get(string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    public void Set(string key, string value)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings (key, value) VALUES ($key, $val)
            ON CONFLICT(key) DO UPDATE SET value = $val
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$val", value);
        cmd.ExecuteNonQuery();
    }

    public void Delete(string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM settings WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.ExecuteNonQuery();
    }

    // Load all settings into AppSettings singleton.
    public void LoadInto(AppSettings settings)
    {
        var encToken = Get("notion_token");
        if (encToken is not null)
        {
            try { settings.NotionToken = DpapiHelper.Decrypt(encToken); }
            catch { /* invalid blob — skip */ }
        }

        settings.TargetDbId = Get("target_db_id");
        settings.CategoryPropertyName = Get("category_property_name") ?? "Category";
        settings.ColorSwapped = Get("color_swapped") == "true";
        settings.AutostartEnabled = Get("autostart_enabled") == "true";

        var cache = Get("category_options_cache");
        if (cache is not null)
        {
            try
            {
                settings.CategoryOptions = System.Text.Json.JsonSerializer
                    .Deserialize<List<string>>(cache) ?? [];
            }
            catch { /* malformed cache — ignore */ }
        }

        var colorsCache = Get("category_colors_cache");
        if (colorsCache is not null)
        {
            try
            {
                settings.CategoryColors = System.Text.Json.JsonSerializer
                    .Deserialize<Dictionary<string, string>>(colorsCache) ?? [];
            }
            catch { /* malformed cache — ignore */ }
        }
    }

    public void SaveToken(string plainToken)
    {
        Set("notion_token", DpapiHelper.Encrypt(plainToken));
    }

    public void SaveCategoryOptions(List<string> options)
    {
        Set("category_options_cache",
            System.Text.Json.JsonSerializer.Serialize(options));
    }

    public void SaveCategoryColors(Dictionary<string, string> colors)
    {
        Set("category_colors_cache",
            System.Text.Json.JsonSerializer.Serialize(colors));
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }
}
