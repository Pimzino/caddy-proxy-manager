using Xunit;

// The end-to-end tests start two complete managers (real Kestrel listeners) and a real Caddy process for each on
// dynamically chosen loopback ports. Running them in parallel would let processes race for ports; run serially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
