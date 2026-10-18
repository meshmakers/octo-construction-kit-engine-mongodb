using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Engine.SemVer;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.UpgradeGuard;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.UnitTests.UpgradeGuard;

/// <summary>
///     Builders for the tenant upgrade guard tests (AB#6867): compiled models that look like what the compiler writes
///     (range-retaining models reference their dependencies major-qualified, classic ones by the pinned id) and a
///     read-only fake of the tenant's installed models.
/// </summary>
internal static class GuardModels
{
    /// <summary>The real checker, diff and classifier of the engine; the guard is tested against them, not against doubles.</summary>
    internal static TenantUpgradeGuard Guard() =>
        new(A.Fake<IDatabaseCkModelRepository>(), new CkModelDiffService(), new CkSemVerClassifier(),
            new CkSurfaceSatisfactionChecker(new CkSemVerClassifier()), NullLogger<TenantUpgradeGuard>.Instance);

    /// <summary>
    ///     A base model <c>Basic-x.y.z</c>: attribute <c>Code-1</c> (String) and type <c>Asset-1</c> declaring the
    ///     attribute <c>Code</c>.
    /// </summary>
    internal static CkCompiledModelRoot Basic(string version, bool withAsset = true, bool internalAsset = false,
        AttributeValueTypesDto codeType = AttributeValueTypesDto.String, string? extraAssetAttribute = null,
        bool codeOnAsset = true, string name = "Basic")
    {
        var id = new CkModelId(name, version);
        var attributes = new List<CkTypeAttributeDto>();
        if (codeOnAsset)
        {
            attributes.Add(Attr(id, "Code-1", "Code"));
        }

        if (extraAssetAttribute != null)
        {
            attributes.Add(Attr(id, extraAssetAttribute + "-1", extraAssetAttribute));
        }

        var model = new CkCompiledModelRoot
        {
            ModelId = id,
            Attributes =
            [
                new CkAttributeDto { AttributeId = new CkAttributeId("Code-1"), ValueType = codeType },
                new CkAttributeDto { AttributeId = new CkAttributeId("Extra-1"), ValueType = AttributeValueTypesDto.String }
            ],
            Types = []
        };
        if (withAsset)
        {
            model.Types.Add(new CkCompiledTypeDto
            {
                TypeId = new CkTypeId("Asset-1"),
                Attributes = attributes,
                Visibility = internalAsset ? CkVisibilityDto.Internal : null
            });
        }

        return model;
    }

    /// <summary>
    ///     An exact-pinned (classic) dependent of the models in <paramref name="pins" />, as the sbeg product model
    ///     <c>EnergyCommunity.Registration-2.8.0</c>: it references the pinned ids.
    /// </summary>
    internal static CkCompiledModelRoot Classic(string id, params string[] pins) => new()
    {
        ModelId = new CkModelId(id),
        Dependencies = pins.Select(p => new CkModelId(p)).ToList(),
        Types = []
    };

    /// <summary>
    ///     A range-retaining dependent of <c>Basic</c> (range <c>[2.4,3.0)</c>, floor 2.4.0): type <c>Reg-1</c>
    ///     derived from <c>Basic@2/Asset-1</c> that reuses <c>Basic@2/Code-1</c>.
    /// </summary>
    internal static CkCompiledModelRoot RangeDependent(string id, string[]? usedSurface = null,
        string? ownAttribute = null, string floor = "2.4.0", string range = "[2.4,3.0)", string baseName = "Basic")
    {
        var modelId = new CkModelId(id);
        var basic = CkModelId.MajorQualified(baseName, 2);
        var attributes = new List<CkTypeAttributeDto>
        {
            new()
            {
                CkAttributeId = new CkId<CkAttributeId>(basic, new CkAttributeId("Code-1")), AttributeName = "Code"
            }
        };
        if (ownAttribute != null)
        {
            attributes.Add(new CkTypeAttributeDto
            {
                CkAttributeId = new CkId<CkAttributeId>(modelId, new CkAttributeId(ownAttribute + "-1")),
                AttributeName = ownAttribute
            });
        }

        return new CkCompiledModelRoot
        {
            ModelId = modelId,
            Dependencies = [new CkModelId(baseName, "2.4.0")],
            DependencyRanges =
            [
                new CkModelDependencyDto
                {
                    Range = new CkModelIdVersionRange(baseName, range), Floor = floor,
                    UsedSurface = usedSurface?.ToList() ?? [baseName + "@2/Asset-1", baseName + "@2/Code-1"]
                }
            ],
            Types =
            [
                new CkCompiledTypeDto
                {
                    TypeId = new CkTypeId("Reg-1"),
                    DerivedFromCkTypeId = new CkId<CkTypeId>(basic, new CkTypeId("Asset-1")),
                    Attributes = attributes
                }
            ]
        };
    }

    /// <summary>The persisted row of a compiled model, as the tenant stores it.</summary>
    internal static CkModel Row(CkCompiledModelRoot model, ModelState state = ModelState.Available) => new()
    {
        Id = model.ModelId,
        ModelId = model.ModelId.Name,
        ModelState = state,
        Dependencies = model.Dependencies?.ToArray(),
        DependencyRanges = model.DependencyRanges?.Select(CkModelDependency.FromDto).ToArray()
    };

    private static CkTypeAttributeDto Attr(CkModelId model, string attributeId, string name) => new()
    {
        CkAttributeId = new CkId<CkAttributeId>(model, new CkAttributeId(attributeId)), AttributeName = name, IsOptional = true
    };
}

/// <summary>
///     A read-only fake of the tenant's installed models. It only counts reads: the reader interface has no write
///     member, so a write through it is not expressible.
/// </summary>
internal sealed class FakeInstalledModels : IInstalledCkModelReader
{
    private readonly List<CkModel> _rows = [];
    private readonly Dictionary<CkModelId, CkCompiledModelRoot> _roots = new();

    public int ListCalls { get; private set; }
    public int LoadCalls { get; private set; }

    /// <summary>Installs a model (its row; its compiled form is loadable unless <paramref name="loadable" /> is false).</summary>
    public FakeInstalledModels Install(CkCompiledModelRoot model, ModelState state = ModelState.Available,
        bool loadable = true)
    {
        _rows.Add(GuardModels.Row(model, state));
        if (loadable)
        {
            _roots[model.ModelId] = model;
        }

        return this;
    }

    public Task<IReadOnlyList<CkModel>> ListInstalledAsync(CancellationToken cancellationToken)
    {
        ListCalls++;
        return Task.FromResult<IReadOnlyList<CkModel>>(_rows.ToList());
    }

    public Task<CkCompiledModelRoot?> LoadAsync(CkModelId modelId, CancellationToken cancellationToken)
    {
        LoadCalls++;
        return Task.FromResult(_roots.GetValueOrDefault(modelId));
    }
}
