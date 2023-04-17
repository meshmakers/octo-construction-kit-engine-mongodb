using org.mariuszgromada.math.mxparser;

namespace Meshmakers.Octo.Backend.Persistence.Formulas;

public class OctoExpression : Expression
{
    private readonly Function _nowFunction = new("now", new NowFunction());

    private readonly Constant _nullFunction = new("null", double.NegativeInfinity);
    private readonly Function _startOfDayFunction = new("startOfDay", new StartOfDayFunction());

    public OctoExpression(string expressionString) : base(expressionString)
    {
        addDefinitions(_startOfDayFunction);
        addDefinitions(_nowFunction);
        addDefinitions(_nullFunction);
    }
}
