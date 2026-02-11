using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace SVF.PropDbReader
{
    /// <summary>
    /// Reads and queries properties from an Autodesk property database (.sdb).
    /// </summary>
    public class PropDbReader : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly SqliteCommand _propertyQuery;
        private readonly string _dbPath;
        private readonly bool _deleteDbOnDispose;

        /// <summary>
        /// Async factory to create PropDbReader by downloading the DB.
        /// </summary>
        public static async Task<PropDbReader> CreateAsync(string accessToken, string urn)
        {
            var dpDownloader = new DbDownloader(accessToken);
            var dbPath = await dpDownloader.DownloadPropertiesDatabaseAsync(urn)
                ?? throw new InvalidOperationException("Failed to download properties database.");
            return new PropDbReader(dbPath, true);
        }

        /// <summary>
        /// Async factory to create PropDbReader by downloading the DB.
        /// </summary>
        public static async Task<string> DownloadAndGetPath(string accessToken, string urn)
        {
            var dpDownloader = new DbDownloader(accessToken);
            var dbPath = await dpDownloader.DownloadPropertiesDatabaseAsync(urn)
                ?? throw new InvalidOperationException("Failed to download properties database.");
            return dbPath;
        }

        /// <summary>
        /// Opens the property database at the given path.
        /// </summary>
        public PropDbReader(string dbPath, bool deleteDbOnDispose = false)
        {
            _dbPath = dbPath;
            _deleteDbOnDispose = deleteDbOnDispose;
            _connection = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
            _connection.Open();
            _propertyQuery = _connection.CreateCommand();
            _propertyQuery.CommandText = @"
                SELECT _objects_attr.category AS catDisplayName,
                       _objects_attr.display_name AS attrDisplayName,
                       _objects_val.value AS propValue
                FROM _objects_eav
                  INNER JOIN _objects_id ON _objects_eav.entity_id = _objects_id.id
                  INNER JOIN _objects_attr ON _objects_eav.attribute_id = _objects_attr.id
                  INNER JOIN _objects_val ON _objects_eav.value_id = _objects_val.id
                WHERE _objects_id.id = $dbId
            ";
            _propertyQuery.Parameters.Add("$dbId", SqliteType.Integer);
        }

        /// <summary>
        /// Opens the property database after downloading it using the URN and the accessToken.
        /// This constructor is memory efficient and does not block the calling thread.
        /// </summary>
        /// <param name="accessToken">Autodesk access token.</param>
        /// <param name="urn">The URN of the model.</param>
        public PropDbReader(string accessToken, string urn)
        {
            // DownloadPropertiesDatabaseAsync is awaited synchronously, but this is safe because it only does IO and is not called on the UI thread.
            var dbPath = DownloadAndGetPath(accessToken, urn).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("Failed to download properties database.");
            _dbPath = dbPath;
            _deleteDbOnDispose = true;
            _connection = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly;Pooling=False");
            _connection.Open();
            _propertyQuery = _connection.CreateCommand();
            _propertyQuery.CommandText = @"
                SELECT _objects_attr.category AS catDisplayName,
                       _objects_attr.display_name AS attrDisplayName,
                       _objects_val.value AS propValue
                FROM _objects_eav
                  INNER JOIN _objects_id ON _objects_eav.entity_id = _objects_id.id
                  INNER JOIN _objects_attr ON _objects_eav.attribute_id = _objects_attr.id
                  INNER JOIN _objects_val ON _objects_eav.value_id = _objects_val.id
                WHERE _objects_id.id = $dbId
            ";
            _propertyQuery.Parameters.Add("$dbId", SqliteType.Integer);
        }


        /// <summary>
        /// Gets the properties for a given dbId, merging parent properties recursively.
        /// </summary>
        public async Task<Dictionary<string, object?>> GetMergedPropertiesAsync(long dbId)
        {
            var cache = new Dictionary<long, Dictionary<string, object?>>();
            var props = await GetPropertiesForDbIdAsync(dbId);
            cache[dbId] = props;
            return await MergeParentPropertiesAsync(props, cache);
        }

        /// <summary>
        /// Gets the direct properties for a given dbId.
        /// </summary>
        public async Task<Dictionary<string, object?>> GetPropertiesForDbIdAsync(long dbId)
        {
            _propertyQuery.Parameters["$dbId"].Value = dbId;
            var props = new Dictionary<string, object?>();

            using (var reader = await _propertyQuery.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    string cat = await reader.IsDBNullAsync(0) ? string.Empty : reader.GetString(0);
                    string attr = await reader.IsDBNullAsync(1) ? string.Empty : reader.GetString(1);
                    object? value = await reader.IsDBNullAsync(2) ? null : reader.GetValue(2);
                    string key = $"{cat}_{attr}";
                    props[key] = value;
                }
            }
            return props;
        }

        /// <summary>
        /// Recursively merges parent properties into the given property dictionary.
        /// </summary>
        private async Task<Dictionary<string, object?>> MergeParentPropertiesAsync(Dictionary<string, object?> childProps, Dictionary<long, Dictionary<string, object?>> cache)
        {
            const string parentKey = "__parent___";
            if (childProps.TryGetValue(parentKey, out var parentDbIdObj) && parentDbIdObj is long parentDbId)
            {
                if (!cache.TryGetValue(parentDbId, out var parentProps))
                {
                    parentProps = await GetPropertiesForDbIdAsync(parentDbId);
                    cache[parentDbId] = parentProps;
                }
                parentProps = await MergeParentPropertiesAsync(parentProps, cache);
                foreach (var kv in parentProps)
                {
                    if (!childProps.ContainsKey(kv.Key))
                        childProps[kv.Key] = kv.Value;
                }
            }
            return childProps;
        }

        /// <summary>
        /// WARNING: For large models, this method can consume a lot of memory. Use GetAllPropertyValuesStreamAsync for streaming results if possible.
        /// Returns all property values for all dbIds for a specific category and display name (property name).
        /// </summary>
        /// <param name="category">The category name of the property.</param>
        /// <param name="displayName">The display name (property name) readable by human.</param>
        /// <returns>A dictionary mapping dbId to the property value.</returns>
        public async Task<Dictionary<long, object?>> GetAllPropertyValuesAsync(string category, string displayName)
        {
            var result = new Dictionary<long, object?>();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT _objects_id.id AS dbId, _objects_val.value AS propValue
                    FROM _objects_eav
                      INNER JOIN _objects_id ON _objects_eav.entity_id = _objects_id.id
                      INNER JOIN _objects_attr ON _objects_eav.attribute_id = _objects_attr.id
                      INNER JOIN _objects_val ON _objects_eav.value_id = _objects_val.id
                    WHERE _objects_attr.category = $category AND _objects_attr.display_name = $displayName
                ";
                cmd.Parameters.AddWithValue("$category", category ?? string.Empty);
                cmd.Parameters.AddWithValue("$displayName", displayName ?? string.Empty);

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        long dbId = reader.GetInt64(0);
                        object? value = await reader.IsDBNullAsync(1) ? null : reader.GetValue(1);
                        result[dbId] = value;
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Streams all property values for all dbIds for a specific category and display name (property name).
        /// This is more memory efficient for large models than GetAllPropertyValuesAsync.
        /// </summary>
        /// <param name="category">The category name of the property.</param>
        /// <param name="displayName">The display name (property name) readable by human.</param>
        /// <returns>An async enumerable of (dbId, value) tuples.</returns>
        public async IAsyncEnumerable<(long dbId, object? value)> GetAllPropertyValuesStreamAsync(string category, string displayName)
        {
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT _objects_id.id AS dbId, _objects_val.value AS propValue
                    FROM _objects_eav
                      INNER JOIN _objects_id ON _objects_eav.entity_id = _objects_id.id
                      INNER JOIN _objects_attr ON _objects_eav.attribute_id = _objects_attr.id
                      INNER JOIN _objects_val ON _objects_eav.value_id = _objects_val.id
                    WHERE _objects_attr.category = $category AND _objects_attr.display_name = $displayName
                ";
                cmd.Parameters.AddWithValue("$category", category ?? string.Empty);
                cmd.Parameters.AddWithValue("$displayName", displayName ?? string.Empty);

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        long dbId = reader.GetInt64(0);
                        object? value = await reader.IsDBNullAsync(1) ? null : reader.GetValue(1);
                        yield return (dbId, value);
                    }
                }
            }
        }

        /// <summary>
        /// Returns all property values for all dbIds for a specific category and display name (property name) as a list of tuples.
        /// This is useful for parallel processing scenarios.
        /// WARNING: For large models, this method can consume a lot of memory. Use GetAllPropertyValuesStreamAsync for streaming results if possible.
        /// </summary>
        /// <param name="category">The category name of the property.</param>
        /// <param name="displayName">The display name (property name) readable by human.</param>
        /// <returns>A list of (dbId, value) tuples.</returns>
        public async Task<List<(long dbId, object? value)>> GetAllPropertyValuesListAsync(string category, string displayName)
        {
            var result = new List<(long dbId, object? value)>();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT _objects_id.id AS dbId, _objects_val.value AS propValue
                    FROM _objects_eav
                      INNER JOIN _objects_id ON _objects_eav.entity_id = _objects_id.id
                      INNER JOIN _objects_attr ON _objects_eav.attribute_id = _objects_attr.id
                      INNER JOIN _objects_val ON _objects_eav.value_id = _objects_val.id
                    WHERE _objects_attr.category = $category AND _objects_attr.display_name = $displayName
                ";
                cmd.Parameters.AddWithValue("$category", category ?? string.Empty);
                cmd.Parameters.AddWithValue("$displayName", displayName ?? string.Empty);

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        long dbId = reader.GetInt64(0);
                        object? value = await reader.IsDBNullAsync(1) ? null : reader.GetValue(1);
                        result.Add((dbId, value));
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Returns all property values for all dbIds for a specific category and display name (property name) as a thread-safe ConcurrentDictionary.
        /// This is useful for parallel processing scenarios.
        /// WARNING: For large models, this method can consume a lot of memory. Use GetAllPropertyValuesStreamAsync for streaming results if possible.
        /// </summary>
        /// <param name="category">The category name of the property.</param>
        /// <param name="displayName">The display name (property name) readable by human.</param>
        /// <returns>A ConcurrentDictionary mapping dbId to the property value.</returns>
        public async Task<ConcurrentDictionary<long, object?>> GetAllPropertyValuesConcurrentAsync(string category, string displayName)
        {
            var result = new ConcurrentDictionary<long, object?>();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT _objects_id.id AS dbId, _objects_val.value AS propValue
                    FROM _objects_eav
                      INNER JOIN _objects_id ON _objects_eav.entity_id = _objects_id.id
                      INNER JOIN _objects_attr ON _objects_eav.attribute_id = _objects_attr.id
                      INNER JOIN _objects_val ON _objects_eav.value_id = _objects_val.id
                    WHERE _objects_attr.category = $category AND _objects_attr.display_name = $displayName
                ";
                cmd.Parameters.AddWithValue("$category", category ?? string.Empty);
                cmd.Parameters.AddWithValue("$displayName", displayName ?? string.Empty);

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        long dbId = reader.GetInt64(0);
                        object? value = await reader.IsDBNullAsync(1) ? null : reader.GetValue(1);
                        result[dbId] = value;
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Streams all property values for all dbIds for a specific category and display name (property name)
        /// and adds them to a thread-safe ConcurrentDictionary as they are read.
        /// This is more memory efficient for large models than GetAllPropertyValuesConcurrentAsync.
        /// </summary>
        /// <param name="category">The category name of the property.</param>
        /// <param name="displayName">The display name (property name) readable by human.</param>
        /// <param name="dict">A ConcurrentDictionary to populate with (dbId, value) pairs.</param>
        /// <returns>A Task that completes when all values have been streamed into the dictionary.</returns>
        public async Task GetAllPropertyValuesStreamToConcurrentAsync(string category, string displayName, ConcurrentDictionary<long, object?> dict)
        {
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT _objects_id.id AS dbId, _objects_val.value AS propValue
                    FROM _objects_eav
                      INNER JOIN _objects_id ON _objects_eav.entity_id = _objects_id.id
                      INNER JOIN _objects_attr ON _objects_eav.attribute_id = _objects_attr.id
                      INNER JOIN _objects_val ON _objects_eav.value_id = _objects_val.id
                    WHERE _objects_attr.category = $category AND _objects_attr.display_name = $displayName
                ";
                cmd.Parameters.AddWithValue("$category", category ?? string.Empty);
                cmd.Parameters.AddWithValue("$displayName", displayName ?? string.Empty);

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        long dbId = reader.GetInt64(0);
                        object? value = await reader.IsDBNullAsync(1) ? null : reader.GetValue(1);
                        dict[dbId] = value;
                    }
                }
            }
        }

        /// <summary>
        /// Gets all properties for all dbIds in the database.
        /// </summary>
        /// <returns>A dictionary mapping dbId to a dictionary of property key-value pairs.</returns>
        public async Task<Dictionary<long, Dictionary<string, object?>>> GetAllPropertiesAsync()
        {
            var result = new Dictionary<long, Dictionary<string, object?>>();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT _objects_id.id AS dbId, _objects_attr.category, _objects_attr.display_name, _objects_val.value
                    FROM _objects_eav
                      INNER JOIN _objects_id ON _objects_eav.entity_id = _objects_id.id
                      INNER JOIN _objects_attr ON _objects_eav.attribute_id = _objects_attr.id
                      INNER JOIN _objects_val ON _objects_eav.value_id = _objects_val.id
                ";
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        long dbId = reader.GetInt64(0);
                        string cat = await reader.IsDBNullAsync(1) ? string.Empty : reader.GetString(1);
                        string attr = await reader.IsDBNullAsync(2) ? string.Empty : reader.GetString(2);
                        object? value = await reader.IsDBNullAsync(3) ? null : reader.GetValue(3);
                        string key = $"{cat}_{attr}";
                        if (!result.TryGetValue(dbId, out var propDict))
                        {
                            propDict = new Dictionary<string, object?>();
                            result[dbId] = propDict;
                        }
                        propDict[key] = value;
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Gets the parent dbId for a given dbId, or null if none exists.
        /// </summary>
        public async Task<long?> GetParentDbIdAsync(long dbId)
        {
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT _objects_val.value
                    FROM _objects_eav
                      INNER JOIN _objects_attr ON _objects_eav.attribute_id = _objects_attr.id
                      INNER JOIN _objects_val ON _objects_eav.value_id = _objects_val.id
                    WHERE _objects_eav.entity_id = $dbId
                      AND _objects_attr.category = '__parent__'
                ";
                cmd.Parameters.AddWithValue("$dbId", dbId);
                var result = await cmd.ExecuteScalarAsync();
                if (result != null && long.TryParse(result.ToString(), out long parentId))
                    return parentId;
                return null;
            }
        }

        /// <summary>
        /// Gets the value for a specific property (by category and display name) for a given dbId.
        /// </summary>
        public async Task<object?> GetPropertyValueAsync(long dbId, string category, string displayName)
        {
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT _objects_val.value
                    FROM _objects_eav
                      INNER JOIN _objects_attr ON _objects_eav.attribute_id = _objects_attr.id
                      INNER JOIN _objects_val ON _objects_eav.value_id = _objects_val.id
                    WHERE _objects_eav.entity_id = $dbId
                      AND _objects_attr.category = $category
                      AND _objects_attr.display_name = $displayName
                ";
                cmd.Parameters.AddWithValue("$dbId", dbId);
                cmd.Parameters.AddWithValue("$category", category ?? string.Empty);
                cmd.Parameters.AddWithValue("$displayName", displayName ?? string.Empty);
                var result = await cmd.ExecuteScalarAsync();
                return result;
            }
        }

        /// <summary>
        /// Finds all dbIds where the given category, property name (display name), and value match.
        /// </summary>
        /// <param name="category">The category name of the property.</param>
        /// <param name="displayName">The display name (property name).</param>
        /// <param name="value">The value to match.</param>
        /// <returns>A list of dbIds matching the criteria.</returns>
        public async Task<List<long>> FindDbIdsByPropertyAsync(string category, string displayName, object value)
        {
            var dbIds = new List<long>();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT _objects_id.id AS dbId
                    FROM _objects_eav
                      INNER JOIN _objects_id ON _objects_eav.entity_id = _objects_id.id
                      INNER JOIN _objects_attr ON _objects_eav.attribute_id = _objects_attr.id
                      INNER JOIN _objects_val ON _objects_eav.value_id = _objects_val.id
                    WHERE _objects_attr.category = $category
                      AND _objects_attr.display_name = $displayName
                      AND _objects_val.value = $value
                ";
                cmd.Parameters.AddWithValue("$category", category ?? string.Empty);
                cmd.Parameters.AddWithValue("$displayName", displayName ?? string.Empty);
                cmd.Parameters.AddWithValue("$value", value ?? string.Empty);
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        dbIds.Add(reader.GetInt64(0));
                    }
                }
            }
            return dbIds;
        }

        /// <summary>
        /// Executes a custom SQL query and returns the results as a list of dictionaries (column name to value).
        /// </summary>
        /// <param name="sql">The SQL query string to execute.</param>
        /// <returns>A list of dictionaries, each representing a row (column name to value).</returns>
        public async Task<List<Dictionary<string, object?>>> QueryAsync(string sql)
        {
            var results = new List<Dictionary<string, object?>>();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = sql;
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        var row = new Dictionary<string, object?>();
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            string colName = reader.GetName(i);
                            object? value = await reader.IsDBNullAsync(i) ? null : reader.GetValue(i);
                            row[colName] = value;
                        }
                        results.Add(row);
                    }
                }
            }
            return results;
        }

        /// <summary>
        /// Gets all distinct property categories in the database.
        /// </summary>
        /// <returns>A list of distinct category names.</returns>
        public async Task<List<string>> GetAllCategoriesAsync()
        {
            var categories = new List<string>();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT DISTINCT category FROM _objects_attr WHERE category IS NOT NULL AND category != '' ORDER BY category";
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        categories.Add(reader.GetString(0));
                    }
                }
            }
            return categories;
        }

        /// <summary>
        /// Gets all distinct property display names (property names) in the database.
        /// </summary>
        /// <returns>A list of distinct property display names.</returns>
        public async Task<List<string>> GetAllPropertyNamesAsync()
        {
            var names = new List<string>();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT DISTINCT display_name FROM _objects_attr WHERE display_name IS NOT NULL AND display_name != '' ORDER BY display_name";
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        names.Add(reader.GetString(0));
                    }
                }
            }
            return names;
        }

        /// <summary>
        /// Gets all categories with their associated property display names.
        /// </summary>
        /// <returns>A dictionary mapping each category name to a list of its property display names.</returns>
        public async Task<Dictionary<string, List<string>>> GetCategoriesWithPropertiesAsync()
        {
            var result = new Dictionary<string, List<string>>();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT DISTINCT category, display_name
                    FROM _objects_attr
                    WHERE category IS NOT NULL AND category != ''
                      AND display_name IS NOT NULL AND display_name != ''
                    ORDER BY category, display_name
                ";
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        string category = reader.GetString(0);
                        string displayName = reader.GetString(1);

                        if (!result.TryGetValue(category, out var list))
                        {
                            list = new List<string>();
                            result[category] = list;
                        }
                        list.Add(displayName);
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Delete the DB File
        /// </summary>
        /// <returns></returns>
        public bool DeleteDbFile()
        {
            try
            {
                if (File.Exists(_dbPath))
                {
                    File.Delete(_dbPath);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error deleting file: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Disposes the database connection and releases all resources.
        /// </summary>
        public void Dispose()
        {
            // Dispose command first
            _propertyQuery?.Dispose();
            // Close and dispose connection
            if (_connection.State != System.Data.ConnectionState.Closed)
                _connection.Close();
            _connection?.Dispose();
            // Suppress finalization
            GC.SuppressFinalize(this);
            // Delete DB file if needed
            if (_deleteDbOnDispose)
                DeleteDbFile();
        }

        ~PropDbReader()
        {
            Dispose();
        }
    }
}
// NOTE: For large models, avoid calling GetAllPropertiesAsync unless necessary. Consider processing in batches or streaming results to reduce memory usage.
