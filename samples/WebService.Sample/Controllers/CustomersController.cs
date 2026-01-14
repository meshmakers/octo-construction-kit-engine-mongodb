using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

using Microsoft.AspNetCore.Mvc;

namespace WebService.Sample.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CustomersController(ILogger<CustomersController> logger, ISystemContext systemContext)
    : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<RtEntity>> Get()
    {
        try
        {
            await systemContext.EnsureSystemCkModelAsync();
            var tenantRepository = await systemContext.FindTenantRepositoryAsync("meshtest");

            var session = tenantRepository.GetSession();

            session.StartTransaction();

            // var point = new Point(new Position(40.7358879, -74.005));
            var queryOptions = RtEntityQueryOptions.Create();
            //   dataQueryOperation.NearGeospatialFilter("Location", point, 2000, 2100);

            var r = await tenantRepository.GetRtEntitiesByTypeAsync(session, "OctoSdkDemo/Customer", queryOptions);
            return r.Items;
        }
        catch (Exception e)
        {
            logger.LogError(e.Message);
            throw;
        }
    }
}
