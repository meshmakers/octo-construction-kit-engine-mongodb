namespace Meshmakers.Octo.SystematizedData.Persistence;

public static class Constants
{
    public const string RegexMongoDbHost = @"^([a-zA-Z0-9_.-]+)(:[0-9]{1,5})?$";
    public const string RegexWithoutWhitespaces = @"^[^\s]+$";

    internal const string AttributesName = "attributes";

    internal const string SystemSchemaVersion = "SystemSchemaVersion";

    internal const int SystemSchemaVersionValue = 1;

    public const string IdField = "_id";

    internal const string OctoTextAnalyzer = "text_octo_";
    internal const string OctoTextAnalyzerEn = "text_octo_en";
    internal const string OctoTextAnalyzerDe = "text_octo_de";

    // ********************************************************************
    // Well-known construction kit identifier
    // ********************************************************************
    public const string SystemServiceHookCkId = "System.ServiceHook";
    public const string SystemNotificationMessageCkId = "System.Notification.Message";
    public const string SystemAutoIncrementCkId = "System.AutoIncrement";
    public const string SystemQueryCkId = "System.Query";
    public const string SystemNotificationTemplate = "System.NotificationTemplate";


    // ********************************************************************
    // Well-known association identifier
    // ********************************************************************
    public const string RelatedRoleId = "System.Related";

    // ********************************************************************
    // Well-known attribute names
    // ********************************************************************
    public const string QueryCkIdAttribute = "QueryCkId";
    public const string ServiceHookBaseUriAttribute = "ServiceHookBaseUri";
    public const string ServiceHookActionAttribute = "ServiceHookAction";
    public const string ServiceHookApiKeyAttribute = "ServiceHookApiKey";
    public const string FieldFilterAttribute = "FieldFilter";
    public const string EnabledAttribute = "Enabled";


    // ********************************************************************
    // BsonDocument attributes
    // ********************************************************************
    public const string ContentType = "contentType";

    internal static readonly string[] KnownAnalyzerLanguages =
        { "de", "en", "es", "fi", "fr", "it", "nl", "no", "pt", "ru", "sv", "zh" };

    internal static readonly string[] TextAnalyzerFeatures = { "frequency", "norm", "position" };
    internal static readonly string[] SystemReservedAttributeNames = { "CkId", "RtId", "RtWellKnownName" };
}
