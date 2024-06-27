namespace Meshmakers.Octo.Runtime.Engine.MongoDb;

public static class Constants
{
    public const string RegexMongoDbHost = @"^([a-zA-Z0-9_.-]+)(:[0-9]{1,5})?$";
    public const string RegexWithoutWhitespaces = @"^[^\s]+$";

    internal const string AttributesName = "attributes";

    public const string IdField = "_id";
    public const string ModelIdField = "ckModelId";

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


    internal static readonly string[] TextAnalyzerFeatures = { "frequency", "norm", "position" };
}