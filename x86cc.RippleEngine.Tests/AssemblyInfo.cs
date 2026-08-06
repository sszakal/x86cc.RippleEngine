using Xunit;

// Each test class spins up its own Postgres via Testcontainers. Running many containers in parallel
// overloads the local container runtime (Podman) and slows DB round trips enough to make timing-sensitive
// engine tests flaky. Run the suite serially so at most one container is live at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
