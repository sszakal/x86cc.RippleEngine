using Xunit;

// Tests share one Postgres container (a database each, see SharedPostgres). They still run serially: the engine
// tests are timing-sensitive, and running their hosts concurrently against one server slows DB round trips
// enough to make them flaky.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
