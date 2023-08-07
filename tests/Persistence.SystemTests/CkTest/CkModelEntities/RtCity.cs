using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using MongoDB.Bson.Serialization.Attributes;
using Newtonsoft.Json;

namespace Meshmakers.Octo.Backend.Persistence.SystemTests.CkModelEntities;

[CkId(TestCkModel.TestCkModelId, TestCkModel.CkIdCity)]
public class RtCity: RtLocation
{
  
}