# Architecture

## Class Diagram

```mermaid
classDiagram
    class PropDbReader {
        -SqliteConnection _connection
        -string _dbPath
        -bool _deleteDbOnDispose
        -bool _disposed
        +PropDbReader(string dbPath, bool deleteDbOnDispose)
        +CreateAsync(string accessToken, string urn, CancellationToken)$ Task~PropDbReader~
        +DownloadAndGetPathAsync(string accessToken, string urn, CancellationToken)$ Task~string~
        +GetPropertiesForDbIdAsync(long dbId, CancellationToken) Task~Dictionary~
        +GetMergedPropertiesAsync(long dbId, CancellationToken) Task~Dictionary~
        +GetPropertyValueAsync(long dbId, string category, string displayName, CancellationToken) Task~object~
        +GetAllPropertyValuesAsync(string category, string displayName, CancellationToken) Task~Dictionary~
        +GetAllPropertyValuesStreamAsync(string category, string displayName, CancellationToken) IAsyncEnumerable
        +GetAllPropertyValuesListAsync(string category, string displayName, CancellationToken) Task~List~
        +GetAllPropertyValuesConcurrentAsync(string category, string displayName, CancellationToken) Task~ConcurrentDictionary~
        +GetAllPropertyValuesStreamToConcurrentAsync(string category, string displayName, ConcurrentDictionary dict, CancellationToken) Task
        +GetAllPropertiesAsync(CancellationToken) Task~Dictionary~
        +GetAllPropertiesStreamAsync(CancellationToken) IAsyncEnumerable
        +GetParentDbIdAsync(long dbId, CancellationToken) Task~long?~
        +FindDbIdsByPropertyAsync(string category, string displayName, object value, CancellationToken) Task~List~
        +GetAllCategoriesAsync(CancellationToken) Task~List~string~~
        +GetAllPropertyNamesAsync(CancellationToken) Task~List~string~~
        +GetCategoriesWithPropertiesAsync(CancellationToken) Task~Dictionary~
        +QueryAsync(string sql, Dictionary? params, CancellationToken) Task~List~
        +DeleteDbFile() bool
        +Dispose() void
        #Dispose(bool disposing) void
    }

    class DbDownloader {
        -static HttpClient SharedHttpClient$
        -string _accessToken
        -string _region
        -string _cacheDir
        -ModelDerivativeClient _modelDerivativeClient
        -bool _disposed
        +DbDownloader(string accessToken, string region)
        +DownloadPropertiesDatabaseAsync(string urn, CancellationToken) Task~string?~
        +SanitizeFilename(string urn)$ string
        +Dispose() void
        #Dispose(bool disposing) void
    }

    class ManifestHelper {
        -Manifest _manifest
        +ManifestHelper(Manifest manifest)
        +Search(string? guid, string? type, string? role) List~ManifestResources~
        +Traverse(Func callback) void
    }

    PropDbReader --> DbDownloader : uses
    DbDownloader --> ManifestHelper : uses
    PropDbReader ..|> IDisposable
    DbDownloader ..|> IDisposable
```

---

## Database Schema (EAV Model)

The property database uses an **Entity-Attribute-Value (EAV)** schema:

```mermaid
erDiagram
    _objects_id {
        int id PK "dbId - element identifier"
        string external_id "External/Revit element ID"
        int viewable_id "Viewable context ID"
    }

    _objects_attr {
        int id PK "Attribute identifier"
        string category "Property category"
        string display_name "Human-readable property name"
        int data_type "Value data type"
    }

    _objects_val {
        int id PK "Value identifier"
        string value "Stored property value"
    }

    _objects_eav {
        int entity_id FK "References _objects_id.id"
        int attribute_id FK "References _objects_attr.id"
        int value_id FK "References _objects_val.id"
    }

    _objects_id ||--o{ _objects_eav : "has properties"
    _objects_attr ||--o{ _objects_eav : "defines attribute"
    _objects_val ||--o{ _objects_eav : "stores value"
```

**Key concept:** The `dbId` is the numeric identifier for every element in the model. It corresponds to the same dbId used in the APS Viewer JavaScript API.

---

## Data Flow — Downloading from APS

```mermaid
sequenceDiagram
    participant App as Your Application
    participant Reader as PropDbReader
    participant Downloader as DbDownloader
    participant Helper as ManifestHelper
    participant APS as Autodesk APS API

    App->>Reader: CreateAsync(token, urn)
    Reader->>Downloader: DownloadPropertiesDatabaseAsync(urn)
    Downloader->>APS: GET manifest
    APS-->>Downloader: Manifest JSON
    Downloader->>Helper: Search(type, role)
    Helper-->>Downloader: Property DB derivative URN
    Downloader->>APS: GET signed cookies
    APS-->>Downloader: Cookies + download URL
    Downloader->>APS: GET .sdb file (streamed)
    APS-->>Downloader: Binary stream
    Downloader-->>Reader: Local .sdb file path
    Reader->>Reader: Open SQLite connection
    Reader-->>App: Ready PropDbReader
```

---

## Download Caching Flow

```mermaid
flowchart TD
    A[DownloadPropertiesDatabaseAsync called] --> B{Cache file exists and valid?}
    B -- Yes --> C[Return cached file path]
    B -- No --> D[Initialize Autodesk SDK]
    D --> E[Fetch model manifest]
    E --> F[Search for PropertyDatabase derivative]
    F --> G{Derivative found?}
    G -- No --> H[Return null]
    G -- Yes --> I[Request signed cookies from APS]
    I --> J[Download .sdb via signed URL]
    J --> K{File valid?}
    K -- No --> L[Delete temp file, return null]
    K -- Yes --> M[Move to cache directory]
    M --> C
```

---

## Thread Safety Design

In v1.1.0, the library was redesigned for improved safety:

- **No shared mutable state** — Each query method creates its own `SqliteCommand`, eliminating the previous thread-safety issue with shared commands.
- **Proper `Dispose(bool)`** — Both `PropDbReader` and `DbDownloader` implement the canonical dispose pattern with idempotency guards.
- **Static `HttpClient`** — `DbDownloader` uses a shared static `HttpClient` to prevent socket exhaustion.
- **`ConfigureAwait(false)`** — All async methods use `ConfigureAwait(false)` to avoid deadlocks when consumed from UI or ASP.NET synchronization contexts.

---

## Use Cases

```mermaid
mindmap
  root((SVF.PropDbReader))
    BIM Data Extraction
      Element metadata
      Custom parameters
      Category & type info
    Digital Twin Pipelines
      IoT dashboards
      Asset management
      Facility management
    Automated QA
      Property completeness
      Naming conventions
      Missing parameters
    Custom Reports
      CSV / JSON / Excel
      Property summaries
      Data audits
    Server-Side Lookup
      No browser required
      Backend queries
      API endpoints
    Data Sync
      SQL Server
      PostgreSQL
      External databases
```
