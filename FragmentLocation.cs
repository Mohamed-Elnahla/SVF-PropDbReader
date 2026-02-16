using System;

namespace SVF.PropDbReader
{
    /// <summary>
    /// Lightweight location data extracted from SVF fragments.
    /// Contains only the translation position and bounding box,
    /// avoiding the memory overhead of full ISvfFragment objects.
    /// </summary>
    public readonly struct FragmentLocation : IEquatable<FragmentLocation>
    {
        /// <summary>Translation X coordinate (world position).</summary>
        public float X { get; }

        /// <summary>Translation Y coordinate (world position).</summary>
        public float Y { get; }

        /// <summary>Translation Z coordinate (world position).</summary>
        public float Z { get; }

        /// <summary>Bounding box minimum X.</summary>
        public float MinX { get; }

        /// <summary>Bounding box minimum Y.</summary>
        public float MinY { get; }

        /// <summary>Bounding box minimum Z.</summary>
        public float MinZ { get; }

        /// <summary>Bounding box maximum X.</summary>
        public float MaxX { get; }

        /// <summary>Bounding box maximum Y.</summary>
        public float MaxY { get; }

        /// <summary>Bounding box maximum Z.</summary>
        public float MaxZ { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="FragmentLocation"/> struct.
        /// </summary>
        public FragmentLocation(
            float x, float y, float z,
            float minX, float minY, float minZ,
            float maxX, float maxY, float maxZ)
        {
            X = x; Y = y; Z = z;
            MinX = minX; MinY = minY; MinZ = minZ;
            MaxX = maxX; MaxY = maxY; MaxZ = maxZ;
        }

        /// <inheritdoc />
        public bool Equals(FragmentLocation other) =>
            X == other.X && Y == other.Y && Z == other.Z &&
            MinX == other.MinX && MinY == other.MinY && MinZ == other.MinZ &&
            MaxX == other.MaxX && MaxY == other.MaxY && MaxZ == other.MaxZ;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is FragmentLocation other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(X, Y, Z, MinX, MinY, MinZ, MaxX, MaxY);

        /// <summary>Equality operator.</summary>
        public static bool operator ==(FragmentLocation left, FragmentLocation right) => left.Equals(right);

        /// <summary>Inequality operator.</summary>
        public static bool operator !=(FragmentLocation left, FragmentLocation right) => !left.Equals(right);

        /// <inheritdoc />
        public override string ToString() =>
            $"Pos({X:F3}, {Y:F3}, {Z:F3}) BBox[({MinX:F3},{MinY:F3},{MinZ:F3})-({MaxX:F3},{MaxY:F3},{MaxZ:F3})]";
    }
}
