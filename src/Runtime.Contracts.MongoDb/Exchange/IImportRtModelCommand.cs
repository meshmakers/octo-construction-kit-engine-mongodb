using Meshmakers.Octo.Runtime.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Exchange;

/// <summary>
///     Interface for importing a runtime model from a file.
/// </summary>
public interface IImportRtModelCommand
{
    /// <summary>
    ///     Imports as text
    /// </summary>
    /// <param name="tenantId">The tenant id</param>
    /// <param name="jsonText">Model as JSON text</param>
    /// <param name="cancellationToken">An optional cancellation token</param>
    /// <returns></returns>
    Task ImportTextAsync(string tenantId, string jsonText, CancellationToken? cancellationToken = null);

    /// <summary>
    ///     Imports a model root
    /// </summary>
    /// <param name="tenantId">The tenant id</param>
    /// <param name="rtModelRoot">The model root</param>
    /// <param name="cancellationToken">An optional cancellation token</param>
    /// <returns></returns>
    Task ImportModelAsync(string tenantId, RtModelRootDto rtModelRoot, CancellationToken? cancellationToken = null);

    /// <summary>
    ///     Imports from a file
    /// </summary>
    /// <param name="tenantId">The tenant id</param>
    /// <param name="filePath">A file path as ZIP (containing YAML or JSON), JSON or YAML files</param>
    /// <param name="contentType">The content type of the file</param>
    /// <param name="cancellationToken">An optional cancellation token</param>
    /// <returns></returns>
    Task ImportAsync(string tenantId, string filePath, string contentType, CancellationToken? cancellationToken = null);
}