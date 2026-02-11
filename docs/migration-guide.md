# Migration Guide: v1.0.x → v1.1.0

This guide covers all breaking changes and new features in v1.1.0.

---

## Breaking Changes

### 1. Synchronous Constructor Removed

The constructor `PropDbReader(string accessToken, string urn)` has been **removed** because it used `.GetAwaiter().GetResult()` internally, which can cause deadlocks in UI/ASP.NET contexts.

**Before (v1.0.x):**
```csharp
// ❌ No longer available
using var reader = new PropDbReader(accessToken, urn);
```

**After (v1.1.0):**
```csharp
// ✅ Use the async factory instead
using var reader = await PropDbReader.CreateAsync(accessToken, urn);
```

### 2. `DownloadAndGetPath` Renamed

The static method was renamed to `DownloadAndGetPathAsync` to follow .NET async naming conventions.

**Before (v1.0.x):**
```csharp
var path = await PropDbReader.DownloadAndGetPath(token, urn);
```

**After (v1.1.0):**
```csharp
var path = await PropDbReader.DownloadAndGetPathAsync(token, urn);
```

### 3. `QueryAsync` Signature Changed

`QueryAsync` now accepts an optional parameters dictionary. Existing calls without parameters still compile without changes.

**Before (v1.0.x):**
```csharp
var results = await reader.QueryAsync("SELECT ...");
```

**After (v1.1.0) — both work:**
```csharp
// Still works — parameters are optional
var results = await reader.QueryAsync("SELECT ...");

// New: parameterized queries (recommended)
var results = await reader.QueryAsync(
    "SELECT * FROM _objects_attr WHERE category = $cat",
    new Dictionary<string, object?> { ["$cat"] = "Dimensions" }
);
```

### 4. `ManifestHelper` Now Has a Namespace

`ManifestHelper` moved from the global namespace into `SVF.PropDbReader`. If you were using it directly:

**Before (v1.0.x):**
```csharp
var helper = new ManifestHelper(manifest);
```

**After (v1.1.0):**
```csharp
using SVF.PropDbReader;
var helper = new ManifestHelper(manifest);
```

---

## New Features

### CancellationToken Support

All async methods now accept an optional `CancellationToken`:

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var props = await reader.GetPropertiesForDbIdAsync(1, cts.Token);
```

### New: `GetAllPropertiesStreamAsync()`

Memory-efficient alternative to `GetAllPropertiesAsync()` for large models:

```csharp
await foreach (var (dbId, key, value) in reader.GetAllPropertiesStreamAsync())
{
    Console.WriteLine($"dbId {dbId}: {key} = {value}");
}
```

### Parameterized `QueryAsync`

Safe parameterized custom queries:

```csharp
var results = await reader.QueryAsync(
    "SELECT * FROM _objects_attr WHERE category = $cat",
    new Dictionary<string, object?> { ["$cat"] = "Dimensions" }
);
```

---

## Internal Improvements (Non-Breaking)

| Improvement | Benefit |
|---|---|
| `ConfigureAwait(false)` on all awaits | Prevents deadlocks in UI/web contexts |
| Proper `Dispose(bool)` pattern | Safe finalization, idempotent dispose |
| Static shared `HttpClient` | Prevents socket exhaustion |
| SQL extracted as constants | Eliminates duplication, easier maintenance |
| `ArgumentNullException.ThrowIfNull` | Fail-fast on null arguments |
| Removed `Console.WriteLine` from library | Libraries should not write to console |
