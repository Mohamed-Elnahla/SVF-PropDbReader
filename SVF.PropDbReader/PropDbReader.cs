using System;
using System.Collections.Generic;
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

        /// <summary>
        /// Opens the property database at the given path.
        /// </summary>
        public PropDbReader(string accessToken, string urn)
        {
            var dpDownloader = new DbDownloader(accessToken);
            string dbPath = dpDownloader.DownloadPropertiesDatabaseAsync(urn).Result ?? throw new InvalidOperationException("Failed to download properties database.");
            _connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
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

        public PropDbReader(string dbPath)
        {
            _connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
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
        public async Task<Dictionary<string, object>> GetMergedPropertiesAsync(long dbId)
        {
            var cache = new Dictionary<long, Dictionary<string, object>>();
            var props = await GetPropertiesForDbIdAsync(dbId);
            cache[dbId] = props;
            return await MergeParentPropertiesAsync(props, cache);
        }

        /// <summary>
        /// Gets the direct properties for a given dbId.
        /// </summary>
        public async Task<Dictionary<string, object>> GetPropertiesForDbIdAsync(long dbId)
        {
            _propertyQuery.Parameters["$dbId"].Value = dbId;
            var props = new Dictionary<string, object>();

            using (var reader = await _propertyQuery.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    string cat = await reader.IsDBNullAsync(0) ? string.Empty : reader.GetString(0);
                    string attr = await reader.IsDBNullAsync(1) ? string.Empty : reader.GetString(1);
                    object value = await reader.IsDBNullAsync(2) ? string.Empty : reader.GetValue(2);
                    string key = $"{cat}_{attr}";
                    props[key] = value;
                }
            }
            return props;
        }

        /// <summary>
        /// Recursively merges parent properties into the given property dictionary.
        /// </summary>
        private async Task<Dictionary<string, object>> MergeParentPropertiesAsync(Dictionary<string, object> childProps, Dictionary<long, Dictionary<string, object>> cache)
        {
            const string parentKey = "__parent___null";
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
        /// Disposes the database connection.
        /// </summary>
        public void Dispose()
        {
            _propertyQuery?.Dispose();
            _connection?.Dispose();
        }
    }
}
