using Meshmakers.Octo.Runtime.Contracts.Geospatial.Geometry;
using MongoDB.Driver.GeoJsonObjectModel;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories;

internal static class PointExtensions
{
    public static GeoJsonPoint<GeoJsonCoordinates> ToGeoJsonPoint(this Point point)
    {
        return new GeoJsonPoint<GeoJsonCoordinates>(point.Coordinates.ToPosition<GeoJsonCoordinates>());
    }
}