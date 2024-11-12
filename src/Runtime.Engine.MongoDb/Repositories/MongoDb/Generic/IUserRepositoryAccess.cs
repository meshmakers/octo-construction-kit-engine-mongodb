namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

internal interface IUserRepositoryAccess
{
    IRepositoryClient GetRepositoryClient(string databaseName);
}