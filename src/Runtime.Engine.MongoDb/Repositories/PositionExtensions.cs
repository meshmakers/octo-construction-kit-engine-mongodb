using Meshmakers.Octo.Runtime.Contracts.Geospatial.Geometry;
using MongoDB.Driver.GeoJsonObjectModel;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories;

internal static class PositionExtensions
{
    public static TCoordinates ToPosition<TCoordinates>(this IPosition position)
        where TCoordinates : GeoJsonCoordinates
    {
        if (position.Altitude.HasValue)
        {
            return (TCoordinates)(object)new GeoJson3DCoordinates(position.Longitude, position.Latitude, position.Altitude.Value);
        }
        return (TCoordinates)(object)new GeoJson2DCoordinates(position.Longitude, position.Latitude);
    }
}