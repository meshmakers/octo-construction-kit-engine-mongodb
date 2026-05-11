using System.Collections.Generic;
using System.Text;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Composable description of a CrateDB column type, used by <see cref="ArchiveDdlGenerator"/> to
/// emit DDL for archive columns. Models the four shapes supported by archive columns:
/// scalar primitive, scalar array, strict object (record), and array of strict objects (record
/// array). Nested compositions are explicit, not implied.
/// </summary>
internal abstract record CrateColumnType
{
    /// <summary>
    /// Renders the type to its CrateDB DDL form (e.g. <c>DOUBLE PRECISION</c>,
    /// <c>ARRAY(TEXT)</c>, <c>OBJECT(STRICT) AS ("a" DOUBLE, "b" TEXT)</c>).
    /// </summary>
    public abstract void AppendTo(StringBuilder sb);

    /// <summary>
    /// Convenience for tests / logging.
    /// </summary>
    public string Render()
    {
        var sb = new StringBuilder();
        AppendTo(sb);
        return sb.ToString();
    }

    /// <summary>
    /// A primitive CrateDB type (TEXT, INTEGER, BIGINT, DOUBLE PRECISION, BOOLEAN,
    /// TIMESTAMP WITH TIME ZONE, GEO_POINT, …).
    /// </summary>
    public sealed record Primitive(string CrateTypeName) : CrateColumnType
    {
        public override void AppendTo(StringBuilder sb) => sb.Append(CrateTypeName);
    }

    /// <summary>
    /// An array of <see cref="Element"/>. Element nullability is preserved per concept §6 D8
    /// (array elements may be null even when the column itself is required).
    /// </summary>
    public sealed record Array(CrateColumnType Element) : CrateColumnType
    {
        public override void AppendTo(StringBuilder sb)
        {
            sb.Append("ARRAY(");
            Element.AppendTo(sb);
            sb.Append(')');
        }
    }

    /// <summary>
    /// A strict CrateDB OBJECT with declared subfields. Strict objects reject keys not declared
    /// here, which is what we want for archive-stored records.
    /// </summary>
    public sealed record StrictObject(IReadOnlyList<RecordField> Fields) : CrateColumnType
    {
        public override void AppendTo(StringBuilder sb)
        {
            sb.Append("OBJECT(STRICT) AS (");
            for (var i = 0; i < Fields.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append('"').Append(Fields[i].Name).Append("\" ");
                Fields[i].Type.AppendTo(sb);
            }
            sb.Append(')');
        }
    }
}

/// <summary>
/// Single subfield inside a <see cref="CrateColumnType.StrictObject"/>.
/// </summary>
internal sealed record RecordField(string Name, CrateColumnType Type);

/// <summary>
/// Maps <see cref="AttributeValueTypesDto"/> to its CrateDB primitive equivalent. Unsupported
/// types throw — they don't have a sensible representation in the time-series store.
/// </summary>
internal static class CrateTypeMapper
{
    public static CrateColumnType.Primitive ToCratePrimitive(AttributeValueTypesDto type) => type switch
    {
        AttributeValueTypesDto.Boolean => new CrateColumnType.Primitive("BOOLEAN"),
        AttributeValueTypesDto.Integer => new CrateColumnType.Primitive("INTEGER"),
        AttributeValueTypesDto.Integer64 => new CrateColumnType.Primitive("BIGINT"),
        AttributeValueTypesDto.Enum => new CrateColumnType.Primitive("INTEGER"),
        AttributeValueTypesDto.Double => new CrateColumnType.Primitive("DOUBLE PRECISION"),
        AttributeValueTypesDto.String => new CrateColumnType.Primitive("TEXT"),
        AttributeValueTypesDto.DateTime => new CrateColumnType.Primitive("TIMESTAMP WITH TIME ZONE"),
        AttributeValueTypesDto.DateTimeOffset => new CrateColumnType.Primitive("TIMESTAMP WITH TIME ZONE"),
        AttributeValueTypesDto.TimeSpan => new CrateColumnType.Primitive("BIGINT"),
        AttributeValueTypesDto.GeospatialPoint => new CrateColumnType.Primitive("GEO_POINT"),
        _ => throw new ArgumentException(
            $"Attribute value type '{type}' has no CrateDB primitive mapping for archive columns.",
            nameof(type)),
    };
}
