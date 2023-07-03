using System;
using System.Linq;
using MongoDB.Driver;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

public class UpdateInfo<T> : IUpdateInfo<T> where T : class, new()
{
    public UpdateInfo(ChangeStreamDocument<T> changeStreamDocument)
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
    }

    public string[] UpdateFields { get; }

    public UpdateTypes UpdateType { get; }
    public T? Document { get; }
    public T? DocumentBeforeChange { get; }
}
