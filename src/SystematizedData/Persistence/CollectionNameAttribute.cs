using System;

namespace Meshmakers.Octo.Backend.Persistence;

[AttributeUsage(AttributeTargets.Class)]
public class CollectionNameAttribute : Attribute
{
    public CollectionNameAttribute(string collectionName)
    {
        CollectionName = collectionName;
    }

    public string CollectionName { get; set; }
}
