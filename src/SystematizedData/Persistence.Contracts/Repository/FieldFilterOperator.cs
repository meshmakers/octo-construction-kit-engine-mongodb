namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

public enum FieldFilterOperator
{
    Equals = 0,
    NotEquals = 1,
    LessThan = 2,
    LessEqualThan = 3,
    GreaterThan = 4,
    GreaterEqualThan = 5,
    In = 6,
    NotIn = 7,
    Like = 8,
    MatchRegEx = 9,
   
    /// <summary>
    /// Arrays: Any element equals
    /// </summary>
    AnyEq = 10
}
