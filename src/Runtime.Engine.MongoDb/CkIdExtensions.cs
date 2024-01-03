using System.Text.RegularExpressions;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb;

internal static class CkIdExtensions
{
    public static string GetCkTypeCollectionName<TKey>(this CkId<TKey> ckKey) where TKey : struct, IComparable<TKey>, ICkKey
    {
        var cleaned = Regex.Replace(ckKey.SemanticVersionedFullName, @"[^A-Za-z0-9]+", "");
        return cleaned;
    }
}