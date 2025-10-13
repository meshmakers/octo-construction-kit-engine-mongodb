using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison;

internal class TenantComparisonService(ISystemContext systemContext) : ITenantComparisonService
{
    public Task<TenantComparisonReport> CompareTenantAsync(string sourceTenantId, string targetTenantId, TenantComparisonOptions options,
        CancellationToken cancellationToken = default)
    {
        
        
        
        
        return Task.FromResult(new TenantComparisonReport());
    } 
    
    public Task<TenantComparisonReport> CompareTenantWithBackupAsync(string liveTenantId, string backupArchivePath, TenantComparisonOptions options,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new TenantComparisonReport());
    }

    public Task<TenantComparisonReport> CompareBackupsAsync(string sourceBackupPath, string targetBackupPath, TenantComparisonOptions options,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new TenantComparisonReport());
    }
}
