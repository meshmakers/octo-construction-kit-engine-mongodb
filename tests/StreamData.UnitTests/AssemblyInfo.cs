using Xunit;

// TenantSchema carries process-wide, set-once instance-prefix state (AB#4946). The tests that
// exercise SetInstancePrefix mutate it (and reset it in Dispose), so test collections must not
// run in parallel — a concurrent collection calling TenantSchema.SchemaName during that window
// would observe the test's prefix. The assembly is pure-logic and fast; serializing it costs
// almost nothing.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
