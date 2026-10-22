// The coordinator owns the one database/output resource; explicit concurrency tests
// create their own simultaneous requests instead of racing unrelated test fixtures.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
