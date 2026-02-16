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

Access 3D element locations (translation coordinates + bounding box) without downloading full fragment geometry.

### Why Use Fragment Locations?

Traditional fragment downloads from APS include:
- Material assignments
- Geometry references  
- Full transformation matrices
- Rendering metadata

**This can be 100-500 MB for large models**, but you often only need position data (36 bytes per element).

### Option A — Memory-Only Locations

Download locations into memory. They're discarded when the reader is disposed:

```csharp
using SVF.PropDbReader;

// Download properties + locations together
using var reader = await PropDbReader.CreateWithLocationsAsync(accessToken, urn);

// Get properties with location in one call
var result = await reader.GetPropertiesWithLocationAsync(dbId: 1);

Console.WriteLine($"Element at: ({result.Location.X}, {result.Location.Y}, {result.Location.Z})");
Console.WriteLine($"Bounding box: Min ({result.Location.MinX}, {result.Location.MinY}, {result.Location.MinZ})");
Console.WriteLine($"              Max ({result.Location.MaxX}, {result.Location.MaxY}, {result.Location.MaxZ})");

foreach (var prop in result.Properties)
{
    Console.WriteLine($"  {prop.Key}: {prop.Value}");
}
```

### Option B — Embedded Locations (Persistent)

Embed locations into the `.sdb` file. Subsequent opens load locations from the database — **no re-download**:

```csharp
// First run: download and embed locations
using var reader = await PropDbReader.CreateWithEmbeddedLocationsAsync(accessToken, urn);
Console.WriteLine($"Locations embedded. File: {reader.DbPath}");

// Later: open the same file
using var cachedReader = new PropDbReader(reader.DbPath);

// Locations are auto-loaded from the database
if (cachedReader.HasFragmentLocations)
{
    var location = await cachedReader.GetEmbeddedFragmentLocationAsync(dbId: 1);
    Console.WriteLine($"Cached location: ({location.X}, {location.Y}, {location.Z})");
}
```

### Combined Queries

Query properties and locations together for filtering and analysis:

```csharp
using var reader = await PropDbReader.CreateWithLocationsAsync(accessToken, urn);

// Find all walls with locations
var walls = await reader.FindByPropertyWithLocationsAsync("__category__", "", "Walls");

foreach (var (dbId, properties, location) in walls)
{
    Console.WriteLine($"Wall {dbId}:");
    Console.WriteLine($"  Position: ({location.X}, {location.Y}, {location.Z})");
    Console.WriteLine($"  Height: {location.MaxZ - location.MinZ:F2}");
}

// Stream all elements with locations (memory-efficient)
await foreach (var (dbId, props, loc) in reader.GetAllPropertiesWithLocationsStreamAsync())
{
    if (loc.Z > 10.0)  // Filter by elevation
    {
        Console.WriteLine($"Element {dbId} is elevated at Z={loc.Z}");
    }
}
```

### Filtering by DbIds

If you already know which elements you need (e.g., from a property query), download only those locations:

```csharp
// First, get dbIds from property query
var wallIds = await reader.FindDbIdsByPropertyAsync("__category__", "", "Walls");

// Download locations only for those specific elements
var locations = await PropDbReader.DownloadFragmentLocationsFilteredAsync(
    accessToken, 
    urn, 
    wallIds.ToHashSet()
);

foreach (var (dbId, location) in locations)
{
    Console.WriteLine($"Wall {dbId}: {location.X}, {location.Y}, {location.Z}");
}
```

---

## Next Steps

- [API Reference](api-reference.md) — Full method signatures and usage examples
- [Architecture](architecture.md) — Database schema, class diagrams, data flow
- [Migration Guide](migration-guide.md) — Upgrading from v1.0.x to v1.1.0
