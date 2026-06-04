using System.IO;
using Noticker.Data;
using Noticker.Models;

namespace Noticker.Tests;

public class SettingsRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SettingsRepository _repo;

    public SettingsRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"noticker_settings_test_{Guid.NewGuid()}.db");
        _repo = new SettingsRepository(_dbPath);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    // ── Get / Set ──────────────────────────────────────────────────────────────

    [Fact]
    public void Set_ThenGet_ReturnsValue()
    {
        _repo.Set("my_key", "my_value");
        Assert.Equal("my_value", _repo.Get("my_key"));
    }

    [Fact]
    public void Get_MissingKey_ReturnsNull()
    {
        Assert.Null(_repo.Get("does_not_exist"));
    }

    [Fact]
    public void Set_Upserts_ExistingKey()
    {
        _repo.Set("key", "first");
        _repo.Set("key", "second");
        Assert.Equal("second", _repo.Get("key"));
    }

    // ── Delete ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Delete_RemovesKey()
    {
        _repo.Set("key", "value");
        _repo.Delete("key");
        Assert.Null(_repo.Get("key"));
    }

    [Fact]
    public void Delete_NonExistentKey_DoesNotThrow()
    {
        var ex = Record.Exception(() => _repo.Delete("ghost"));
        Assert.Null(ex);
    }

    // ── LoadInto ───────────────────────────────────────────────────────────────
    // AppSettings is a singleton, so we use Instance and verify state after LoadInto.

    [Fact]
    public void LoadInto_ColorSwapped_True()
    {
        _repo.Set("color_swapped", "true");
        _repo.LoadInto(AppSettings.Instance);
        Assert.True(AppSettings.Instance.ColorSwapped);
    }

    [Fact]
    public void LoadInto_TargetDbId_Loaded()
    {
        _repo.Set("target_db_id", "db-xyz");
        _repo.LoadInto(AppSettings.Instance);
        Assert.Equal("db-xyz", AppSettings.Instance.TargetDbId);
    }

    [Fact]
    public void LoadInto_CategoryPropertyName_DefaultsToCategory()
    {
        // fresh DB — no category_property_name stored
        _repo.LoadInto(AppSettings.Instance);
        Assert.Equal("Category", AppSettings.Instance.CategoryPropertyName);
    }

    [Fact]
    public void LoadInto_CategoryOptions_ParsedFromJson()
    {
        _repo.Set("category_options_cache", "[\"Work\",\"Personal\"]");
        _repo.LoadInto(AppSettings.Instance);
        Assert.Contains("Work", AppSettings.Instance.CategoryOptions);
        Assert.Contains("Personal", AppSettings.Instance.CategoryOptions);
    }

    [Fact]
    public void LoadInto_MalformedCategoryOptions_FallsBackToEmpty()
    {
        AppSettings.Instance.CategoryOptions = [];
        _repo.Set("category_options_cache", "not-json");
        _repo.LoadInto(AppSettings.Instance);
        Assert.Empty(AppSettings.Instance.CategoryOptions);
    }

    [Fact]
    public void LoadInto_CategoryColors_ParsedFromJson()
    {
        _repo.Set("category_colors_cache", "{\"Work\":\"blue\"}");
        _repo.LoadInto(AppSettings.Instance);
        Assert.True(AppSettings.Instance.CategoryColors.ContainsKey("Work"));
        Assert.Equal("blue", AppSettings.Instance.CategoryColors["Work"]);
    }
}
