namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

internal interface IAdminRepositoryAccess
{
    IAdminRepositoryClient GetRepositoryClient(string databaseName); 
}