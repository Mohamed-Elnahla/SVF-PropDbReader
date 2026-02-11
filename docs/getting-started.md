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

## Next Steps

- [API Reference](api-reference.md) — Full method signatures and usage examples
- [Architecture](architecture.md) — Database schema, class diagrams, data flow
- [Migration Guide](migration-guide.md) — Upgrading from v1.0.x to v1.1.0
