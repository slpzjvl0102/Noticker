using System.IO;
using Noticker.Data;
using Noticker.Models;

namespace Noticker.Tests;

public class StickerRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly StickerRepository _repo;

    public StickerRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"noticker_test_{Guid.NewGuid()}.db");
        _repo = new StickerRepository(_dbPath);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    private static Sticker MakeSticker(string title = "Test", string body = "") => new()
    {
        Title = title,
        Body = body,
        MonitorDeviceName = "\\\\.\\DISPLAY1",
        PositionX = 10,
        PositionY = 20,
        Width = 250,
        Height = 300,
    };

    // ── Insert / GetAll ────────────────────────────────────────────────────────

    [Fact]
    public void Insert_ThenGetAll_ReturnsSameSticker()
    {
        var s = MakeSticker("Hello");
        _repo.Insert(s);

        var all = _repo.GetAll();

        Assert.Single(all);
        Assert.Equal(s.Id, all[0].Id);
        Assert.Equal("Hello", all[0].Title);
    }

    [Fact]
    public void Insert_MultipleStickers_ReturnsAllOrderedByCreatedAt()
    {
        _repo.Insert(MakeSticker("A"));
        _repo.Insert(MakeSticker("B"));
        _repo.Insert(MakeSticker("C"));

        var all = _repo.GetAll();

        Assert.Equal(3, all.Count);
    }

    // ── Update ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Update_PersistsBodyContent()
    {
        var s = MakeSticker();
        _repo.Insert(s);

        s.Body = "updated body";
        _repo.Update(s);

        var loaded = _repo.GetAll()[0];
        Assert.Equal("updated body", loaded.Body);
    }

    [Fact]
    public void Update_PersistsIsHidden()
    {
        var s = MakeSticker();
        _repo.Insert(s);

        s.IsHidden = true;
        _repo.Update(s);

        var loaded = _repo.GetAll()[0];
        Assert.True(loaded.IsHidden);
    }

    [Fact]
    public void Update_IsHidden_False_ClearsFlag()
    {
        var s = MakeSticker();
        s.IsHidden = true;
        _repo.Insert(s);

        s.IsHidden = false;
        _repo.Update(s);

        var loaded = _repo.GetAll()[0];
        Assert.False(loaded.IsHidden);
    }

    [Fact]
    public void Update_PersistsTitle()
    {
        var s = MakeSticker("original");
        _repo.Insert(s);

        s.Title = "changed";
        _repo.Update(s);

        var loaded = _repo.GetAll()[0];
        Assert.Equal("changed", loaded.Title);
    }

    // ── Delete ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Delete_RemovesSticker()
    {
        var s = MakeSticker();
        _repo.Insert(s);

        _repo.Delete(s.Id);

        Assert.Empty(_repo.GetAll());
    }

    [Fact]
    public void Delete_NonExistentId_DoesNotThrow()
    {
        var ex = Record.Exception(() => _repo.Delete("no-such-id"));
        Assert.Null(ex);
    }

    // ── GetPendingForRetry ─────────────────────────────────────────────────────

    [Fact]
    public void GetPendingForRetry_ReturnsPendingWithContent()
    {
        var s = MakeSticker("title", "body");
        s.SyncState = "pending";
        _repo.Insert(s);

        var pending = _repo.GetPendingForRetry();

        Assert.Single(pending);
        Assert.Equal(s.Id, pending[0].Id);
    }

    [Fact]
    public void GetPendingForRetry_ExcludesEmptyTitleAndBody()
    {
        var s = MakeSticker("", "");
        s.SyncState = "pending";
        _repo.Insert(s);

        var pending = _repo.GetPendingForRetry();

        Assert.Empty(pending);
    }

    [Fact]
    public void GetPendingForRetry_ExcludesSyncedStickers()
    {
        var s = MakeSticker("title", "body");
        s.SyncState = "synced";
        _repo.Insert(s);

        var pending = _repo.GetPendingForRetry();

        Assert.Empty(pending);
    }

    [Fact]
    public void GetPendingForRetry_ExcludesRetryCountOver3()
    {
        var s = MakeSticker("title", "body");
        s.SyncState = "failed";
        s.RetryCount = 3;
        _repo.Insert(s);

        var pending = _repo.GetPendingForRetry();

        Assert.Empty(pending);
    }

    // ── UpdateSyncState ────────────────────────────────────────────────────────

    [Fact]
    public void UpdateSyncState_Synced_SetsLastSyncedAt()
    {
        var s = MakeSticker();
        _repo.Insert(s);

        _repo.UpdateSyncState(s.Id, "synced", "page-abc", 0);

        var loaded = _repo.GetAll()[0];
        Assert.Equal("synced", loaded.SyncState);
        Assert.NotNull(loaded.LastSyncedAt);
    }

    [Fact]
    public void UpdateSyncState_Failed_DoesNotSetLastSyncedAt()
    {
        var s = MakeSticker();
        _repo.Insert(s);

        _repo.UpdateSyncState(s.Id, "failed", null, 1);

        var loaded = _repo.GetAll()[0];
        Assert.Equal("failed", loaded.SyncState);
        Assert.Null(loaded.LastSyncedAt);
    }

    // ── GetAllSummary ──────────────────────────────────────────────────────────

    [Fact]
    public void GetAllSummary_ReturnsIsHiddenCorrectly()
    {
        var s = MakeSticker("note");
        s.IsHidden = true;
        _repo.Insert(s);

        var summary = _repo.GetAllSummary();

        Assert.Single(summary);
        Assert.True(summary[0].IsHidden);
    }

    [Fact]
    public void GetAllSummary_OrderedByUpdatedAtDesc()
    {
        var a = MakeSticker("A");
        a.UpdatedAt = "2024-01-01T00:00:00Z";
        var b = MakeSticker("B");
        b.UpdatedAt = "2024-06-01T00:00:00Z";
        _repo.Insert(a);
        _repo.Insert(b);

        var summary = _repo.GetAllSummary();

        Assert.Equal("B", summary[0].Title);
        Assert.Equal("A", summary[1].Title);
    }

    // ── V4: NotionLastEdit / NotionLastEditBy / PullDisabled ──────────────────

    [Fact]
    public void Insert_PersistsV4Fields()
    {
        var s = MakeSticker();
        s.NotionLastEdit = "2026-06-10T12:00:00.000Z";
        s.NotionLastEditBy = "user-abc";
        s.PullDisabled = true;
        _repo.Insert(s);

        var loaded = _repo.GetAll()[0];
        Assert.Equal("2026-06-10T12:00:00.000Z", loaded.NotionLastEdit);
        Assert.Equal("user-abc", loaded.NotionLastEditBy);
        Assert.True(loaded.PullDisabled);
    }

    [Fact]
    public void Insert_V4FieldsDefaultToNullAndFalse()
    {
        _repo.Insert(MakeSticker());

        var loaded = _repo.GetAll()[0];
        Assert.Null(loaded.NotionLastEdit);
        Assert.Null(loaded.NotionLastEditBy);
        Assert.False(loaded.PullDisabled);
    }

    [Fact]
    public void Update_PersistsV4Fields()
    {
        var s = MakeSticker();
        _repo.Insert(s);

        s.NotionLastEdit = "2026-06-10T13:00:00.000Z";
        s.NotionLastEditBy = "user-def";
        s.PullDisabled = true;
        _repo.Update(s);

        var loaded = _repo.GetAll()[0];
        Assert.Equal("2026-06-10T13:00:00.000Z", loaded.NotionLastEdit);
        Assert.Equal("user-def", loaded.NotionLastEditBy);
        Assert.True(loaded.PullDisabled);
    }

    // ── UpdateNotionLastEdit ───────────────────────────────────────────────────

    [Fact]
    public void UpdateNotionLastEdit_WritesBothColumns()
    {
        var s = MakeSticker();
        _repo.Insert(s);

        _repo.UpdateNotionLastEdit(s.Id, "2026-06-10T14:00:00.000Z", "user-ghi");

        var loaded = _repo.GetAll()[0];
        Assert.Equal("2026-06-10T14:00:00.000Z", loaded.NotionLastEdit);
        Assert.Equal("user-ghi", loaded.NotionLastEditBy);
    }

    [Fact]
    public void UpdateNotionLastEdit_DoesNotTouchOtherColumns()
    {
        var s = MakeSticker("keep-title", "keep-body");
        _repo.Insert(s);
        var before = _repo.GetAll()[0];

        _repo.UpdateNotionLastEdit(s.Id, "2026-06-10T14:00:00.000Z", "user-ghi");

        var loaded = _repo.GetAll()[0];
        Assert.Equal("keep-title", loaded.Title);
        Assert.Equal("keep-body", loaded.Body);
        Assert.Equal(before.SyncState, loaded.SyncState);
        Assert.Equal(before.UpdatedAt, loaded.UpdatedAt);
        Assert.Equal(before.PullDisabled, loaded.PullDisabled);
    }

    // ── SetPullDisabled ────────────────────────────────────────────────────────

    [Fact]
    public void SetPullDisabled_RoundTrips()
    {
        var s = MakeSticker();
        _repo.Insert(s);

        _repo.SetPullDisabled(s.Id, true);
        Assert.True(_repo.GetAll()[0].PullDisabled);

        _repo.SetPullDisabled(s.Id, false);
        Assert.False(_repo.GetAll()[0].PullDisabled);
    }

    // ── Conflict state ─────────────────────────────────────────────────────────

    [Fact]
    public void GetPendingForRetry_ExcludesConflictStickers()
    {
        var s = MakeSticker("title", "body");
        s.SyncState = "conflict";
        _repo.Insert(s);

        var pending = _repo.GetPendingForRetry();

        Assert.Empty(pending);
    }

    // ── updated_at hygiene ─────────────────────────────────────────────────────

    [Fact]
    public void UpdateSyncState_DoesNotChangeUpdatedAt()
    {
        var s = MakeSticker();
        _repo.Insert(s);
        var before = _repo.GetAll()[0].UpdatedAt;

        _repo.UpdateSyncState(s.Id, "synced", "page-abc", 0);

        var after = _repo.GetAll()[0].UpdatedAt;
        Assert.Equal(before, after);
    }

    // ── Migration ──────────────────────────────────────────────────────────────

    [Fact]
    public void FreshDatabase_ReportsUserVersion5()
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        Assert.Equal(5, Convert.ToInt32(cmd.ExecuteScalar()));
    }

    [Fact]
    public void BodyRuns_InsertUpdate_RoundTrip()
    {
        var s = new Sticker
        {
            MonitorDeviceName = "m",
            BodyRuns = """[{"Kind":"paragraph","Runs":[{"T":"굵게","B":true,"U":false}]}]""",
        };
        _repo.Insert(s);
        var loaded = _repo.GetAll().Single(x => x.Id == s.Id);
        Assert.Equal(s.BodyRuns, loaded.BodyRuns);

        loaded.BodyRuns = null;
        _repo.Update(loaded);
        Assert.Null(_repo.GetAll().Single(x => x.Id == s.Id).BodyRuns);
    }
}
