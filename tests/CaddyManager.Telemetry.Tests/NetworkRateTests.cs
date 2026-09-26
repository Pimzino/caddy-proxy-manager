using CaddyManager.Telemetry.Resources;

namespace CaddyManager.Telemetry.Tests;

/// <summary>
/// Isolated tests of the network rate calculation (ResourceSampler.NetworkDelta): interfaces cannot be brought up and down
/// from a test, so the per-interface cumulative counters are given directly.
///
/// Ways it could fail (written before the code):
///  1. An interface that comes up with non-zero counters (link regained, VPN connected, Hyper-V vEthernet re-created)
///     adds its whole since-boot count as traffic of one interval: a huge spike (TEL-5).
///  2. An interface that goes away makes the sum drop, so the whole interval reports 0 although the others had traffic.
///  3. A counter that goes backwards (reset, 32-bit wrap) yields a negative rate or hides the other interfaces' traffic.
///  4. The first sample (no previous reading) or a zero interval divides by zero or reports a rate.
///  5. The rate is not per second (divided by the wrong interval).
/// </summary>
public sealed class NetworkRateTests
{
    private static Dictionary<string, (long Rx, long Tx)> Nics(params (string Id, long Rx, long Tx)[] nics) =>
        nics.ToDictionary(n => n.Id, n => (n.Rx, n.Tx));

    [Fact]
    public void InterfaceComingUpContributesNothingInItsFirstInterval()
    {
        var before = Nics(("eth", 1_000_000, 500_000));
        var after = Nics(("eth", 1_004_000, 502_000), ("vpn", 9_000_000_000, 7_000_000_000));
        Assert.Equal((2000.0, 1000.0), ResourceSampler.NetworkDelta(before, after, 2));
        var later = Nics(("eth", 1_006_000, 503_000), ("vpn", 9_000_010_000, 7_000_002_000));
        Assert.Equal((6000.0, 1500.0), ResourceSampler.NetworkDelta(after, later, 2));
    }

    [Fact]
    public void InterfaceGoingAwayDoesNotHideTheOthers()
    {
        var before = Nics(("eth", 1_000_000, 500_000), ("vpn", 9_000_000, 7_000_000));
        var after = Nics(("eth", 1_010_000, 504_000));
        Assert.Equal((5000.0, 2000.0), ResourceSampler.NetworkDelta(before, after, 2));
    }

    [Fact]
    public void CounterGoingBackwardsCountsZeroForThatInterfaceOnly()
    {
        var before = Nics(("eth", 1_000_000, 500_000), ("wifi", 4_000_000_000, 100));
        var after = Nics(("eth", 1_002_000, 501_000), ("wifi", 50, 200));
        Assert.Equal((1000.0, 550.0), ResourceSampler.NetworkDelta(before, after, 2));
    }

    [Fact]
    public void FirstSampleAndZeroIntervalReportZero()
    {
        var now = Nics(("eth", 1_000_000, 500_000));
        Assert.Equal((0.0, 0.0), ResourceSampler.NetworkDelta(null, now, 2));
        Assert.Equal((0.0, 0.0), ResourceSampler.NetworkDelta(now, Nics(("eth", 2_000_000, 600_000)), 0));
    }
}
