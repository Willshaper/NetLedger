using System.Globalization;
using NetLedger.Core;
using NetLedger.Core.Native;
using Xunit;

namespace NetLedger.Tests;

public class CounterAndFormatTests
{
    private static InterfaceSnapshot Nic(Guid guid, ulong received, ulong sent, ulong linkBits = 1_000_000_000) =>
        new(guid, "Ethernet", "Test adapter", IfTable.IfTypeEthernet, true, linkBits, received, sent);

    [Fact]
    public void FirstSampleOnlySetsBaseline()
    {
        var tracker = new CounterTracker();
        var output = new List<InterfaceDelta>();
        Guid id = Guid.NewGuid();

        tracker.Update([Nic(id, 1000, 500)], 1, output);
        Assert.Empty(output);

        tracker.Update([Nic(id, 1600, 700)], 1, output);
        InterfaceDelta delta = Assert.Single(output);
        Assert.Equal(600UL, delta.Received);
        Assert.Equal(200UL, delta.Sent);
    }

    [Fact]
    public void CounterResetCountsEverythingSinceReset() =>
        Assert.Equal(300UL, CounterTracker.Delta(previous: 50_000, current: 300, linkSpeedBitsPerSecond: 1_000_000_000, elapsedSeconds: 1));

    [Fact]
    public void ImpossibleJumpIsDiscarded() =>
        // 100 Mbit link, one second: 10 GB can't have gone through it.
        Assert.Equal(0UL, CounterTracker.Delta(0, 10_000_000_000, 100_000_000, 1));

    [Fact]
    public void ReturningAdapterGetsFreshBaseline()
    {
        var tracker = new CounterTracker();
        var output = new List<InterfaceDelta>();
        Guid id = Guid.NewGuid();
        tracker.Update([Nic(id, 1000, 0)], 1, output);
        tracker.Update([], 1, output);
        tracker.Update([Nic(id, 900_000, 0)], 1, output);
        Assert.Empty(output);
    }

    [Fact]
    public void SizesUseThreeSignificantDigits()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        ByteFormat.SizeBase = SizeBase.Binary;
        Assert.Equal("512 B", ByteFormat.Size(512));
        Assert.Equal("1.50 KB", ByteFormat.Size(1536));
        Assert.Equal("12.3 GB", ByteFormat.Size((long)(12.3 * 1024 * 1024 * 1024)));
        Assert.Equal("123 MB", ByteFormat.Size(123L * 1024 * 1024));

        ByteFormat.SizeBase = SizeBase.Decimal;
        Assert.Equal("1.00 TB", ByteFormat.Size(1_000_000_000_000));
        ByteFormat.SizeBase = SizeBase.Binary;
    }

    [Fact]
    public void RatesScaleLikeTheTaskbar()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Assert.Equal("0.0 Kbit", ByteFormat.Rate(0, RateUnit.AutoBits));
        Assert.Equal("423 Mbit", ByteFormat.Rate(423e6 / 8, RateUnit.AutoBits));
        Assert.Equal("1.2 Gbit", ByteFormat.Rate(1.2e9 / 8, RateUnit.AutoBits));
        Assert.Equal("0.5 Mbit", ByteFormat.Rate(0.5e6 / 8, RateUnit.Mbit));
        Assert.Equal("1.0 Mbit", ByteFormat.Rate(999_970.0 / 8, RateUnit.AutoBits)); // not "1000 Kbit"
    }
}
