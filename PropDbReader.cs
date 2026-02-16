using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using APSToolkit;
using Microsoft.Data.Sqlite;

namespace SVF.PropDbReader
{
    /// <summary>
    /// Reads and queries properties from an Autodesk property database (.sdb).
    /// This class is thread-safe for concurrent read operations.
    /// </summary>
    public class PropDbReader : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly string _dbPath;
        private readonly bool _deleteDbOnDispose;
        private bool _disposed;

        /// <summary>
        /// Gets the file path to the underlying .sdb database.
        /// </summary>
        public string DbPath => _dbPath;

        #region SQL Constants

        private const string PropertyQuerySql = @"
            SELECT _objects_attr.category AS catDisplayName,
                   _objects_attr.display_name AS attrDisplayName,
                   _objects_val.value AS propValue
            FROM _objects_eav
              INNER JOIN _objects_id ON _objects_eav.entity_id = _objects_id.id
              INNER JOIN _objects_attr ON _objects_eav.attribute_id = _objects_attr.id
              INNER JOIN _objects_val ON _objects_eav.value_id = _objects_val.id
            WHERE _objects_id.id = $dbId
        ";

        private const string PropertyByFilterSql = @"
            SELECT _objects_id.id AS dbId, _objects_val.value AS propValue
            FROM _objects_eav
              INNER JOIN _objects_id ON _objects_eav.entity_id = _objects_id.id
              INNER JOIN _objects_attr ON _objects_eav.attribute_id = _objects_attr.id
              INNER JOIN _objects_val ON _objects_eav.value_id = _objects_val.id
            WHERE _objects_attr.category = $category AND _objects_attr.display_name = $displayName
        ";

        private const string AllPropertiesSql = @"
            SELECT _objects_id.id AS dbId, _objects_attr.category, _objects_attr.display_name, _objects_val.value
            FROM _objects_eav
              INNER JOIN _objects_id ON _objects_eav.entity_id = _objects_id.id
              INNER JOIN _objects_attr ON _objects_eav.attribute_id = _objects_attr.id
              INNER JOIN _objects_val ON _objects_eav.value_id = _objects_val.id
        ";

        private const string ParentQuerySql = @"
            SELECT _objects_val.value
            FROM _objects_eav
              INNER JOIN _objects_attr ON _objects_eav.attribute_id = _objects_attr.id
              INNER JOIN _objects_val ON _objects_eav.value_id = _objects_val.id
            WHERE _objects_eav.entity_id = $dbId
              AND _objects_attr.category = '__parent__'
        ";

        private const string FindByPropertySql = @"
            SELECT _objects_id.id AS dbId
            FROM _objects_eav
              INNER JOIN _objects_id ON _objects_eav.entity_id = _objects_id.id
              INNER JOIN _objects_attr ON _objects_eav.attribute_id = _objects_attr.id
              INNER JOIN _objects_val ON _objects_eav.value_id = _objects_val.id
            WHERE _objects_attr.category = $category
              AND _objects_attr.display_name = $displayName
              AND _objects_val.value = $value
        ";

        private const string PropertyValueQuerySql = @"
            SELECT _objects_val.value
            FROM _objects_eav
              INNER JOIN _objects_attr ON _objects_eav.attribute_id = _objects_attr.id
              INNER JOIN _objects_val ON _objects_eav.value_id = _objects_val.id
            WHERE _objects_eav.entity_id = $dbId
              AND _objects_attr.category = $category
              AND _objects_attr.display_name = $displayName
        ";

        private const string AllCategoriesSql =
            "SELECT DISTINCT category FROM _objects_attr WHERE category IS NOT NULL AND category != '' ORDER BY category";

        private const string AllPropertyNamesSql =
            "SELECT DISTINCT display_name FROM _objects_attr WHERE display_name IS NOT NULL AND display_name != '' ORDER BY display_name";

        private const string CategoriesWithPropertiesSql = @"
            SELECT DISTINCT category, display_name
            FROM _objects_attr
            WHERE category IS NOT NULL AND category != ''
              AND display_name IS NOT NULL AND display_name != ''
            ORDER BY category, display_name
        ";

        /// <summary>
        /// The parent key used in the property dictionary (category "__parent__" + separator "_" + empty display name).
        /// </summary>
        private const string ParentKey = "__parent___";

        private const string CreateLocationTableSql = @"
            CREATE TABLE IF NOT EXISTS _fragment_locations (
                db_id   INTEGER PRIMARY KEY,
                x       REAL NOT NULL,
                y       REAL NOT NULL,
                z       REAL NOT NULL,
                min_x   REAL NOT NULL,
                min_y   REAL NOT NULL,
                min_z   REAL NOT NULL,
                max_x   REAL NOT NULL,
                max_y   REAL NOT NULL,
                max_z   REAL NOT NULL
            )
        ";

        private const string InsertLocationSql = @"
            INSERT OR REPLACE INTO _fragment_locations
                (db_id, x, y, z, min_x, min_y, min_z, max_x, max_y, max_z)
            VALUES
                ($dbId, $x, $y, $z, $minX, $minY, $minZ, $maxX, $maxY, $maxZ)
        ";

        private const string SelectAllLocationsSql =
            "SELECT db_id, x, y, z, min_x, min_y, min_z, max_x, max_y, max_z FROM _fragment_locations";

        private const string SelectLocationByIdSql =
            "SELECT x, y, z, min_x, min_y, min_z, max_x, max_y, max_z FROM _fragment_locations WHERE db_id = $dbId";

        private const string LocationTableExistsSql =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='_fragment_locations'";

        private const string CountLocationsSql =
            "SELECT COUNT(*) FROM _fragment_locations";

        #endregion

        /// <summary>
        /// Async factory to create <see cref="PropDbReader"/> by downloading the DB from the Autodesk Model Derivative API.
        /// This is the recommended way to create a <see cref="PropDbReader"/> instance.
        /// </summary>
        /// <param name="accessToken">Autodesk access token.</param>
        /// <param name="urn">The URN of the model.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A configured <see cref="PropDbReader"/> instance.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the properties database cannot be downloaded.</exception>
        public static async Task<PropDbReader> CreateAsync(string accessToken, string urn, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(accessToken);
            ArgumentNullException.ThrowIfNull(urn);

            var dbPath = await DownloadAndGetPathAsync(accessToken, urn, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Failed to download properties database.");
            return new PropDbReader(dbPath, deleteDbOnDispose: true);
        }

        /// <summary>
        /// Async factory that creates a <see cref="PropDbReader"/>, downloads fragment locations,
        /// and <b>embeds them into the SQLite database file</b> as a <c>_fragment_locations</c> table.
        /// All location queries are served directly from the database — no data is kept in memory.
        /// On subsequent opens of the same cached SDB file, locations are available immediately.
        /// </summary>
        /// <param name="accessToken">Autodesk access token.</param>
        /// <param name="urn">The URN of the model.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A configured <see cref="PropDbReader"/> instance with fragment locations embedded and loaded.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the properties database cannot be downloaded.</exception>
        public static async Task<PropDbReader> CreateWithEmbeddedLocationsAsync(string accessToken, string urn, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(accessToken);
            ArgumentNullException.ThrowIfNull(urn);

            // Download SDB and fragment locations in parallel
            var dbPathTask = DownloadAndGetPathAsync(accessToken, urn, cancellationToken);
            var locationsTask = Derivatives.ReadFragmentLocationsRemoteAsync(urn, accessToken);

            await Task.WhenAll(dbPathTask, locationsTask).ConfigureAwait(false);

            var dbPath = await dbPathTask ?? throw new InvalidOperationException("Failed to download properties database.");
            var locations = await locationsTask;

            // Write locations into the SQLite file (requires read-write connection)
            await EmbedLocationsIntoFileAsync(dbPath, locations, cancellationToken).ConfigureAwait(false);

            // Now open normally — the constructor will auto-detect the embedded table
            return new PropDbReader(dbPath, deleteDbOnDispose: true);
        }

        /// <summary>
        /// Downloads the property database and embeds fragment locations into it, returning the local file path.
        /// The resulting file can be opened later with the standard <see cref="PropDbReader(string, bool)"/>
        /// constructor and locations will be automatically loaded.
        /// </summary>
        /// <param name="accessToken">Autodesk access token.</param>
        /// <param name="urn">The URN of the model.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The local path to the downloaded database file with embedded locations.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the properties database cannot be downloaded.</exception>
        public static async Task<string> DownloadWithEmbeddedLocationsAsync(string accessToken, string urn, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(accessToken);
            ArgumentNullException.ThrowIfNull(urn);

            var dbPathTask = DownloadAndGetPathAsync(accessToken, urn, cancellationToken);
            var locationsTask = Derivatives.ReadFragmentLocationsRemoteAsync(urn, accessToken);

            await Task.WhenAll(dbPathTask, locationsTask).ConfigureAwait(false);

            var dbPath = await dbPathTask;
            var locations = await locationsTask;

            await EmbedLocationsIntoFileAsync(dbPath, locations, cancellationToken).ConfigureAwait(false);
            return dbPath;
        }

        /// <summary>
        /// Downloads the property database and returns the local file path without opening it.
        /// </summary>
        /// <param name="accessToken">Autodesk access token.</param>
        /// <param name="urn">The URN of the model.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The local path to the downloaded database file.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the properties database cannot be downloaded.</exception>
        public static async Task<string> DownloadAndGetPathAsync(string accessToken, string urn, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(accessToken);
            ArgumentNullException.ThrowIfNull(urn);

            using var downloader = new DbDownloader(accessToken);
            var dbPath = await downloader.DownloadPropertiesDatabaseAsync(urn, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Failed to download properties database.");
            return dbPath;
        }

        /// <summary>
        /// Opens the property database at the given path.
        /// If the database contains an embedded <c>_fragment_locations</c> table (created by
        /// <see cref="EmbedFragmentLocationsAsync"/> or <see cref="CreateWithEmbeddedLocationsAsync"/>),
        /// the locations are automatically loaded into memory.
        /// </summary>
        /// <param name="dbPath">Local path to the .sdb property database file.</param>
        /// <param name="deleteDbOnDispose">If true, the database file will be deleted when this instance is disposed.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="dbPath"/> is null.</exception>
        public PropDbReader(string dbPath, bool deleteDbOnDispose = false)
        {
            ArgumentNullException.ThrowIfNull(dbPath);

            _dbPath = dbPath;
            _deleteDbOnDispose = deleteDbOnDispose;
            _connection = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly;Pooling=False");
            _connection.Open();
        }

        /// <summary>
        /// Gets the properties for a given dbId, merging parent properties recursively.
        /// Child properties take precedence over parent properties.
        /// </summary>
        /// <param name="dbId">The database ID of the element.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A dictionary of merged property key-value pairs.</returns>
        public async Task<Dictionary<string, object?>> GetMergedPropertiesAsync(long dbId, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            var cache = new Dictionary<long, Dictionary<string, object?>>();
            var props = await GetPropertiesForDbIdAsync(dbId, cancellationToken).ConfigureAwait(false);
            cache[dbId] = props;
            return await MergeParentPropertiesAsync(props, cache, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets the direct properties for a given dbId (without parent merging).
        /// </summary>
        /// <param name="dbId">The database ID of the element.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A dictionary of property key-value pairs.</returns>
        public async Task<Dictionary<string, object?>> GetPropertiesForDbIdAsync(long dbId, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            var props = new Dictionary<string, object?>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = PropertyQuerySql;
            cmd.Parameters.AddWithValue("$dbId", dbId);

            using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string cat = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                string attr = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                object? value = reader.IsDBNull(2) ? null : reader.GetValue(2);
                string key = $"{cat}_{attr}";
                props[key] = value;
            }
            return props;
        }

        /// <summary>
        /// Recursively merges parent properties into the given property dictionary.
        /// </summary>
        private async Task<Dictionary<string, object?>> MergeParentPropertiesAsync(
            Dictionary<string, object?> childProps,
            Dictionary<long, Dictionary<string, object?>> cache,
            CancellationToken cancellationToken)
        {
            if (childProps.TryGetValue(ParentKey, out var parentDbIdObj) && parentDbIdObj is long parentDbId)
            {
                if (!cache.TryGetValue(parentDbId, out var parentProps))
                {
                    parentProps = await GetPropertiesForDbIdAsync(parentDbId, cancellationToken).ConfigureAwait(false);
                    cache[parentDbId] = parentProps;
                }
                parentProps = await MergeParentPropertiesAsync(parentProps, cache, cancellationToken).ConfigureAwait(false);
                foreach (var kv in parentProps)
                {
                    if (!childProps.ContainsKey(kv.Key))
                        childProps[kv.Key] = kv.Value;
                }
            }
            return childProps;
        }

        /// <summary>
        /// WARNING: For large models, this method can consume a lot of memory. Use <see cref="GetAllPropertyValuesStreamAsync"/> for streaming results if possible.
        /// Returns all property values for all dbIds for a specific category and display name (property name).
        /// </summary>
        /// <param name="category">The category name of the property.</param>
        /// <param name="displayName">The display name (property name) readable by human.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A dictionary mapping dbId to the property value.</returns>
        public async Task<Dictionary<long, object?>> GetAllPropertyValuesAsync(string category, string displayName, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(category);
            ArgumentNullException.ThrowIfNull(displayName);
            ThrowIfDisposed();

            var result = new Dictionary<long, object?>();
            using var cmd = CreateFilterCommand(category, displayName);
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                long dbId = reader.GetInt64(0);
                object? value = reader.IsDBNull(1) ? null : reader.GetValue(1);
                result[dbId] = value;
            }
            return result;
        }

        /// <summary>
        /// Streams all property values for all dbIds for a specific category and display name (property name).
        /// This is more memory efficient for large models than <see cref="GetAllPropertyValuesAsync"/>.
        /// </summary>
        /// <param name="category">The category name of the property.</param>
        /// <param name="displayName">The display name (property name) readable by human.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>An async enumerable of (dbId, value) tuples.</returns>
        public async IAsyncEnumerable<(long dbId, object? value)> GetAllPropertyValuesStreamAsync(
            string category,
            string displayName,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(category);
            ArgumentNullException.ThrowIfNull(displayName);
            ThrowIfDisposed();

            using var cmd = CreateFilterCommand(category, displayName);
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                long dbId = reader.GetInt64(0);
                object? value = reader.IsDBNull(1) ? null : reader.GetValue(1);
                yield return (dbId, value);
            }
        }

        /// <summary>
        /// Returns all property values for all dbIds for a specific category and display name as a list of tuples.
        /// This is useful for parallel processing scenarios.
        /// WARNING: For large models, this method can consume a lot of memory.
        /// </summary>
        /// <param name="category">The category name of the property.</param>
        /// <param name="displayName">The display name (property name) readable by human.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A list of (dbId, value) tuples.</returns>
        public async Task<List<(long dbId, object? value)>> GetAllPropertyValuesListAsync(string category, string displayName, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(category);
            ArgumentNullException.ThrowIfNull(displayName);
            ThrowIfDisposed();

            var result = new List<(long dbId, object? value)>();
            using var cmd = CreateFilterCommand(category, displayName);
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                long dbId = reader.GetInt64(0);
                object? value = reader.IsDBNull(1) ? null : reader.GetValue(1);
                result.Add((dbId, value));
            }
            return result;
        }

        /// <summary>
        /// Returns all property values for all dbIds for a specific category and display name as a thread-safe <see cref="ConcurrentDictionary{TKey,TValue}"/>.
        /// WARNING: For large models, this method can consume a lot of memory.
        /// </summary>
        /// <param name="category">The category name of the property.</param>
        /// <param name="displayName">The display name (property name) readable by human.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A ConcurrentDictionary mapping dbId to the property value.</returns>
        public async Task<ConcurrentDictionary<long, object?>> GetAllPropertyValuesConcurrentAsync(string category, string displayName, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(category);
            ArgumentNullException.ThrowIfNull(displayName);
            ThrowIfDisposed();

            var result = new ConcurrentDictionary<long, object?>();
            using var cmd = CreateFilterCommand(category, displayName);
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                long dbId = reader.GetInt64(0);
                object? value = reader.IsDBNull(1) ? null : reader.GetValue(1);
                result[dbId] = value;
            }
            return result;
        }

        /// <summary>
        /// Streams all property values for a specific category and display name
        /// into a caller-provided <see cref="ConcurrentDictionary{TKey,TValue}"/>.
        /// </summary>
        /// <param name="category">The category name of the property.</param>
        /// <param name="displayName">The display name (property name) readable by human.</param>
        /// <param name="dict">A ConcurrentDictionary to populate with (dbId, value) pairs.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A Task that completes when all values have been streamed into the dictionary.</returns>
        public async Task GetAllPropertyValuesStreamToConcurrentAsync(string category, string displayName, ConcurrentDictionary<long, object?> dict, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(category);
            ArgumentNullException.ThrowIfNull(displayName);
            ArgumentNullException.ThrowIfNull(dict);
            ThrowIfDisposed();

            using var cmd = CreateFilterCommand(category, displayName);
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                long dbId = reader.GetInt64(0);
                object? value = reader.IsDBNull(1) ? null : reader.GetValue(1);
                dict[dbId] = value;
            }
        }

        /// <summary>
        /// Gets all properties for all dbIds in the database.
        /// WARNING: For large models, this method can consume a lot of memory. Consider using <see cref="GetAllPropertiesStreamAsync"/>.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A dictionary mapping dbId to a dictionary of property key-value pairs.</returns>
        public async Task<Dictionary<long, Dictionary<string, object?>>> GetAllPropertiesAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            var result = new Dictionary<long, Dictionary<string, object?>>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = AllPropertiesSql;
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                long dbId = reader.GetInt64(0);
                string cat = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                string attr = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                object? value = reader.IsDBNull(3) ? null : reader.GetValue(3);
                string key = $"{cat}_{attr}";
                if (!result.TryGetValue(dbId, out var propDict))
                {
                    propDict = new Dictionary<string, object?>();
                    result[dbId] = propDict;
                }
                propDict[key] = value;
            }
            return result;
        }

        /// <summary>
        /// Streams all properties for all dbIds in the database.
        /// This is more memory efficient for large models than <see cref="GetAllPropertiesAsync"/>.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>An async enumerable of (dbId, key, value) tuples.</returns>
        public async IAsyncEnumerable<(long dbId, string key, object? value)> GetAllPropertiesStreamAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = AllPropertiesSql;
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string cat = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                string attr = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                object? value = reader.IsDBNull(3) ? null : reader.GetValue(3);
                long dbId = reader.GetInt64(0);
                string key = $"{cat}_{attr}";
                yield return (dbId, key, value);
            }
        }

        /// <summary>
        /// Gets the parent dbId for a given dbId, or null if none exists.
        /// </summary>
        /// <param name="dbId">The database ID of the element.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The parent dbId, or null if none exists.</returns>
        public async Task<long?> GetParentDbIdAsync(long dbId, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = ParentQuerySql;
            cmd.Parameters.AddWithValue("$dbId", dbId);
            var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result != null && long.TryParse(result.ToString(), out long parentId))
                return parentId;
            return null;
        }

        /// <summary>
        /// Gets the value for a specific property (by category and display name) for a given dbId.
        /// </summary>
        /// <param name="dbId">The database ID of the element.</param>
        /// <param name="category">The category name of the property.</param>
        /// <param name="displayName">The display name (property name).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The property value, or null if not found.</returns>
        public async Task<object?> GetPropertyValueAsync(long dbId, string category, string displayName, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(category);
            ArgumentNullException.ThrowIfNull(displayName);
            ThrowIfDisposed();

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = PropertyValueQuerySql;
            cmd.Parameters.AddWithValue("$dbId", dbId);
            cmd.Parameters.AddWithValue("$category", category);
            cmd.Parameters.AddWithValue("$displayName", displayName);
            return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Finds all dbIds where the given category, property name (display name), and value match.
        /// </summary>
        /// <param name="category">The category name of the property.</param>
        /// <param name="displayName">The display name (property name).</param>
        /// <param name="value">The value to match.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A list of dbIds matching the criteria.</returns>
        public async Task<List<long>> FindDbIdsByPropertyAsync(string category, string displayName, object value, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(category);
            ArgumentNullException.ThrowIfNull(displayName);
            ArgumentNullException.ThrowIfNull(value);
            ThrowIfDisposed();

            var dbIds = new List<long>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = FindByPropertySql;
            cmd.Parameters.AddWithValue("$category", category);
            cmd.Parameters.AddWithValue("$displayName", displayName);
            cmd.Parameters.AddWithValue("$value", value);
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                dbIds.Add(reader.GetInt64(0));
            }
            return dbIds;
        }

        /// <summary>
        /// Executes a custom SQL query with parameters and returns the results as a list of dictionaries (column name to value).
        /// </summary>
        /// <param name="sql">The SQL query string to execute.</param>
        /// <param name="parameters">Optional dictionary of parameter name-value pairs (parameter names should include the $ prefix).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A list of dictionaries, each representing a row (column name to value).</returns>
        public async Task<List<Dictionary<string, object?>>> QueryAsync(string sql, Dictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(sql);
            ThrowIfDisposed();

            var results = new List<Dictionary<string, object?>>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;

            if (parameters != null)
            {
                foreach (var param in parameters)
                {
                    cmd.Parameters.AddWithValue(param.Key, param.Value ?? DBNull.Value);
                }
            }

            using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    string colName = reader.GetName(i);
                    object? value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    row[colName] = value;
                }
                results.Add(row);
            }
            return results;
        }

        /// <summary>
        /// Gets all distinct property categories in the database.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A list of distinct category names.</returns>
        public async Task<List<string>> GetAllCategoriesAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            var categories = new List<string>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = AllCategoriesSql;
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                categories.Add(reader.GetString(0));
            }
            return categories;
        }

        /// <summary>
        /// Gets all distinct property display names (property names) in the database.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A list of distinct property display names.</returns>
        public async Task<List<string>> GetAllPropertyNamesAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            var names = new List<string>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = AllPropertyNamesSql;
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                names.Add(reader.GetString(0));
            }
            return names;
        }

        /// <summary>
        /// Gets all categories with their associated property display names.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A dictionary mapping each category name to a list of its property display names.</returns>
        public async Task<Dictionary<string, List<string>>> GetCategoriesWithPropertiesAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            var result = new Dictionary<string, List<string>>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = CategoriesWithPropertiesSql;
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
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
            return result;
        }

        /// <summary>
        /// Deletes the database file from disk. Returns true if the file was successfully deleted.
        /// </summary>
        /// <returns>True if the file was deleted; false otherwise.</returns>
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
            catch
            {
                // Silently ignore — caller can check the return value.
            }
            return false;
        }

        #region Fragment Locations

        /// <summary>
        /// Gets whether the database has an embedded <c>_fragment_locations</c> table with data.
        /// </summary>
        public bool HasFragmentLocations => HasEmbeddedLocationTable();

        /// <summary>
        /// Gets the number of embedded fragment locations by querying the database,
        /// or 0 if the table does not exist.
        /// </summary>
        public int FragmentLocationCount
        {
            get
            {
                try
                {
                    if (!HasEmbeddedLocationTable()) return 0;
                    using var cmd = _connection.CreateCommand();
                    cmd.CommandText = CountLocationsSql;
                    var result = cmd.ExecuteScalar();
                    return result != null ? Convert.ToInt32(result) : 0;
                }
                catch { return 0; }
            }
        }

        /// <summary>
        /// Downloads only the lightweight fragment location data (position + bounding box per dbID)
        /// for the specified model. This is much smaller than downloading full SVF derivatives.
        /// The returned dictionary is transient — use <see cref="EmbedLocationsIntoFileAsync"/>
        /// to persist it into a database file for disk-based queries.
        /// </summary>
        /// <param name="accessToken">Autodesk access token.</param>
        /// <param name="urn">The URN of the model.</param>
        /// <returns>A dictionary mapping each unique dbID to its <see cref="FragmentLocation"/>.</returns>
        public static async Task<Dictionary<int, FragmentLocation>> DownloadFragmentLocationsAsync(
            string accessToken, string urn)
        {
            ArgumentNullException.ThrowIfNull(accessToken);
            ArgumentNullException.ThrowIfNull(urn);

            return await Derivatives.ReadFragmentLocationsRemoteAsync(urn, accessToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Downloads fragment locations only for the specified set of dbIDs.
        /// Useful when you already know which elements you need from an SDB query.
        /// </summary>
        /// <param name="accessToken">Autodesk access token.</param>
        /// <param name="urn">The URN of the model.</param>
        /// <param name="targetDbIds">The set of dbIDs to retrieve locations for.</param>
        /// <returns>A dictionary mapping each requested dbID (found in fragments) to its <see cref="FragmentLocation"/>.</returns>
        public static async Task<Dictionary<int, FragmentLocation>> DownloadFragmentLocationsFilteredAsync(
            string accessToken, string urn, HashSet<int> targetDbIds)
        {
            ArgumentNullException.ThrowIfNull(accessToken);
            ArgumentNullException.ThrowIfNull(urn);
            ArgumentNullException.ThrowIfNull(targetDbIds);

            return await Derivatives.ReadFragmentLocationsFilteredRemoteAsync(urn, accessToken, targetDbIds).ConfigureAwait(false);
        }

        /// <summary>
        /// Downloads fragment locations and embeds them into this reader's database file.
        /// After calling this method, all location-aware query methods become available
        /// and serve data directly from the SQLite database (no in-memory storage).
        /// </summary>
        /// <param name="accessToken">Autodesk access token.</param>
        /// <param name="urn">The URN of the model.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public async Task EmbedFragmentLocationsAsync(string accessToken, string urn, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(accessToken);
            ArgumentNullException.ThrowIfNull(urn);
            ThrowIfDisposed();

            var locations = await Derivatives.ReadFragmentLocationsRemoteAsync(urn, accessToken).ConfigureAwait(false);
            await EmbedLocationsIntoFileAsync(_dbPath, locations, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets the fragment location for a specific dbId directly from the embedded SQLite table.
        /// No data is held in memory — each call reads from disk.
        /// </summary>
        /// <param name="dbId">The database ID of the element.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The fragment location, or null if not found or no embedded table exists.</returns>
        public async Task<FragmentLocation?> GetFragmentLocationAsync(int dbId, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return await GetEmbeddedFragmentLocationAsync(dbId, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets merged properties combined with location data for a specific dbId.
        /// Location is queried directly from the embedded SQLite table (disk-based).
        /// Requires the <c>_fragment_locations</c> table to exist in the database.
        /// </summary>
        /// <param name="dbId">The database ID of the element.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A tuple of (merged properties, fragment location or null if not found).</returns>
        public async Task<(Dictionary<string, object?> Properties, FragmentLocation? Location)> GetMergedPropertiesWithLocationAsync(
            long dbId, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            ThrowIfNoLocations();

            var props = await GetMergedPropertiesAsync(dbId, cancellationToken).ConfigureAwait(false);
            var location = await GetEmbeddedFragmentLocationAsync((int)dbId, cancellationToken).ConfigureAwait(false);

            return (props, location);
        }

        /// <summary>
        /// Gets direct properties (without parent merging) combined with location data for a specific dbId.
        /// Location is queried directly from the embedded SQLite table (disk-based).
        /// Requires the <c>_fragment_locations</c> table to exist in the database.
        /// </summary>
        /// <param name="dbId">The database ID of the element.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A tuple of (direct properties, fragment location or null if not found).</returns>
        public async Task<(Dictionary<string, object?> Properties, FragmentLocation? Location)> GetPropertiesWithLocationAsync(
            long dbId, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            ThrowIfNoLocations();

            var props = await GetPropertiesForDbIdAsync(dbId, cancellationToken).ConfigureAwait(false);
            var location = await GetEmbeddedFragmentLocationAsync((int)dbId, cancellationToken).ConfigureAwait(false);

            return (props, location);
        }

        /// <summary>
        /// Streams all dbIds that have embedded fragment locations, along with their merged properties.
        /// Only yields elements that exist in both the property database and fragment location table.
        /// All data is read from disk — nothing is held in memory.
        /// Requires the <c>_fragment_locations</c> table to exist in the database.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>An async enumerable of (dbId, properties, location) tuples.</returns>
        /// <exception cref="InvalidOperationException">Thrown if the location table does not exist.</exception>
        public async IAsyncEnumerable<(long DbId, Dictionary<string, object?> Properties, FragmentLocation Location)> GetAllPropertiesWithLocationsStreamAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            ThrowIfNoLocations();

            await foreach (var (dbId, location) in GetEmbeddedFragmentLocationsStreamAsync(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var props = await GetMergedPropertiesAsync(dbId, cancellationToken).ConfigureAwait(false);
                yield return (dbId, props, location);
            }
        }

        /// <summary>
        /// Gets properties with locations for a batch of dbIds.
        /// Locations are queried from the embedded SQLite table (disk-based).
        /// Only returns entries that have fragment locations.
        /// Requires the <c>_fragment_locations</c> table to exist in the database.
        /// </summary>
        /// <param name="dbIds">The dbIds to retrieve.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A list of (dbId, properties, location) tuples for dbIds that have locations.</returns>
        /// <exception cref="InvalidOperationException">Thrown if the location table does not exist.</exception>
        public async Task<List<(long DbId, Dictionary<string, object?> Properties, FragmentLocation Location)>> GetPropertiesWithLocationsBatchAsync(
            IEnumerable<int> dbIds, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(dbIds);
            ThrowIfDisposed();
            ThrowIfNoLocations();

            var results = new List<(long, Dictionary<string, object?>, FragmentLocation)>();
            foreach (var dbId in dbIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var location = await GetEmbeddedFragmentLocationAsync(dbId, cancellationToken).ConfigureAwait(false);
                if (location.HasValue)
                {
                    var props = await GetMergedPropertiesAsync(dbId, cancellationToken).ConfigureAwait(false);
                    results.Add((dbId, props, location.Value));
                }
            }
            return results;
        }

        /// <summary>
        /// Finds all dbIds matching a property filter and returns their locations alongside the property value.
        /// Queries the SDB property tables first, then looks up locations from the embedded SQLite table.
        /// Requires the <c>_fragment_locations</c> table to exist in the database.
        /// </summary>
        /// <param name="category">The category name of the property.</param>
        /// <param name="displayName">The display name (property name).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A list of (dbId, propertyValue, location) tuples for matches that have locations.</returns>
        /// <exception cref="InvalidOperationException">Thrown if the location table does not exist.</exception>
        public async Task<List<(long DbId, object? PropertyValue, FragmentLocation Location)>> FindByPropertyWithLocationsAsync(
            string category, string displayName, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(category);
            ArgumentNullException.ThrowIfNull(displayName);
            ThrowIfDisposed();
            ThrowIfNoLocations();

            var results = new List<(long, object?, FragmentLocation)>();
            var propertyValues = await GetAllPropertyValuesAsync(category, displayName, cancellationToken).ConfigureAwait(false);

            foreach (var kvp in propertyValues)
            {
                var location = await GetEmbeddedFragmentLocationAsync((int)kvp.Key, cancellationToken).ConfigureAwait(false);
                if (location.HasValue)
                {
                    results.Add((kvp.Key, kvp.Value, location.Value));
                }
            }

            return results;
        }

        /// <summary>
        /// Streams dbIds matching a property filter along with their locations from the embedded SQLite table.
        /// Requires the <c>_fragment_locations</c> table to exist in the database.
        /// </summary>
        /// <param name="category">The category name of the property.</param>
        /// <param name="displayName">The display name (property name).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>An async enumerable of (dbId, propertyValue, location) tuples.</returns>
        /// <exception cref="InvalidOperationException">Thrown if the location table does not exist.</exception>
        public async IAsyncEnumerable<(long DbId, object? PropertyValue, FragmentLocation Location)> FindByPropertyWithLocationsStreamAsync(
            string category, string displayName,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(category);
            ArgumentNullException.ThrowIfNull(displayName);
            ThrowIfDisposed();
            ThrowIfNoLocations();

            await foreach (var (dbId, value) in GetAllPropertyValuesStreamAsync(category, displayName, cancellationToken))
            {
                var location = await GetEmbeddedFragmentLocationAsync((int)dbId, cancellationToken).ConfigureAwait(false);
                if (location.HasValue)
                {
                    yield return (dbId, value, location.Value);
                }
            }
        }

        /// <summary>
        /// Finds dbIds matching a specific property value and returns their merged properties with locations.
        /// This is the most comprehensive combined query — it filters by property, merges parent properties,
        /// and includes location data from the embedded SQLite table, all in one call.
        /// Requires the <c>_fragment_locations</c> table to exist in the database.
        /// </summary>
        /// <param name="category">The category name of the property to filter by.</param>
        /// <param name="displayName">The display name (property name) to filter by.</param>
        /// <param name="value">The value to match.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A list of (dbId, mergedProperties, location) tuples for matching elements with locations.</returns>
        /// <exception cref="InvalidOperationException">Thrown if the location table does not exist.</exception>
        public async Task<List<(long DbId, Dictionary<string, object?> Properties, FragmentLocation Location)>> FindByPropertyWithFullDataAsync(
            string category, string displayName, object value, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(category);
            ArgumentNullException.ThrowIfNull(displayName);
            ArgumentNullException.ThrowIfNull(value);
            ThrowIfDisposed();
            ThrowIfNoLocations();

            var dbIds = await FindDbIdsByPropertyAsync(category, displayName, value, cancellationToken).ConfigureAwait(false);
            var results = new List<(long, Dictionary<string, object?>, FragmentLocation)>();

            foreach (var dbId in dbIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var location = await GetEmbeddedFragmentLocationAsync((int)dbId, cancellationToken).ConfigureAwait(false);
                if (location.HasValue)
                {
                    var props = await GetMergedPropertiesAsync(dbId, cancellationToken).ConfigureAwait(false);
                    results.Add((dbId, props, location.Value));
                }
            }

            return results;
        }

        /// <summary>
        /// Writes the provided fragment locations into the specified SQLite database file
        /// as a <c>_fragment_locations</c> table. The file is opened in read-write mode
        /// only for this operation and then closed. This is a static helper usable before
        /// constructing a <see cref="PropDbReader"/>.
        /// </summary>
        /// <param name="dbPath">Path to the existing .sdb SQLite file.</param>
        /// <param name="locations">The fragment locations to embed.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public static async Task EmbedLocationsIntoFileAsync(
            string dbPath, Dictionary<int, FragmentLocation> locations, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(dbPath);
            ArgumentNullException.ThrowIfNull(locations);

            // Open a separate read-write connection just for this operation
            using var rwConn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWrite;Pooling=False");
            await rwConn.OpenAsync(cancellationToken).ConfigureAwait(false);

            using var transaction = rwConn.BeginTransaction();
            try
            {
                // Create table
                using (var cmd = rwConn.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = CreateLocationTableSql;
                    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                // Bulk insert using a prepared statement
                using (var cmd = rwConn.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = InsertLocationSql;

                    var pDbId = cmd.Parameters.Add("$dbId", Microsoft.Data.Sqlite.SqliteType.Integer);
                    var pX = cmd.Parameters.Add("$x", Microsoft.Data.Sqlite.SqliteType.Real);
                    var pY = cmd.Parameters.Add("$y", Microsoft.Data.Sqlite.SqliteType.Real);
                    var pZ = cmd.Parameters.Add("$z", Microsoft.Data.Sqlite.SqliteType.Real);
                    var pMinX = cmd.Parameters.Add("$minX", Microsoft.Data.Sqlite.SqliteType.Real);
                    var pMinY = cmd.Parameters.Add("$minY", Microsoft.Data.Sqlite.SqliteType.Real);
                    var pMinZ = cmd.Parameters.Add("$minZ", Microsoft.Data.Sqlite.SqliteType.Real);
                    var pMaxX = cmd.Parameters.Add("$maxX", Microsoft.Data.Sqlite.SqliteType.Real);
                    var pMaxY = cmd.Parameters.Add("$maxY", Microsoft.Data.Sqlite.SqliteType.Real);
                    var pMaxZ = cmd.Parameters.Add("$maxZ", Microsoft.Data.Sqlite.SqliteType.Real);

                    foreach (var kvp in locations)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        pDbId.Value = kvp.Key;
                        pX.Value = kvp.Value.X;
                        pY.Value = kvp.Value.Y;
                        pZ.Value = kvp.Value.Z;
                        pMinX.Value = kvp.Value.MinX;
                        pMinY.Value = kvp.Value.MinY;
                        pMinZ.Value = kvp.Value.MinZ;
                        pMaxX.Value = kvp.Value.MaxX;
                        pMaxY.Value = kvp.Value.MaxY;
                        pMaxZ.Value = kvp.Value.MaxZ;

                        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        /// <summary>
        /// Gets the location for a single dbId directly from the embedded SQLite table,
        /// without requiring the full location set to be loaded in memory.
        /// Useful for one-off lookups when you don't need all locations at once.
        /// </summary>
        /// <param name="dbId">The database ID of the element.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The <see cref="FragmentLocation"/>, or null if not found or no embedded table exists.</returns>
        public async Task<FragmentLocation?> GetEmbeddedFragmentLocationAsync(int dbId, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = SelectLocationByIdSql;
            cmd.Parameters.AddWithValue("$dbId", dbId);

            using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new FragmentLocation(
                    (float)reader.GetDouble(0), (float)reader.GetDouble(1), (float)reader.GetDouble(2),
                    (float)reader.GetDouble(3), (float)reader.GetDouble(4), (float)reader.GetDouble(5),
                    (float)reader.GetDouble(6), (float)reader.GetDouble(7), (float)reader.GetDouble(8));
            }
            return null;
        }

        /// <summary>
        /// Streams all embedded fragment locations directly from the SQLite table,
        /// without loading them all into memory at once.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>An async enumerable of (dbId, location) tuples.</returns>
        public async IAsyncEnumerable<(int DbId, FragmentLocation Location)> GetEmbeddedFragmentLocationsStreamAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = SelectAllLocationsSql;

            using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                int dbId = reader.GetInt32(0);
                var location = new FragmentLocation(
                    (float)reader.GetDouble(1), (float)reader.GetDouble(2), (float)reader.GetDouble(3),
                    (float)reader.GetDouble(4), (float)reader.GetDouble(5), (float)reader.GetDouble(6),
                    (float)reader.GetDouble(7), (float)reader.GetDouble(8), (float)reader.GetDouble(9));
                yield return (dbId, location);
            }
        }

        /// <summary>
        /// Checks whether the SQLite database contains an embedded <c>_fragment_locations</c> table.
        /// </summary>
        public bool HasEmbeddedLocationTable()
        {
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = LocationTableExistsSql;
                var result = cmd.ExecuteScalar();
                return result != null && Convert.ToInt64(result) > 0;
            }
            catch
            {
                return false;
            }
        }

        private void ThrowIfNoLocations()
        {
            if (!HasEmbeddedLocationTable())
                throw new InvalidOperationException(
                    "Fragment locations not available. Use CreateWithEmbeddedLocationsAsync or EmbedFragmentLocationsAsync to embed locations into the database first.");
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Creates a <see cref="SqliteCommand"/> configured with the category/displayName filter query.
        /// The caller is responsible for disposing the returned command.
        /// </summary>
        private SqliteCommand CreateFilterCommand(string category, string displayName)
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = PropertyByFilterSql;
            cmd.Parameters.AddWithValue("$category", category);
            cmd.Parameters.AddWithValue("$displayName", displayName);
            return cmd;
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        #endregion

        #region IDisposable

        /// <summary>
        /// Disposes the database connection and releases all resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases managed and/or unmanaged resources.
        /// </summary>
        /// <param name="disposing">True if called from <see cref="Dispose()"/>; false if called from the finalizer.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                // Dispose managed resources
                if (_connection.State != System.Data.ConnectionState.Closed)
                    _connection.Close();
                _connection.Dispose();
            }

            // Clean up unmanaged resources (file on disk)
            if (_deleteDbOnDispose)
            {
                try { File.Delete(_dbPath); } catch { }
            }

            _disposed = true;
        }

        /// <summary>
        /// Destructor — ensures the database file is cleaned up even if Dispose was not called.
        /// </summary>
        ~PropDbReader()
        {
            Dispose(disposing: false);
        }

        #endregion
    }
}
