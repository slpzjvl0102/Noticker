using Microsoft.Data.Sqlite;
using Noticker.Models;

namespace Noticker.Data;

public class StickerRepository
{
    private readonly string _connectionString;

    public StickerRepository(string dbPath)
    {
        _connectionString = $"Data Source={dbPath}";
        EnsureDatabase();
    }

    private void EnsureDatabase()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS stickers (
                id                   TEXT PRIMARY KEY,
                notion_page_id       TEXT,
                title                TEXT NOT NULL DEFAULT '',
                body                 TEXT NOT NULL DEFAULT '',
                category             TEXT,
                monitor_device_name  TEXT NOT NULL,
                position_x           INTEGER NOT NULL,
                position_y           INTEGER NOT NULL,
                width                INTEGER NOT NULL DEFAULT 250,
                height               INTEGER NOT NULL DEFAULT 300,
                sync_state           TEXT NOT NULL DEFAULT 'pending',
                retry_count          INTEGER NOT NULL DEFAULT 0,
                last_synced_at       TEXT,
                created_at           TEXT NOT NULL,
                updated_at           TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
        MigrateIfNeeded(conn);
    }

    private static void MigrateIfNeeded(SqliteConnection conn)
    {
        int version;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version";
            version = Convert.ToInt32(cmd.ExecuteScalar());
        }
        if (version < 2) MigrateToV2(conn);
        if (version < 3) MigrateToV3(conn);
    }

    private static void MigrateToV2(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "ALTER TABLE stickers ADD COLUMN body_rtf TEXT";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "ALTER TABLE stickers ADD COLUMN font_family TEXT NOT NULL DEFAULT ''";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "PRAGMA user_version = 2";
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    private static void MigrateToV3(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "ALTER TABLE stickers ADD COLUMN is_hidden INTEGER NOT NULL DEFAULT 0";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "PRAGMA user_version = 3";
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    public List<(string Id, string Body, string UpdatedAt, bool IsHidden)> GetAllSummary()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, body, updated_at, is_hidden FROM stickers ORDER BY updated_at DESC";
        using var reader = cmd.ExecuteReader();
        var result = new List<(string, string, string, bool)>();
        while (reader.Read())
            result.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                        reader.GetInt32(3) != 0));
        return result;
    }

    public List<Sticker> GetAll()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM stickers ORDER BY created_at";
        using var reader = cmd.ExecuteReader();
        var result = new List<Sticker>();
        while (reader.Read()) result.Add(Map(reader));
        return result;
    }

    public void Insert(Sticker s)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO stickers
                    (id, notion_page_id, title, body, category,
                     monitor_device_name, position_x, position_y, width, height,
                     sync_state, retry_count, last_synced_at, created_at, updated_at,
                     body_rtf, font_family, is_hidden)
                VALUES
                    ($id, $npid, $title, $body, $cat,
                     $dev, $x, $y, $w, $h,
                     $ss, $rc, $lsa, $ca, $ua,
                     $body_rtf, $font_family, $is_hidden)
                """;
            Bind(cmd, s);
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            s.SyncState = "failed";
            throw new InvalidOperationException($"SQLite insert failed: {ex.Message}", ex);
        }
    }

    public void Update(Sticker s)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE stickers SET
                    notion_page_id      = $npid,
                    title               = $title,
                    body                = $body,
                    category            = $cat,
                    monitor_device_name = $dev,
                    position_x          = $x,
                    position_y          = $y,
                    width               = $w,
                    height              = $h,
                    sync_state          = $ss,
                    retry_count         = $rc,
                    last_synced_at      = $lsa,
                    updated_at          = $ua,
                    body_rtf            = $body_rtf,
                    font_family         = $font_family,
                    is_hidden           = $is_hidden
                WHERE id = $id
                """;
            Bind(cmd, s);
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            s.SyncState = "failed";
            throw new InvalidOperationException($"SQLite update failed: {ex.Message}", ex);
        }
    }

    public void Delete(string id)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM stickers WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            throw new InvalidOperationException($"SQLite delete failed: {ex.Message}", ex);
        }
    }

    public List<Sticker> GetPendingForRetry()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM stickers
            WHERE sync_state IN ('pending', 'failed') AND retry_count < 3
              AND (title != '' OR body != '')
            ORDER BY updated_at
            """;
        using var reader = cmd.ExecuteReader();
        var result = new List<Sticker>();
        while (reader.Read()) result.Add(Map(reader));
        return result;
    }

    public void UpdateSyncState(string id, string state, string? pageId, int retryCount)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE stickers SET
                    sync_state      = $ss,
                    notion_page_id  = $npid,
                    retry_count     = $rc,
                    last_synced_at  = $lsa,
                    updated_at      = $ua
                WHERE id = $id
                """;
            cmd.Parameters.AddWithValue("$ss", state);
            cmd.Parameters.AddWithValue("$npid", (object?)pageId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$rc", retryCount);
            cmd.Parameters.AddWithValue("$lsa", state == "synced"
                ? (object)DateTime.UtcNow.ToString("O")
                : DBNull.Value);
            cmd.Parameters.AddWithValue("$ua", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            throw new InvalidOperationException($"SQLite sync state update failed: {ex.Message}", ex);
        }
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static void Bind(SqliteCommand cmd, Sticker s)
    {
        cmd.Parameters.AddWithValue("$id", s.Id);
        cmd.Parameters.AddWithValue("$npid", (object?)s.NotionPageId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$title", s.Title);
        cmd.Parameters.AddWithValue("$body", s.Body);
        cmd.Parameters.AddWithValue("$cat", (object?)s.Category ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dev", s.MonitorDeviceName);
        cmd.Parameters.AddWithValue("$x", s.PositionX);
        cmd.Parameters.AddWithValue("$y", s.PositionY);
        cmd.Parameters.AddWithValue("$w", s.Width);
        cmd.Parameters.AddWithValue("$h", s.Height);
        cmd.Parameters.AddWithValue("$ss", s.SyncState);
        cmd.Parameters.AddWithValue("$rc", s.RetryCount);
        cmd.Parameters.AddWithValue("$lsa", (object?)s.LastSyncedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ca", s.CreatedAt);
        cmd.Parameters.AddWithValue("$ua", s.UpdatedAt);
        cmd.Parameters.AddWithValue("$body_rtf", (object?)s.BodyRtf ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$font_family", s.FontFamily);
        cmd.Parameters.AddWithValue("$is_hidden", s.IsHidden ? 1 : 0);
    }

    private static Sticker Map(SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        NotionPageId = r.IsDBNull(r.GetOrdinal("notion_page_id")) ? null : r.GetString(r.GetOrdinal("notion_page_id")),
        Title = r.GetString(r.GetOrdinal("title")),
        Body = r.GetString(r.GetOrdinal("body")),
        Category = r.IsDBNull(r.GetOrdinal("category")) ? null : r.GetString(r.GetOrdinal("category")),
        MonitorDeviceName = r.GetString(r.GetOrdinal("monitor_device_name")),
        PositionX = r.GetInt32(r.GetOrdinal("position_x")),
        PositionY = r.GetInt32(r.GetOrdinal("position_y")),
        Width = r.GetInt32(r.GetOrdinal("width")),
        Height = r.GetInt32(r.GetOrdinal("height")),
        SyncState = r.GetString(r.GetOrdinal("sync_state")),
        RetryCount = r.GetInt32(r.GetOrdinal("retry_count")),
        LastSyncedAt = r.IsDBNull(r.GetOrdinal("last_synced_at")) ? null : r.GetString(r.GetOrdinal("last_synced_at")),
        CreatedAt = r.GetString(r.GetOrdinal("created_at")),
        UpdatedAt = r.GetString(r.GetOrdinal("updated_at")),
        BodyRtf = r.IsDBNull(r.GetOrdinal("body_rtf")) ? null : r.GetString(r.GetOrdinal("body_rtf")),
        FontFamily = r.GetString(r.GetOrdinal("font_family")),
        IsHidden = r.GetInt32(r.GetOrdinal("is_hidden")) != 0,
    };
}
