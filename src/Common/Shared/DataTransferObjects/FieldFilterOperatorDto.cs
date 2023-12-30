namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public enum FieldFilterOperatorDto
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
    NotMatchRegEx = 10
}