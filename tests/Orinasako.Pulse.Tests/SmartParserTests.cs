using Orinasako.Pulse.Contracts;
using Orinasako.Pulse.Core;

namespace Orinasako.Pulse.Tests;

public sealed class SmartParserTests
{
    [Fact]
    public void NvmeHealthyLogUsesStandardFieldsAndCorrectDataUnitScale()
    {
        var health = new byte[512];
        health[1] = 44; health[2] = 1; // 300 K
        health[3] = 90; health[4] = 10; health[5] = 8;
        health[32] = 1; health[48] = 2; health[112] = 3; health[128] = 4;
        var identify = new byte[4096];
        identify[266] = 0x4A; identify[267] = 1;
        identify[268] = 0x5E; identify[269] = 1;

        var parsed = SmartParsers.ParseNvmeHealthLog(health, identify);

        Assert.Equal(StorageHealth.Healthy, parsed.Health);
        Assert.Equal(27, parsed.TemperatureC);
        Assert.Equal(57, parsed.WarningTemperatureC);
        Assert.Equal(77, parsed.CriticalTemperatureC);
        Assert.Equal(8, parsed.WearUsedPercent);
        Assert.Equal(92, parsed.LifeRemainingPercent);
        Assert.Equal("512000", parsed.DataReadBytes); // NVMe data units are 1000 × 512 bytes.
        Assert.Equal("4", parsed.PowerOnHours);
        Assert.Equal("3", parsed.PowerCycleCount);
    }

    [Theory]
    [InlineData(0x01, StorageHealth.Critical)]
    [InlineData(0x04, StorageHealth.Critical)]
    [InlineData(0x08, StorageHealth.Critical)]
    [InlineData(0x02, StorageHealth.Degraded)]
    [InlineData(0x10, StorageHealth.Degraded)]
    public void NvmeCriticalWarningBitsAreNotCollapsedToHealthy(byte mask, StorageHealth expected)
    {
        var log = new byte[512];
        log[0] = mask;
        Assert.Equal(expected, SmartParsers.ParseNvmeHealthLog(log).Health);
    }

    [Fact]
    public void NvmeWearAtEnduranceEstimateIsDegradedButNotCritical()
    {
        var log = new byte[512];
        log[5] = 100;
        var parsed = SmartParsers.ParseNvmeHealthLog(log);
        Assert.Equal(StorageHealth.Degraded, parsed.Health);
        Assert.Equal(0, parsed.LifeRemainingPercent);
    }

    [Fact]
    public void NvmeInvalidTemperatureAndPercentagesStayUnavailable()
    {
        var log = new byte[512];
        log[1] = 0xFF; log[2] = 0xFF;
        log[3] = 255; log[4] = 255; log[5] = 255;
        var parsed = SmartParsers.ParseNvmeHealthLog(log);
        Assert.Null(parsed.TemperatureC);
        Assert.Null(parsed.AvailableSparePercent);
        Assert.Null(parsed.WearUsedPercent);
    }

    [Fact]
    public void AtaRawValuesArePreservedAndHealthIsUnknownWithoutStatusEvidence()
    {
        var data = new byte[512];
        data[2] = 5; data[3] = 1; data[4] = 0; data[5] = 80; data[6] = 70;
        data[7] = 0x34; data[8] = 0x12;
        var thresholds = new byte[512];
        thresholds[2] = 5; thresholds[3] = 10;

        var attributes = SmartParsers.ParseAtaAttributes(data, thresholds);
        var first = Assert.Single(attributes);

        Assert.Equal("005", first.Id);
        Assert.Equal("341200000000", first.RawValueHex.Value);
        Assert.Equal(80, first.NormalizedValue.Value);
        Assert.Equal(10, first.ThresholdValue.Value);
        Assert.Equal(StorageHealth.Unknown, SmartParsers.AssessAtaHealth(true, true, null, attributes));
        Assert.Equal(StorageHealth.Healthy, SmartParsers.AssessAtaHealth(true, true, false, attributes));
        Assert.Equal(StorageHealth.Critical, SmartParsers.AssessAtaHealth(true, true, true, attributes));
    }

    [Fact]
    public void AtaMissingOrDisabledSmartIsUnknownRatherThanHealthy()
    {
        Assert.Equal(StorageHealth.Unknown, SmartParsers.AssessAtaHealth(false, false, false, []));
        Assert.Equal(StorageHealth.Unknown, SmartParsers.AssessAtaHealth(true, false, false, []));
    }

    [Fact]
    public void MalformedProtocolBuffersAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => SmartParsers.ParseNvmeHealthLog(new byte[64]));
        Assert.Throws<InvalidDataException>(() => SmartParsers.ParseAtaAttributes(new byte[511]));
    }
}
