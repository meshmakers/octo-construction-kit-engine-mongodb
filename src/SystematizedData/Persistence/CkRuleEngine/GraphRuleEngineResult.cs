using System.Collections.Generic;
using Meshmakers.Octo.Backend.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.Backend.Persistence.CkRuleEngine;

public class GraphRuleEngineResult
{
    public GraphRuleEngineResult()
    {
        RtAssociationsToCreate = new List<RtAssociation>();
        RtAssociationsToDelete = new List<RtAssociation>();
    }

    public List<RtAssociation> RtAssociationsToCreate { get; }
    public List<RtAssociation> RtAssociationsToDelete { get; }
}
