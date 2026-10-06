namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

/// <summary>
///     Optional capability of an <see cref="IMongoDataSourceMapper{TKey,TDocument}" />: documents read
///     through a <see cref="MongoDbDataSourceCollection{TKey,TDocument}" /> are handed to the mapper
///     before they leave the collection (AB#5533).
/// </summary>
/// <remarks>
///     The runtime-entity mapper uses it to turn legacy strings in <c>Secret</c> slots into
///     <c>RtSecretValue.LegacyPlaintext</c> on every collection read path (by id, by ids, find,
///     first-or-default, upsert-and-return, migration reads, change-stream documents), the same way
///     the RT query engine does for query results. Without it, an entity read by id and written back
///     would hand the stored <c>enc:v1</c> text to the engine write step as new plaintext input.
/// </remarks>
/// <typeparam name="TDocument">Document type of the collection</typeparam>
internal interface IMongoDocumentReadNormalizer<in TDocument>
{
    /// <summary>
    ///     Normalises the documents of one read operation in place.
    /// </summary>
    void NormalizeAfterRead(IEnumerable<TDocument> documents);
}
