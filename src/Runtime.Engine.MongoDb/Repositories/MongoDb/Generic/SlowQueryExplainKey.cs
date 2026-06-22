namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
/// Composite key the explain cache and the grouped-snapshot view share. Identical to the
/// tuple that <see cref="SlowQueriesBuffer.GetGroupedSnapshot"/> uses — semantically-identical
/// queries get one explain even when fired across many requests.
/// </summary>
/// <remarks>
/// Why composite and not just <see cref="Fingerprint"/>: the fingerprinter normalises primitive
/// command-body values, but the buffer entry's <see cref="CommandName"/> / <see cref="Target"/>
/// / <see cref="Database"/> are extracted independently and can legitimately differ for the
/// same fingerprint (e.g. <c>{find: "ck_types"}</c> and <c>{find: "rt_entities"}</c> both
/// normalise to <c>{find: "?"}</c>). Sharing a single explain across those would mis-attribute
/// the plan.
/// </remarks>
public readonly record struct SlowQueryExplainKey(
    string Fingerprint,
    string CommandName,
    string Target,
    string Database);
