using System;
using System.Runtime.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence;

[Serializable]
public class TenantException : Exception
{
    //
    // For guidelines regarding the creation of new exception types, see
    //    http://msdn.microsoft.com/library/default.asp?url=/library/en-us/cpgenref/html/cpconerrorraisinghandlingguidelines.asp
    // and
    //    http://msdn.microsoft.com/library/default.asp?url=/library/en-us/dncscol/html/csharp07192001.asp
    //

    public TenantException()
    {
    }

    public TenantException(string message) : base(message)
    {
    }

    public TenantException(string message, Exception inner) : base(message, inner)
    {
    }

    protected TenantException(
        SerializationInfo info,
        StreamingContext context) : base(info, context)
    {
    }

    internal static Exception SystemModelNotFound()
    {
        return new TenantException("System model not found.");
    }

    internal static Exception ErrorDuringSystemModelLoad(OperationResult operationResult)
    {
        return new TenantException($"Error loading system model.{Environment.NewLine}{operationResult.GetMessages()}");
    }

    public static Exception ModelNotFound(CkModelId ckModelId)
    {
        return new TenantException($"Model {ckModelId} not found by repository management.");
    }

    public static Exception ErrorDuringModelLoad(CkModelId ckModelId, OperationResult operationResult)
    {
        return new TenantException($"Error loading model {ckModelId}.{Environment.NewLine}{operationResult.GetMessages()}");
    }

    public static Exception TenantDoesAlreadyExist(string tenantId)
    {
        return new TenantException($"Tenant {tenantId} does already exist.");
    }

    public static Exception TenantDoesNotExist(string tenantId)
    {
        return new TenantException($"Tenant '{tenantId}' does not exist.");
    }
}
