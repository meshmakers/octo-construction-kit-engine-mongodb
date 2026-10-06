using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories;

public class UpdateInfo<T> : IUpdateInfo<T> where T : class, new()
{
    public UpdateInfo(ChangeStreamDocument<T> changeStreamDocument)
        : this(changeStreamDocument, null)
    {
    }

    /// <summary>
    ///     AB#5533: the full document and the pre-image are handed to the collection's read normaliser
    ///     (legacy strings in Secret slots become <c>RtSecretValue.LegacyPlaintext</c>).
    /// </summary>
    internal UpdateInfo(ChangeStreamDocument<T> changeStreamDocument,
        MongoDb.IMongoDocumentReadNormalizer<T>? readNormalizer)
    {
        switch (changeStreamDocument.OperationType)
        {
            case ChangeStreamOperationType.Insert:
                UpdateType = UpdateTypes.Insert;
                break;
            case ChangeStreamOperationType.Update:
                UpdateType = UpdateTypes.Update;
                break;
            case ChangeStreamOperationType.Replace:
                UpdateType = UpdateTypes.Replace;
                break;
            case ChangeStreamOperationType.Delete:
                UpdateType = UpdateTypes.Delete;
                break;
            default:
                UpdateType = UpdateTypes.Undefined;
                break;
        }

        UpdateFields = changeStreamDocument.UpdateDescription?.UpdatedFields.Names.ToArray() ?? Array.Empty<string>();

        Document = changeStreamDocument.FullDocument;
        DocumentBeforeChange = changeStreamDocument.FullDocumentBeforeChange;

        if (readNormalizer != null)
        {
            if (Document != null)
            {
                readNormalizer.NormalizeAfterRead([Document]);
            }

            if (DocumentBeforeChange != null)
            {
                readNormalizer.NormalizeAfterRead([DocumentBeforeChange]);
            }
        }
    }

    public string[] UpdateFields { get; }

    public UpdateTypes UpdateType { get; }
    public T? Document { get; }
    public T? DocumentBeforeChange { get; }
}