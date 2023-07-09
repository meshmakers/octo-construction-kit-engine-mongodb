using System.Collections.Generic;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

public class FieldGroupBy
{
    public FieldGroupBy(IEnumerable<string> attributeNames, IEnumerable<string>? countAttributeNames,
        IEnumerable<string>? maxValueAttributeNames, IEnumerable<string>? minValueAttributeNames, IEnumerable<string>? avgAttributeNames)
    {
        AttributeNames = attributeNames;
        CountAttributeNames = countAttributeNames;
        MaxValueAttributeNames = maxValueAttributeNames;
        MinValueAttributeNames = minValueAttributeNames;
        AvgAttributeNames = avgAttributeNames;
    }

    
    public IEnumerable<string> AttributeNames { get;  }
    
    public IEnumerable<string>? CountAttributeNames { get; } 
    public IEnumerable<string>? MaxValueAttributeNames { get; } 
    public IEnumerable<string>? MinValueAttributeNames { get; } 
    public IEnumerable<string>? AvgAttributeNames { get; }

}