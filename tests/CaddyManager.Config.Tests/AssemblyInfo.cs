using Xunit;

// Live tests start real Caddy processes on dynamically chosen ports; running test classes in parallel let two
// processes race for the same port (seen on the Windows CI runner). The suite is fast enough to run serially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
