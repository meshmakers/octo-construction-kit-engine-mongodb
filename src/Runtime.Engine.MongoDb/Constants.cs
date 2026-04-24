using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb;

public static class Constants
{

    public const string RegexMongoDbHost = @"^([a-zA-Z0-9_.-]+)(:[0-9]{1,5})?$";
    public const string RegexWithoutWhitespaces = @"^[^\s]+$";

    internal const string AttributesName = "attributes";
    internal const string AssociationName = "_associations";
    internal const string PathSeparator = ".";
    internal const string IndexAccessor = ".{0}";

    public const string IdField = "_id";
    public const string ModelIdField = "ckModelId";
    public const string IdIndexName = "_id_";

    internal const string OctoTextAnalyzer = "text_octo_";
    internal const string OctoTextAnalyzerEn = "text_octo_en";
    internal const string OctoTextAnalyzerDe = "text_octo_de";

    // ********************************************************************
    // Well-known association identifier
    // ********************************************************************
    public const string RelatedRoleId = "System.Related";

    // ********************************************************************
    // Well-known attribute names
    // ********************************************************************
    public const string QueryCkTypeIdAttribute = "QueryCkTypeId";
    public const string ServiceHookBaseUriAttribute = "ServiceHookBaseUri";
    public const string ServiceHookActionAttribute = "ServiceHookAction";
    public const string ServiceHookApiKeyAttribute = "ServiceHookApiKey";
    public const string FieldFilterAttribute = "FieldFilter";
    public const string EnabledAttribute = "Enabled";


    // ********************************************************************
    // BsonDocument attributes
    // ********************************************************************
    public const string ContentType = "contentType";
    public const string ExpiryDateTime = "expiryDateTime";
    public const string BinaryType = "binaryType";
    public const string RtEntityId = "rtEntityId";


    internal static readonly string[] TextAnalyzerFeatures = { "frequency", "norm", "position" };

    // ********************************************************************
    // MongoDB change-stream document field names
    // ********************************************************************

    /// <summary>
    /// Name of the post-image field in a MongoDB change-stream document
    /// (<c>ChangeStreamDocument.FullDocument</c>).
    /// </summary>
    public const string ChangeStreamFullDocument = "fullDocument";

    /// <summary>
    /// Name of the pre-image field in a MongoDB change-stream document
    /// (<c>ChangeStreamDocument.FullDocumentBeforeChange</c>). Only populated when the
    /// collection has <c>changeStreamPreAndPostImages: { enabled: true }</c>.
    /// </summary>
    public const string ChangeStreamFullDocumentBeforeChange = "fullDocumentBeforeChange";

    public static bool IsSystemAttribute(string f)
    {
        return (string.Compare(f, IdField, StringComparison.InvariantCultureIgnoreCase) == 0 ||
                string.Compare(f, nameof(RtEntity.RtCreationDateTime), StringComparison.InvariantCultureIgnoreCase) ==
                0 ||
                string.Compare(f, nameof(RtEntity.RtChangedDateTime), StringComparison.InvariantCultureIgnoreCase) ==
                0 ||
                string.Compare(f, nameof(RtEntity.CkTypeId), StringComparison.InvariantCultureIgnoreCase) == 0 ||
                string.Compare(f, nameof(RtEntity.RtVersion), StringComparison.InvariantCultureIgnoreCase) == 0 ||
                string.Compare(f, nameof(RtEntity.RtWellKnownName), StringComparison.InvariantCultureIgnoreCase) == 0 ||
                string.Compare(f, nameof(RtEntity.RtState), StringComparison.InvariantCultureIgnoreCase) == 0 ||
                string.Compare(f, nameof(RtEntity.RtArchivedDateTime), StringComparison.InvariantCultureIgnoreCase) ==
                0 ||
                string.Compare(f, nameof(RtAssociation.OriginCkTypeId), StringComparison.InvariantCultureIgnoreCase) ==
                0 ||
                string.Compare(f, nameof(RtAssociation.OriginRtId), StringComparison.InvariantCultureIgnoreCase) == 0 ||
                string.Compare(f, nameof(RtAssociation.TargetCkTypeId), StringComparison.InvariantCultureIgnoreCase) ==
                0 ||
                string.Compare(f, nameof(RtAssociation.TargetRtId), StringComparison.InvariantCultureIgnoreCase) == 0 ||
                string.Compare(f, nameof(RtAssociation.AssociationId), StringComparison.InvariantCultureIgnoreCase) ==
                0 ||
                string.Compare(f, nameof(RtAssociation.AssociationRoleId),
                    StringComparison.InvariantCultureIgnoreCase) == 0);
    }
}
