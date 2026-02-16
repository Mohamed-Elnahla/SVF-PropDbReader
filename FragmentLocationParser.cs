using System.Collections.Generic;
using APSToolkit;
using APSToolkit.Schema;

namespace SVF.PropDbReader
{
    /// <summary>
    /// Lightweight parser that extracts only dbID-to-location mappings from SVF fragment binary data.
    /// Unlike <see cref="Fragments.ParseFragments"/>, this avoids allocating full ISvfFragment objects,
    /// significantly reducing memory usage for large models.
    /// </summary>
    internal static class FragmentLocationParser
    {
        /// <summary>
        /// Parses a FragmentList.pack binary buffer and returns a dictionary mapping each unique dbID
        /// to its <see cref="FragmentLocation"/>. Only the first occurrence of each dbID is kept.
        /// Fragments without a valid transform are skipped.
        /// </summary>
        /// <param name="buffer">The raw binary data from a FragmentList.pack file.</param>
        /// <returns>A dictionary of dbID to FragmentLocation.</returns>
        public static Dictionary<int, FragmentLocation> Parse(byte[] buffer)
        {
            var locations = new Dictionary<int, FragmentLocation>();
            var pfr = new PackFileReader(buffer);

            for (int i = 0, len = pfr.NumEntries(); i < len; i++)
            {
                ISvfManifestType? entryType = pfr.SeekEntry(i);
                if (entryType == null) continue;

                // Read all fields to advance the reader offset (even if we don't use them all)
                pfr.GetUint8();    // flags
                pfr.GetVarint();   // materialId
                pfr.GetVarint();   // geometryId

                ISvfTransform? transform = pfr.GetTransform();

                float bboxOffsetX = 0, bboxOffsetY = 0, bboxOffsetZ = 0;
                if (entryType.Value.version > 3 && transform.HasValue)
                {
                    bboxOffsetX = transform.Value.t.X;
                    bboxOffsetY = transform.Value.t.Y;
                    bboxOffsetZ = transform.Value.t.Z;
                }

                float minX = pfr.GetFloat32() + bboxOffsetX;
                float minY = pfr.GetFloat32() + bboxOffsetY;
                float minZ = pfr.GetFloat32() + bboxOffsetZ;
                float maxX = pfr.GetFloat32() + bboxOffsetX;
                float maxY = pfr.GetFloat32() + bboxOffsetY;
                float maxZ = pfr.GetFloat32() + bboxOffsetZ;

                int dbId = pfr.GetVarint();

                // Only keep the first occurrence per dbID, and only if transform is valid
                if (transform.HasValue && !locations.ContainsKey(dbId))
                {
                    locations[dbId] = new FragmentLocation(
                        transform.Value.t.X, transform.Value.t.Y, transform.Value.t.Z,
                        minX, minY, minZ,
                        maxX, maxY, maxZ);
                }
            }

            return locations;
        }

        /// <summary>
        /// Parses a FragmentList.pack binary buffer and returns locations only for the specified dbIDs.
        /// This is useful when you already know which dbIDs you care about (e.g., from an SDB query).
        /// </summary>
        /// <param name="buffer">The raw binary data from a FragmentList.pack file.</param>
        /// <param name="targetDbIds">The set of dbIDs to retrieve locations for.</param>
        /// <returns>A dictionary of dbID to FragmentLocation for the requested IDs that were found.</returns>
        public static Dictionary<int, FragmentLocation> ParseFiltered(byte[] buffer, HashSet<int> targetDbIds)
        {
            var locations = new Dictionary<int, FragmentLocation>();
            var pfr = new PackFileReader(buffer);

            for (int i = 0, len = pfr.NumEntries(); i < len; i++)
            {
                ISvfManifestType? entryType = pfr.SeekEntry(i);
                if (entryType == null) continue;

                pfr.GetUint8();    // flags
                pfr.GetVarint();   // materialId
                pfr.GetVarint();   // geometryId

                ISvfTransform? transform = pfr.GetTransform();

                float bboxOffsetX = 0, bboxOffsetY = 0, bboxOffsetZ = 0;
                if (entryType.Value.version > 3 && transform.HasValue)
                {
                    bboxOffsetX = transform.Value.t.X;
                    bboxOffsetY = transform.Value.t.Y;
                    bboxOffsetZ = transform.Value.t.Z;
                }

                float minX = pfr.GetFloat32() + bboxOffsetX;
                float minY = pfr.GetFloat32() + bboxOffsetY;
                float minZ = pfr.GetFloat32() + bboxOffsetZ;
                float maxX = pfr.GetFloat32() + bboxOffsetX;
                float maxY = pfr.GetFloat32() + bboxOffsetY;
                float maxZ = pfr.GetFloat32() + bboxOffsetZ;

                int dbId = pfr.GetVarint();

                if (transform.HasValue && targetDbIds.Contains(dbId) && !locations.ContainsKey(dbId))
                {
                    locations[dbId] = new FragmentLocation(
                        transform.Value.t.X, transform.Value.t.Y, transform.Value.t.Z,
                        minX, minY, minZ,
                        maxX, maxY, maxZ);
                }
            }

            return locations;
        }
    }
}
