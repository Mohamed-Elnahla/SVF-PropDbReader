using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using SVF.PropDbReader;

// Note: On Windows, SQLite connection pooling can keep files locked.
// We call SqliteConnection.ClearAllPools() in tests that need to verify file deletion.

namespace SVF.PropDbReader.Tests;

/// <summary>
/// Tests for PropDbReader using an in-memory SQLite database that mimics the EAV schema.
/// </summary>
public class PropDbReaderTests : IDisposable
{
    private readonly string _dbPath;
    private readonly PropDbReader _reader;

    public PropDbReaderTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.sdb");
        CreateTestDatabase(_dbPath);
        _reader = new PropDbReader(_dbPath, deleteDbOnDispose: true);
    }

    public void Dispose()
    {
        _reader.Dispose();
    }

    #region Test Database Setup

    private static void CreateTestDatabase(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE _objects_id (
                id INTEGER PRIMARY KEY,
                external_id TEXT,
                viewable_id INTEGER
            );

            CREATE TABLE _objects_attr (
                id INTEGER PRIMARY KEY,
                category TEXT,
                display_name TEXT,
                data_type INTEGER
            );

            CREATE TABLE _objects_val (
                id INTEGER PRIMARY KEY,
                value TEXT
            );

            CREATE TABLE _objects_eav (
                entity_id INTEGER,
                attribute_id INTEGER,
                value_id INTEGER
            );

            -- Insert attributes
            INSERT INTO _objects_attr (id, category, display_name, data_type) VALUES (1, 'Dimensions', 'Width', 0);
            INSERT INTO _objects_attr (id, category, display_name, data_type) VALUES (2, 'Dimensions', 'Height', 0);
            INSERT INTO _objects_attr (id, category, display_name, data_type) VALUES (3, 'Identity Data', 'Name', 0);
            INSERT INTO _objects_attr (id, category, display_name, data_type) VALUES (4, '__parent__', '', 0);
            INSERT INTO _objects_attr (id, category, display_name, data_type) VALUES (5, '__category__', '', 0);
            INSERT INTO _objects_attr (id, category, display_name, data_type) VALUES (6, 'Materials and Finishes', 'Material', 0);

            -- Insert values
            INSERT INTO _objects_val (id, value) VALUES (1, '10.5');
            INSERT INTO _objects_val (id, value) VALUES (2, '3.0');
            INSERT INTO _objects_val (id, value) VALUES (3, 'Basic Wall');
            INSERT INTO _objects_val (id, value) VALUES (4, '100');
            INSERT INTO _objects_val (id, value) VALUES (5, 'Walls');
            INSERT INTO _objects_val (id, value) VALUES (6, 'Concrete');
            INSERT INTO _objects_val (id, value) VALUES (7, '20.0');
            INSERT INTO _objects_val (id, value) VALUES (8, 'Floor Type 1');
            INSERT INTO _objects_val (id, value) VALUES (9, 'Floors');
            INSERT INTO _objects_val (id, value) VALUES (10, 'Default Material');

            -- Insert objects
            INSERT INTO _objects_id (id, external_id, viewable_id) VALUES (100, 'ext-100', 1);
            INSERT INTO _objects_id (id, external_id, viewable_id) VALUES (200, 'ext-200', 1);
            INSERT INTO _objects_id (id, external_id, viewable_id) VALUES (300, 'ext-300', 1);

            -- Entity 100 (Wall) — has parent 300, Width=10.5, Height=3.0, Name=Basic Wall, Category=Walls
            INSERT INTO _objects_eav (entity_id, attribute_id, value_id) VALUES (100, 1, 1);
            INSERT INTO _objects_eav (entity_id, attribute_id, value_id) VALUES (100, 2, 2);
            INSERT INTO _objects_eav (entity_id, attribute_id, value_id) VALUES (100, 3, 3);
            INSERT INTO _objects_eav (entity_id, attribute_id, value_id) VALUES (100, 4, 4);
            INSERT INTO _objects_eav (entity_id, attribute_id, value_id) VALUES (100, 5, 5);

            -- Entity 200 (Floor) — Width=20.0, Name=Floor Type 1, Category=Floors
            INSERT INTO _objects_eav (entity_id, attribute_id, value_id) VALUES (200, 1, 7);
            INSERT INTO _objects_eav (entity_id, attribute_id, value_id) VALUES (200, 3, 8);
            INSERT INTO _objects_eav (entity_id, attribute_id, value_id) VALUES (200, 5, 9);

            -- Entity 300 (parent of 100) — Material=Default Material
            INSERT INTO _objects_eav (entity_id, attribute_id, value_id) VALUES (300, 6, 10);
        ";
        cmd.ExecuteNonQuery();
    }

    #endregion

    #region Constructor Tests

    [Fact]
    public void Constructor_WithValidPath_OpensConnection()
    {
        // The _reader is already created in the constructor — just verify it works
        Assert.NotNull(_reader);
    }

    [Fact]
    public void Constructor_WithNullPath_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new PropDbReader(null!));
    }

    #endregion

    #region GetPropertiesForDbIdAsync Tests

    [Fact]
    public async Task GetPropertiesForDbIdAsync_ReturnsPropertiesForExistingElement()
    {
        var props = await _reader.GetPropertiesForDbIdAsync(100);

        Assert.NotEmpty(props);
        Assert.Equal("10.5", props["Dimensions_Width"]?.ToString());
        Assert.Equal("3.0", props["Dimensions_Height"]?.ToString());
        Assert.Equal("Basic Wall", props["Identity Data_Name"]?.ToString());
    }

    [Fact]
    public async Task GetPropertiesForDbIdAsync_ReturnsEmptyForNonExistentElement()
    {
        var props = await _reader.GetPropertiesForDbIdAsync(99999);
        Assert.Empty(props);
    }

    [Fact]
    public async Task GetPropertiesForDbIdAsync_SupportsCancellation()
    {
        using var cts = new CancellationTokenSource();
        var props = await _reader.GetPropertiesForDbIdAsync(100, cts.Token);
        Assert.NotEmpty(props);
    }

    #endregion

    #region GetPropertyValueAsync Tests

    [Fact]
    public async Task GetPropertyValueAsync_ReturnsCorrectValue()
    {
        var value = await _reader.GetPropertyValueAsync(100, "Dimensions", "Width");
        Assert.Equal("10.5", value?.ToString());
    }

    [Fact]
    public async Task GetPropertyValueAsync_ReturnsNullForNonExistentProperty()
    {
        var value = await _reader.GetPropertyValueAsync(100, "NonExistent", "Property");
        Assert.Null(value);
    }

    [Fact]
    public async Task GetPropertyValueAsync_ThrowsOnNullCategory()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _reader.GetPropertyValueAsync(100, null!, "Width"));
    }

    [Fact]
    public async Task GetPropertyValueAsync_ThrowsOnNullDisplayName()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _reader.GetPropertyValueAsync(100, "Dimensions", null!));
    }

    #endregion

    #region GetParentDbIdAsync Tests

    [Fact]
    public async Task GetParentDbIdAsync_ReturnsParentWhenExists()
    {
        var parentId = await _reader.GetParentDbIdAsync(100);
        Assert.NotNull(parentId);
        Assert.Equal(100L, parentId.Value);
    }

    [Fact]
    public async Task GetParentDbIdAsync_ReturnsNullWhenNoParent()
    {
        var parentId = await _reader.GetParentDbIdAsync(200);
        Assert.Null(parentId);
    }

    #endregion

    #region GetAllPropertyValuesAsync Tests

    [Fact]
    public async Task GetAllPropertyValuesAsync_ReturnsAllValuesForProperty()
    {
        var widths = await _reader.GetAllPropertyValuesAsync("Dimensions", "Width");

        Assert.Equal(2, widths.Count);
        Assert.True(widths.ContainsKey(100));
        Assert.True(widths.ContainsKey(200));
        Assert.Equal("10.5", widths[100]?.ToString());
        Assert.Equal("20.0", widths[200]?.ToString());
    }

    [Fact]
    public async Task GetAllPropertyValuesAsync_ReturnsEmptyForNonExistentProperty()
    {
        var result = await _reader.GetAllPropertyValuesAsync("NonExistent", "Property");
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAllPropertyValuesAsync_ThrowsOnNullCategory()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _reader.GetAllPropertyValuesAsync(null!, "Width"));
    }

    #endregion

    #region GetAllPropertyValuesStreamAsync Tests

    [Fact]
    public async Task GetAllPropertyValuesStreamAsync_StreamsAllValues()
    {
        var items = new List<(long dbId, object? value)>();
        await foreach (var item in _reader.GetAllPropertyValuesStreamAsync("Dimensions", "Width"))
        {
            items.Add(item);
        }

        Assert.Equal(2, items.Count);
    }

    #endregion

    #region GetAllPropertyValuesListAsync Tests

    [Fact]
    public async Task GetAllPropertyValuesListAsync_ReturnsList()
    {
        var list = await _reader.GetAllPropertyValuesListAsync("Dimensions", "Width");
        Assert.Equal(2, list.Count);
    }

    #endregion

    #region GetAllPropertyValuesConcurrentAsync Tests

    [Fact]
    public async Task GetAllPropertyValuesConcurrentAsync_ReturnsConcurrentDictionary()
    {
        var result = await _reader.GetAllPropertyValuesConcurrentAsync("Dimensions", "Width");
        Assert.IsType<ConcurrentDictionary<long, object?>>(result);
        Assert.Equal(2, result.Count);
    }

    #endregion

    #region GetAllPropertyValuesStreamToConcurrentAsync Tests

    [Fact]
    public async Task GetAllPropertyValuesStreamToConcurrentAsync_PopulatesDictionary()
    {
        var dict = new ConcurrentDictionary<long, object?>();
        await _reader.GetAllPropertyValuesStreamToConcurrentAsync("Dimensions", "Width", dict);
        Assert.Equal(2, dict.Count);
    }

    [Fact]
    public async Task GetAllPropertyValuesStreamToConcurrentAsync_ThrowsOnNullDict()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _reader.GetAllPropertyValuesStreamToConcurrentAsync("Dimensions", "Width", null!));
    }

    #endregion

    #region GetAllPropertiesAsync Tests

    [Fact]
    public async Task GetAllPropertiesAsync_ReturnsAllElements()
    {
        var allProps = await _reader.GetAllPropertiesAsync();

        Assert.True(allProps.ContainsKey(100));
        Assert.True(allProps.ContainsKey(200));
        Assert.True(allProps.ContainsKey(300));

        var entity100 = allProps[100];
        Assert.Equal("10.5", entity100["Dimensions_Width"]?.ToString());
    }

    #endregion

    #region GetAllPropertiesStreamAsync Tests

    [Fact]
    public async Task GetAllPropertiesStreamAsync_StreamsAllProperties()
    {
        var items = new List<(long dbId, string key, object? value)>();
        await foreach (var item in _reader.GetAllPropertiesStreamAsync())
        {
            items.Add(item);
        }

        // Total EAV rows: entity 100 has 5, entity 200 has 3, entity 300 has 1 = 9
        Assert.Equal(9, items.Count);
    }

    #endregion

    #region FindDbIdsByPropertyAsync Tests

    [Fact]
    public async Task FindDbIdsByPropertyAsync_FindsMatchingElements()
    {
        var ids = await _reader.FindDbIdsByPropertyAsync("__category__", "", "Walls");
        Assert.Single(ids);
        Assert.Equal(100L, ids[0]);
    }

    [Fact]
    public async Task FindDbIdsByPropertyAsync_ReturnsEmptyWhenNoMatch()
    {
        var ids = await _reader.FindDbIdsByPropertyAsync("__category__", "", "Roofs");
        Assert.Empty(ids);
    }

    #endregion

    #region GetAllCategoriesAsync Tests

    [Fact]
    public async Task GetAllCategoriesAsync_ReturnsDistinctCategories()
    {
        var categories = await _reader.GetAllCategoriesAsync();

        Assert.Contains("Dimensions", categories);
        Assert.Contains("Identity Data", categories);
        Assert.Contains("__parent__", categories);
        Assert.Contains("__category__", categories);
        Assert.Contains("Materials and Finishes", categories);
    }

    #endregion

    #region GetAllPropertyNamesAsync Tests

    [Fact]
    public async Task GetAllPropertyNamesAsync_ReturnsDistinctNames()
    {
        var names = await _reader.GetAllPropertyNamesAsync();

        Assert.Contains("Width", names);
        Assert.Contains("Height", names);
        Assert.Contains("Name", names);
        Assert.Contains("Material", names);
    }

    #endregion

    #region GetCategoriesWithPropertiesAsync Tests

    [Fact]
    public async Task GetCategoriesWithPropertiesAsync_ReturnsCategoryMap()
    {
        var map = await _reader.GetCategoriesWithPropertiesAsync();

        Assert.True(map.ContainsKey("Dimensions"));
        Assert.Contains("Width", map["Dimensions"]);
        Assert.Contains("Height", map["Dimensions"]);
    }

    #endregion

    #region QueryAsync Tests

    [Fact]
    public async Task QueryAsync_ReturnsResults()
    {
        var results = await _reader.QueryAsync("SELECT COUNT(*) AS cnt FROM _objects_id");
        Assert.Single(results);
        Assert.Equal(3L, results[0]["cnt"]);
    }

    [Fact]
    public async Task QueryAsync_WithParameters_ReturnsFilteredResults()
    {
        var results = await _reader.QueryAsync(
            "SELECT * FROM _objects_attr WHERE category = $cat",
            new Dictionary<string, object?> { ["$cat"] = "Dimensions" });

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task QueryAsync_ThrowsOnNullSql()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _reader.QueryAsync(null!));
    }

    #endregion

    #region Dispose Tests

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"test_dispose_{Guid.NewGuid():N}.sdb");
        CreateTestDatabase(path);
        var reader = new PropDbReader(path, deleteDbOnDispose: true);

        // Should not throw on double dispose
        reader.Dispose();
        reader.Dispose();
    }

    [Fact]
    public void Dispose_DeletesFileWhenFlagSet()
    {
        var path = Path.Combine(Path.GetTempPath(), $"test_delete_{Guid.NewGuid():N}.sdb");
        CreateTestDatabase(path);
        SqliteConnection.ClearAllPools();
        Assert.True(File.Exists(path));

        var reader = new PropDbReader(path, deleteDbOnDispose: true);
        reader.Dispose();
        SqliteConnection.ClearAllPools();

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Dispose_KeepsFileWhenFlagNotSet()
    {
        var path = Path.Combine(Path.GetTempPath(), $"test_keep_{Guid.NewGuid():N}.sdb");
        CreateTestDatabase(path);
        // Clear pools from CreateTestDatabase's connection before opening the reader
        SqliteConnection.ClearAllPools();

        var reader = new PropDbReader(path, deleteDbOnDispose: false);
        reader.Dispose();
        SqliteConnection.ClearAllPools();

        Assert.True(File.Exists(path));

        // Cleanup
        File.Delete(path);
    }

    [Fact]
    public async Task Methods_ThrowObjectDisposedException_AfterDispose()
    {
        var path = Path.Combine(Path.GetTempPath(), $"test_disposed_{Guid.NewGuid():N}.sdb");
        CreateTestDatabase(path);
        var reader = new PropDbReader(path);
        reader.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => reader.GetPropertiesForDbIdAsync(100));
    }

    #endregion

    #region DeleteDbFile Tests

    [Fact]
    public void DeleteDbFile_ReturnsFalseForNonExistentFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nonexistent_{Guid.NewGuid():N}.sdb");
        CreateTestDatabase(path);
        // Clear pools from CreateTestDatabase's connection
        SqliteConnection.ClearAllPools();

        var reader = new PropDbReader(path);

        // Dispose reader + clear pools so we can delete the file externally
        reader.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(path);

        // Now DeleteDbFile should return false since the file is already gone
        Assert.False(reader.DeleteDbFile());
    }

    #endregion
}
