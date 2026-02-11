# SVF-PropDbReader

<p align="center">
  <img src="Resources/Logo.png" alt="SVF-PropDbReader Logo" width="90%"/>
</p>

[![NuGet Version](https://img.shields.io/nuget/v/SVF.PropDbReader.svg)](https://www.nuget.org/packages/SVF.PropDbReader)
[![NuGet Downloads](https://img.shields.io/nuget/dt/SVF.PropDbReader.svg)](https://www.nuget.org/packages/SVF.PropDbReader)
[![.NET 10.0 | 9.0 | 8.0](https://img.shields.io/badge/.NET-10.0%20%7C%209.0%20%7C%208.0-blue.svg)](https://dotnet.microsoft.com/download)
[![License](https://img.shields.io/badge/license-Apache--2.0-green.svg)](LICENSE.txt)
[![Build & Test](https://github.com/Mohamed-Elnahla/SVF-PropDbReader/actions/workflows/build-test.yml/badge.svg)](https://github.com/Mohamed-Elnahla/SVF-PropDbReader/actions/workflows/build-test.yml)

---

## Overview

**SVF-PropDbReader** is a .NET library (targeting .NET 10.0, 9.0, and 8.0) for reading and extracting property database (PropDb) information from SVF files. SVF is the format used by **Autodesk Platform Services (APS)** to stream 3D models in web applications.

The PropDb is a SQLite database (`.sdb` file) embedded in every translated SVF model — it contains **all metadata and properties** for every element in the model.

This library enables you to:

- **Read properties** from a local `.sdb` file or download them directly from APS
- **Discover** all categories and property names in a model
- **Query, filter, and stream** property values efficiently across large models
- **Merge inherited properties** from parent elements
- **Execute custom parameterized SQL** against the property database

---

## Quick Start

### Installation

```shell
dotnet add package SVF.PropDbReader
```

### From a Local `.sdb` File

```csharp
using SVF.PropDbReader;

using var reader = new PropDbReader(@"C:\path\to\properties.sdb");
var props = await reader.GetPropertiesForDbIdAsync(1);

foreach (var kvp in props)
    Console.WriteLine($"{kvp.Key}: {kvp.Value}");
```

### From Autodesk APS (Recommended)

```csharp
using SVF.PropDbReader;

using var reader = await PropDbReader.CreateAsync("<ACCESS_TOKEN>", "<MODEL_URN>");
var props = await reader.GetPropertiesForDbIdAsync(1);
```

### Streaming Large Models

```csharp
// Memory-efficient — one row at a time
await foreach (var (dbId, value) in reader.GetAllPropertyValuesStreamAsync("Dimensions", "Area"))
{
    Console.WriteLine($"dbId {dbId}: Area = {value}");
}
```

### Cancellation Support

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var props = await reader.GetPropertiesForDbIdAsync(1, cts.Token);
```

---

## Documentation

| Document | Description |
|---|---|
| [Getting Started](docs/getting-started.md) | Installation, setup, and quick start examples |
| [API Reference](docs/api-reference.md) | Full method signatures, parameters, and usage |
| [Architecture](docs/architecture.md) | Class diagrams, EAV schema, data flow, thread-safety design |
| [Migration Guide](docs/migration-guide.md) | Upgrading from v1.0.x to v1.1.0 |
| [Examples Notebook](examples/PropDbReader-Examples.ipynb) | Interactive .NET Jupyter notebook with all examples |

---

## Dependencies

| Package | Version | Purpose |
|---|---|---|
| [Microsoft.Data.Sqlite](https://www.nuget.org/packages/Microsoft.Data.Sqlite/) | 10.0.3 | SQLite database access |
| [Autodesk.ModelDerivative](https://www.nuget.org/packages/Autodesk.ModelDerivative/) | 2.2.0 | APS Model Derivative API client |
| [Autodesk.Authentication](https://www.nuget.org/packages/Autodesk.Authentication/) | 2.0.1 | APS Authentication SDK |

---

## Contributing

1. **Fork** the repository
2. **Create a branch** for your feature: `git checkout -b feature/my-new-method`
3. **Implement** your changes with XML doc comments
4. **Test**: `dotnet test`
5. **Submit** a pull request

### Reporting Issues

Open an issue at [GitHub Issues](https://github.com/Mohamed-Elnahla/SVF-PropDbReader/issues) with your .NET version, package version, and steps to reproduce.

---

## License

This project is licensed under the **Apache License 2.0**. See [LICENSE](LICENSE.txt) for details.

---

## References

- [Autodesk Platform Services (APS) Documentation](https://aps.autodesk.com/en/docs/)
- [APS Model Derivative API](https://aps.autodesk.com/en/docs/model-derivative/v2/developers_guide/overview/)
- [NuGet Package: SVF.PropDbReader](https://www.nuget.org/packages/SVF.PropDbReader)
