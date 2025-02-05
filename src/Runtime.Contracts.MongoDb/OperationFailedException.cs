using System.Globalization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb;

/// <summary>
/// Used to indicate that an operation failed.
/// </summary>
[Serializable]
public class OperationFailedException : PersistenceException
{
    protected OperationFailedException()
    {
    }

    protected OperationFailedException(string message) : base(message)
    {
    }

    protected OperationFailedException(string message, Exception inner) : base(message, inner)
    {
    }

    public static Exception CreateWithMessage(string text)
    {
        return new OperationFailedException(text);
    }

    public static Exception FormulaCalculationFailed(object searchTerm)
    {
        return new OperationFailedException($"Formula '{searchTerm}' cannot be calculated.");
    }

    public static Exception FormulaEvaluationFailed(object searchTerm)
    {
        return new OperationFailedException($"Term '{searchTerm}' cannot be evaluated by formula.");
    }

    public static Exception AttributeNameResolutionFailed(string fieldFilterAttributeName)
    {
        return new OperationFailedException($"Attribute name '{fieldFilterAttributeName}' cannot be resolved.");
    }

    public static Exception AttributeDoesNotExist(string attributeName, string getEntityName)
    {
        return new OperationFailedException(
            $"Attribute '{attributeName}' does not exist on type '{getEntityName}'");
    }

    public static Exception NoFilterSet()
    {
        return new OperationFailedException("No filter set.");
    }

    public static Exception ValidationErrors()
    {
        return new OperationFailedException("Validation errors.");
    }

    public static Exception BulkImportError()
    {
        return new OperationFailedException("Bulk import error.");
    }
    
    public static Exception BulkImportError(Exception innerException)
    {
        return new OperationFailedException($"Bulk import failed: {innerException.Message}", innerException);
    }

    public static Exception CkTypeHasNoDefiningCollectionRoot(CkId<CkTypeId> ckTypeId)
    {
        return new OperationFailedException($"CkType '{ckTypeId}' has no defining collection root.");
    }

    public static Exception IndexTypeNotImplemented(IndexTypes indexIndexType)
    {
        return new OperationFailedException($"Index type '{indexIndexType}' not implemented.");
    }

    public static Exception DatabaseOperationFailed(string operationName, Exception exception)
    {
        return new OperationFailedException(
            $"Database operation '{operationName}' failed: {exception.Message}",
            exception);
    }

    public static Exception PagingNeeded()
    {
        return new OperationFailedException("'skip' without 'take' is not possible.");
    }

    public static Exception GraphDirectionUnsupported(GraphDirections graphDirection)
    {
        return new OperationFailedException($"Graph direction '{graphDirection}' is not supported.");
    }
    
    public static Exception UpdateAutoCompleteTextsFailed(CkId<CkTypeId> ckTypeId, string attributeName, Exception exception)
    {
        return new OperationFailedException(
            $"Update of autocomplete texts for attribute '{attributeName}' of CkType '{ckTypeId}' failed: {exception.Message}",
            exception);
    }

    public static Exception ModelImportingWaitTimeout()
    {
        return new OperationFailedException("Model importing wait timeout.");
    }

    public static Exception CkTypeIdUndefined()
    {
        return new OperationFailedException("CkTypeId is undefined.");
    }

    public static Exception AttributeNotFound<TKey>(CkId<CkAttributeId> modelAttributeId, string elementType, CkId<TKey> ckId)
        where TKey : IComparable<TKey>, ICkKey
    {
        return new OperationFailedException($"Attribute '{modelAttributeId}' does not exist at {elementType} '{ckId}'.");
    }

    public static Exception RecordNotFound<TKey>(CkId<CkRecordId> ckRecordId, string elementType,  CkId<TKey> ckId)
        where TKey : IComparable<TKey>, ICkKey
    {
        return new OperationFailedException($"Record '{ckRecordId}' does not exist at {elementType} '{ckId}'.");
    }

    public static Exception CkModelsMissing(string tenantId, ICollection<CkModelId> ckModelIds)
    {
        return new OperationFailedException($"Models '{string.Join(", ", ckModelIds)}' are missing in tenant '{tenantId}'.");
    }

    public static Exception AssociationRoleIdUndefined()
    {
        return new OperationFailedException("AssociationRoleId is undefined.");
    }

    public static Exception CkRecordIdNotDefined(CkTypeAttributeGraph ckTypeAttributeGraph)
    {
        return new OperationFailedException($"CkRecordId is not defined for attribute '{ckTypeAttributeGraph.AttributeName}'.");
    }

    public static Exception PathTypeNotSupported(PathTerm pathTerm)
    {
        return new OperationFailedException($"Path type '{pathTerm.Type}' is not supported.");
    }

    public static Exception CkEnumIdNotDefined(CkTypeAttributeGraph ckTypeAttributeGraph)
    {
        return new OperationFailedException($"CkEnumId is not defined for attribute '{ckTypeAttributeGraph.AttributeName}'.");
    }

    public static Exception CkEnumIdNotFound(CkTypeAttributeGraph typeAttributeGraph)
    {
        return new OperationFailedException($"CkEnumId '{typeAttributeGraph.ValueCkEnumId}' not found.");
    }

    public static Exception CkEnumWithOutOfRange(CkTypeAttributeGraph typeAttributeGraph, object value)
    {
        return new OperationFailedException($"Value '{value}' is out of range for CkEnum '{typeAttributeGraph.ValueCkEnumId}'.");
    }
}