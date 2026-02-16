# Getting Started

## Installation

### Using .NET CLI

```shell
dotnet add package SVF.PropDbReader
```

### Using Package Manager Console

```powershell
Install-Package SVF.PropDbReader
```

### Using PackageReference (in `.csproj`)

```xml
<PackageReference Include="SVF.PropDbReader" Version="1.1.0.0" />
```

> **Requirements:** .NET 8.0 or later.

---

## Quick Start

### Option A — Read from a Local `.sdb` File

```csharp
using SVF.PropDbReader;

string dbPath = @"C:\path\to\your\properties.sdb";

using var reader = new PropDbReader(dbPath);

// Get all properties for element with dbId = 1
var props = await reader.GetPropertiesForDbIdAsync(1);

foreach (var kvp in props)
{
    Console.WriteLine($"{kvp.Key}: {kvp.Value}");
}
```

### Option B — Download from Autodesk APS (Async Factory — Recommended)

```csharp
using SVF.PropDbReader;

string accessToken = "<YOUR_ACCESS_TOKEN>";
string urn = "<YOUR_MODEL_URN>";

// Fully async — no synchronous blocking
using var reader = await PropDbReader.CreateAsync(accessToken, urn);

var props = await reader.GetPropertiesForDbIdAsync(1);
foreach (var kvp in props)
{
    Console.WriteLine($"{kvp.Key}: {kvp.Value}");
}
```

### Option C — Download Only (Get Path Without Opening)

```csharp
using SVF.PropDbReader;

// Download the .sdb file and get the local path
string dbPath = await PropDbReader.DownloadAndGetPathAsync("<ACCESS_TOKEN>", "<MODEL_URN>");

Console.WriteLine($"Database saved to: {dbPath}");

// Open it later
using var reader = new PropDbReader(dbPath, deleteDbOnDispose: true);
```

---

## CancellationToken Support

All async methods accept an optional `CancellationToken` for cancellation support:

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

using var reader = await PropDbReader.CreateAsync(accessToken, urn, cts.Token);
var props = await reader.GetPropertiesForDbIdAsync(1, cts.Token);
```

---

## Fragment Locations (New in v1.1.0)

Access 3D element locations (translation coordinates + bounding box) without downloading full fragment geometry. All location data is stored on disk in the `.sdb` SQLite database — **zero memory overhead**.

### Why Use Fragment Locations?

Traditional fragment downloads from APS include:
- Material assignments
- Geometry references  
- Full transformation matrices
- Rendering metadata

**This can be 100-500 MB for large models**, but you often only need position data (36 bytes per element).

Fragment locations are embedded directly into the `.sdb` file as a `_fragment_locations` SQLite table. Every query reads from disk — nothing is cached in memory.

### Embedding Locations

Download the `.sdb` and embed fragment locations in one step:

```csharp
using SVF.PropDbReader;

// Download + embed locations into the .sdb file (all on disk)
using var reader = await PropDbReader.CreateWithEmbeddedLocationsAsync(accessToken, urn);
Console.WriteLine($"Locations embedded. File: {reader.DbPath}");
Console.WriteLine($"Location count: {reader.FragmentLocationCount}");
```

Or embed locations into an existing `.sdb` file:

```csharp
// Open an existing database
using var reader = new PropDbReader("properties.sdb");

// Embed locations (downloads fragment data, writes to SQLite, discards download)
await reader.EmbedFragmentLocationsAsync(accessToken, urn);
Console.WriteLine($"Has locations: {reader.HasFragmentLocations}");
```

### Querying Locations

Subsequent opens read locations directly from the database — **no re-download**:

```csharp
// Open a file that already has embedded locations
using var reader = new PropDbReader("properties.sdb");

if (reader.HasFragmentLocations)
{
    var location = await reader.GetFragmentLocationAsync(dbId: 1);
    if (location.HasValue)
    {
        Console.WriteLine($"Position: ({location.Value.X}, {location.Value.Y}, {location.Value.Z})");
        Console.WriteLine($"Bbox Min: ({location.Value.MinX}, {location.Value.MinY}, {location.Value.MinZ})");
        Console.WriteLine($"Bbox Max: ({location.Value.MaxX}, {location.Value.MaxY}, {location.Value.MaxZ})");
    }
}
```

### Combined Queries

Query properties and locations together. All location lookups go through SQLite on disk:

```csharp
using var reader = await PropDbReader.CreateWithEmbeddedLocationsAsync(accessToken, urn);

// Get properties + location for a single element
var result = await reader.GetPropertiesWithLocationAsync(dbId: 1);
Console.WriteLine($"Element at: ({result.Location.X}, {result.Location.Y}, {result.Location.Z})");

foreach (var prop in result.Properties)
{
    Console.WriteLine($"  {prop.Key}: {prop.Value}");
}

// Find all walls with locations
var walls = await reader.FindByPropertyWithLocationsAsync("__category__", "", "Walls");

foreach (var (dbId, properties, location) in walls)
{
    Console.WriteLine($"Wall {dbId}:");
    Console.WriteLine($"  Position: ({location.X}, {location.Y}, {location.Z})");
    Console.WriteLine($"  Height: {location.MaxZ - location.MinZ:F2}");
}

// Stream all elements with locations (memory-efficient — one row at a time from disk)
await foreach (var (dbId, props, loc) in reader.GetAllPropertiesWithLocationsStreamAsync())
{
    if (loc.Z > 10.0)  // Filter by elevation
    {
        Console.WriteLine($"Element {dbId} is elevated at Z={loc.Z}");
    }
}
```

---

## Next Steps

- [API Reference](api-reference.md) — Full method signatures and usage examples
- [Architecture](architecture.md) — Database schema, class diagrams, data flow
- [Migration Guide](migration-guide.md) — Upgrading from v1.0.x to v1.1.0
