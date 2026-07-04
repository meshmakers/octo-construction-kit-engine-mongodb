# Blueprint Integration for MongoDB

This documentation describes the blueprint integration in the MongoDB Runtime Engine.

## Overview

Blueprints enable the initialization of tenants with predefined configuration:
- **CK Models**: Construction Kit models are automatically imported
- **Seed Data**: Initial entities can be loaded from YAML files
- **History**: All blueprint applications are tracked
- **Updates**: Existing tenants can be updated to new blueprint versions

## Configuration

### DI Registration

```csharp
services.AddRuntimeEngine()
    .AddMongoDbRuntimeRepository()
    .AddMongoBlueprintSupport();  // MongoDB-specific blueprint services
```

The method `AddMongoBlueprintSupport()` registers:
- `ITenantBlueprintHistory` → `MongoTenantBlueprintHistory`

## Creating a Tenant with Blueprint

### Basic Usage

```csharp
public class TenantService
{
    private readonly ISystemContext _systemContext;

    public TenantService(ISystemContext systemContext)
    {
        _systemContext = systemContext;
    }

    public async Task<BlueprintApplicationResult?> CreateTenantAsync(
        string tenantId,
        string databaseName,
        BlueprintId? blueprintId)
    {
        using var session = await _systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        var result = await _systemContext.CreateChildTenantAsync(
            session,
            databaseName,
            tenantId,
            blueprintId);

        await session.CommitTransactionAsync();
        return result;
    }
}
```

### Examples

```csharp
// Tenant without blueprint
await CreateTenantAsync("customer-123", "customer_123_db", null);

// Tenant with blueprint (specific version)
var blueprintId = new BlueprintId("InfrastructureStarter", "1.0.0");
await CreateTenantAsync("customer-456", "customer_456_db", blueprintId);

// Tenant with blueprint (from string)
BlueprintId blueprintId = "InfrastructureStarter-2.0.0";
await CreateTenantAsync("customer-789", "customer_789_db", blueprintId);
```

### Error Handling

On blueprint errors, the tenant is automatically rolled back:

```csharp
try
{
    var result = await _systemContext.CreateChildTenantAsync(
        session, databaseName, tenantId, blueprintId);

    if (result != null && !result.IsSuccess)
    {
        // Blueprint was applied but with warnings
        foreach (var message in result.OperationResult.Messages)
        {
            _logger.LogWarning("Blueprint warning: {Message}", message.Text);
        }
    }
}
catch (InvalidOperationException ex)
{
    // Blueprint application failed, tenant was deleted
    _logger.LogError(ex, "Failed to apply blueprint");
}
```

## Listing and Searching Blueprints

### IBlueprintCatalogManager

The `IBlueprintCatalogManager` provides access to all registered blueprint catalogs.

```csharp
public class BlueprintCatalogService
{
    private readonly IBlueprintCatalogManager _catalogManager;

    public BlueprintCatalogService(IBlueprintCatalogManager catalogManager)
    {
        _catalogManager = catalogManager;
    }

    /// <summary>
    /// Lists all available blueprints
    /// </summary>
    public async Task<BlueprintListResult> ListBlueprintsAsync(
        int skip = 0,
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        return await _catalogManager.ListAsync(skip, take,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Searches for blueprints
    /// </summary>
    public async Task<BlueprintSearchResult> SearchBlueprintsAsync(
        string searchTerm,
        int skip = 0,
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        return await _catalogManager.SearchAsync(searchTerm, skip, take,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Lists all available catalogs
    /// </summary>
    public IEnumerable<(string Name, string Description)> GetCatalogs()
    {
        return _catalogManager.GetCatalogList()
            .Select(t => (t.Item1, t.Item2));
    }

    /// <summary>
    /// Checks if a blueprint exists
    /// </summary>
    public async Task<bool> ExistsAsync(BlueprintId blueprintId)
    {
        return await _catalogManager.IsExistingAsync(blueprintId);
    }

    /// <summary>
    /// Loads blueprint details
    /// </summary>
    public async Task<BlueprintMetaRootDto?> GetBlueprintAsync(
        BlueprintId blueprintId,
        CancellationToken cancellationToken = default)
    {
        var operationResult = new OperationResult();
        return await _catalogManager.TryGetAsync(blueprintId, operationResult,
            cancellationToken: cancellationToken);
    }
}
```

### Return Structures

#### BlueprintListResult

```csharp
public class BlueprintListResult
{
    // List of found blueprints
    public List<BlueprintCatalogResultItem> Items { get; init; }

    // Total count for pagination
    public int TotalCount { get; init; }
}
```

#### BlueprintCatalogResultItem

```csharp
public class BlueprintCatalogResultItem
{
    // Versioned Blueprint ID (e.g., "MyBlueprint-1.0.0")
    public BlueprintId BlueprintId { get; set; }

    // Optional description
    public string? Description { get; set; }

    // Name of the catalog (e.g., "Local", "GitHub")
    public string CatalogName { get; init; }
}
```

### API Controller Example

```csharp
[ApiController]
[Route("api/[controller]")]
public class BlueprintsController : ControllerBase
{
    private readonly IBlueprintCatalogManager _catalogManager;

    public BlueprintsController(IBlueprintCatalogManager catalogManager)
    {
        _catalogManager = catalogManager;
    }

    /// <summary>
    /// Lists all available blueprints
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<BlueprintListResponse>> GetBlueprints(
        [FromQuery] int skip = 0,
        [FromQuery] int take = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await _catalogManager.ListAsync(skip, take,
            cancellationToken: cancellationToken);

        return Ok(new BlueprintListResponse
        {
            Items = result.Items.Select(i => new BlueprintDto
            {
                Id = i.BlueprintId.FullName,
                Name = i.BlueprintId.Name,
                Version = i.BlueprintId.Version.ToString(),
                Description = i.Description,
                Catalog = i.CatalogName
            }).ToList(),
            TotalCount = result.TotalCount,
            Skip = skip,
            Take = take
        });
    }

    /// <summary>
    /// Searches for blueprints
    /// </summary>
    [HttpGet("search")]
    public async Task<ActionResult<BlueprintListResponse>> SearchBlueprints(
        [FromQuery] string q,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 20,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return BadRequest("Search term is required");
        }

        var result = await _catalogManager.SearchAsync(q, skip, take,
            cancellationToken: cancellationToken);

        return Ok(new BlueprintListResponse
        {
            Items = result.Items.Select(i => new BlueprintDto
            {
                Id = i.BlueprintId.FullName,
                Name = i.BlueprintId.Name,
                Version = i.BlueprintId.Version.ToString(),
                Description = i.Description,
                Catalog = i.CatalogName
            }).ToList(),
            TotalCount = result.TotalCount,
            Skip = skip,
            Take = take
        });
    }

    /// <summary>
    /// Lists available catalogs
    /// </summary>
    [HttpGet("catalogs")]
    public ActionResult<IEnumerable<CatalogDto>> GetCatalogs()
    {
        var catalogs = _catalogManager.GetCatalogList()
            .Select(t => new CatalogDto
            {
                Name = t.Item1,
                Description = t.Item2
            });

        return Ok(catalogs);
    }

    /// <summary>
    /// Checks if a blueprint exists
    /// </summary>
    [HttpHead("{blueprintId}")]
    public async Task<IActionResult> BlueprintExists(string blueprintId)
    {
        var id = new BlueprintId(blueprintId);
        var exists = await _catalogManager.IsExistingAsync(id);

        return exists ? Ok() : NotFound();
    }
}

// DTOs for the API
public class BlueprintListResponse
{
    public List<BlueprintDto> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; }
}

public class BlueprintDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Description { get; set; }
    public string Catalog { get; set; } = "";
}

public class CatalogDto
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
}
```

## Querying Blueprint History

### ITenantBlueprintHistory

```csharp
public class TenantBlueprintService
{
    private readonly ITenantBlueprintHistory _history;

    public TenantBlueprintService(ITenantBlueprintHistory history)
    {
        _history = history;
    }

    /// <summary>
    /// Checks if a tenant uses a blueprint
    /// </summary>
    public async Task<bool> HasBlueprintAsync(string tenantId)
    {
        return await _history.HasBlueprintAsync(tenantId);
    }

    /// <summary>
    /// Returns the current blueprint of a tenant
    /// </summary>
    public async Task<TenantBlueprintInfo?> GetCurrentBlueprintAsync(string tenantId)
    {
        return await _history.GetCurrentAsync(tenantId);
    }

    /// <summary>
    /// Returns the complete blueprint history of a tenant
    /// </summary>
    public async Task<IReadOnlyList<TenantBlueprintInfo>> GetHistoryAsync(string tenantId)
    {
        return await _history.GetHistoryAsync(tenantId);
    }
}
```

### TenantBlueprintInfo

```csharp
public class TenantBlueprintInfo
{
    // Applied blueprint
    public BlueprintId BlueprintId { get; set; }

    // Time of application
    public DateTime AppliedAt { get; set; }

    // Type of application (Initial, Update, Migration)
    public BlueprintApplicationMode ApplicationMode { get; set; }

    // Previous version (for updates)
    public BlueprintId? PreviousVersion { get; set; }

    // Statistics
    public int EntitiesCreated { get; set; }
    public int EntitiesUpdated { get; set; }
    public int EntitiesDeleted { get; set; }

    // Checksum of seed data
    public string? SeedDataChecksum { get; set; }
}
```

## Catalog Types

### Local Catalog

Blueprints from the local file system:

```
blueprints/
├── MyBlueprint-1.0.0/
│   ├── blueprint.yaml
│   ├── ck-models/
│   └── seed-data/
└── AnotherBlueprint-2.0.0/
    └── ...
```

### GitHub Catalog (if configured)

Blueprints from GitHub repositories.

## See Also

- [Blueprint Documentation (Engine)](../../octo-construction-kit-engine/docs/blueprints.md) - Detailed blueprint specification
- [ITenantContext](../src/Runtime.Contracts.MongoDb/ITenantContext.cs) - Tenant management interface
- [IBlueprintCatalogManager](../../octo-construction-kit-engine/src/ConstructionKit.Engine/BlueprintCatalogs/IBlueprintCatalogManager.cs) - Catalog manager interface
