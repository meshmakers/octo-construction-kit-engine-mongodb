using Meshmakers.Octo.Runtime.Contracts.Geospatial.Geometry;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver.GeoJsonObjectModel;

namespace WebService.Sample.Controllers;

[ApiController]
[Route("api/[controller]")]
public class WeatherForecastController(ILogger<WeatherForecastController> logger, ISystemContext systemContext)
    : ControllerBase
{
    [HttpGet("GetRestaurants")]
    public async Task<IEnumerable<RtEntity>> Get()
    {
        var tenantRepository = await systemContext.FindTenantRepositoryAsync("meshTest");

        var session = tenantRepository.GetSession();
        try
        {
            session.StartTransaction();

            var point = new Point(new Position(40.7358879, -74.005));
            var dataQueryOperation = DataQueryOperation.Create();
            dataQueryOperation.NearGeospatialFilter("Location", point, 2000, 2100);

            var r = await tenantRepository.GetRtEntitiesByTypeAsync(session, "FireGuardians/Restaurant", dataQueryOperation);
            return r.Items;
        }
        catch (Exception e)
        {
            logger.LogError(e.Message);
            throw;
        }
    }
}