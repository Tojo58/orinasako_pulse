using System.Buffers.Binary;
using System.Numerics;
using Orinasako.Pulse.Contracts;

namespace Orinasako.Pulse.Core;

public sealed record ParsedSmartData(
    StorageHealth Health,
    string? HealthBasis,
    int? TemperatureC,
    int? WarningTemperatureC,
    int? CriticalTemperatureC,
    int? WearUsedPercent,
    int? LifeRemainingPercent,
    int? AvailableSparePercent,
    int? AvailableSpareThresholdPercent,
    string? PowerOnHours,
    string? PowerCycleCount,
    string? MediaErrors,
    string? ErrorLogEntries,
    string? DataReadBytes,
    string? DataWrittenBytes,
    IReadOnlyList<StorageSmartAttribute> Attributes);

public static class SmartParsers
{
    public const int SectorSize = 512;
    private static readonly IReadOnlyDictionary<int, string> AtaNames = new Dictionary<int, string>
    {
        [1] = "Raw read error rate (vendor-defined)", [5] = "Reallocated sectors (raw encoding may be vendor-defined)",
        [9] = "Power-on hours (raw encoding may be vendor-defined)", [12] = "Power cycles (raw encoding may be vendor-defined)",
        [187] = "Reported uncorrectable errors", [188] = "Command timeouts (vendor-defined)",
        [190] = "Temperature (raw encoding may be vendor-defined)", [194] = "Temperature (raw encoding may be vendor-defined)",
        [196] = "Reallocation events (vendor-defined)", [197] = "Pending sectors", [198] = "Offline uncorrectable sectors",
        [199] = "Interface CRC errors (vendor-defined)", [231] = "SSD wear (vendor-defined)", [233] = "SSD wear (vendor-defined)",
        [241] = "Host writes (vendor-defined units)", [242] = "Host reads (vendor-defined units)",
    };

    public static ParsedSmartData ParseNvmeHealthLog(ReadOnlySpan<byte> log, ReadOnlySpan<byte> identifyController = default)
    {
        if (log.Length < SectorSize) throw new InvalidDataException("NVMe health log must contain at least 512 bytes.");
        var warning = log[0];
        var temperature = KelvinToCelsius(BinaryPrimitives.ReadUInt16LittleEndian(log[1..3]));
        var spare = ValidPercent(log[3]);
        var spareThreshold = ValidPercent(log[4]);
        var wear = log[5] <= 100 ? (int?)log[5] : null;
        var warningTemperature = identifyController.Length >= 270
            ? KelvinToCelsius(BinaryPrimitives.ReadUInt16LittleEndian(identifyController[266..268])) : null;
        var criticalTemperature = identifyController.Length >= 270
            ? KelvinToCelsius(BinaryPrimitives.ReadUInt16LittleEndian(identifyController[268..270])) : null;
        var cycles = UInt128Decimal(log[112..128]);
        var hours = UInt128Decimal(log[128..144]);
        var mediaErrors = UInt128Decimal(log[160..176]);
        var errorEntries = UInt128Decimal(log[176..192]);
        var readBytes = UInt128Decimal(log[32..48], 512_000);
        var writtenBytes = UInt128Decimal(log[48..64], 512_000);

        var criticalFlag = (warning & ((1 << 0) | (1 << 2) | (1 << 3))) != 0;
        var warningFlag = (warning & ((1 << 1) | (1 << 4))) != 0;
        var spareFailed = spare is not null && spareThreshold is > 0 && spare < spareThreshold;
        var criticalThermal = temperature is not null && criticalTemperature is not null && temperature >= criticalTemperature;
        var warningThermal = temperature is not null && warningTemperature is not null && temperature >= warningTemperature;
        var hasMediaErrors = !string.Equals(mediaErrors, "0", StringComparison.Ordinal);
        var health = criticalFlag || spareFailed || criticalThermal ? StorageHealth.Critical
            : warningFlag || warningThermal || wear >= 100 || hasMediaErrors ? StorageHealth.Degraded
            : StorageHealth.Healthy;
        var basis = criticalFlag ? $"NVMe critical-warning mask 0x{warning:X2}."
            : spareFailed ? "Available spare is below the controller-reported threshold."
            : criticalThermal ? "Composite temperature reached the controller-reported critical threshold."
            : warningFlag ? $"NVMe warning flag is set (mask 0x{warning:X2})."
            : warningThermal ? "Composite temperature reached the controller-reported warning threshold."
            : wear >= 100 ? "Controller-estimated endurance usage reached 100%; this alone does not prove drive failure."
            : hasMediaErrors ? "The controller reports accumulated media/data-integrity errors."
            : "The NVMe health log was read and reports no active health warning.";

        var attributes = new List<StorageSmartAttribute>
        {
            Standard("NVMe", "criticalWarning", "Critical warning mask", warning.ToString("X2"), "hex mask", "Bit fields defined by NVMe."),
            Standard("NVMe", "temperature", "Composite temperature", temperature?.ToString(), "°C", null),
            Standard("NVMe", "availableSpare", "Available spare", spare?.ToString(), "%", null),
            Standard("NVMe", "availableSpareThreshold", "Available spare threshold", spareThreshold?.ToString(), "%", null),
            Standard("NVMe", "percentageUsed", "Percentage used", wear?.ToString(), "%", "Controller endurance estimate; 100% alone is not a failure state."),
            Standard("NVMe", "powerCycles", "Power cycles", cycles, "count", null),
            Standard("NVMe", "powerOnHours", "Power-on hours", hours, "h", null),
            Standard("NVMe", "mediaErrors", "Media and data-integrity errors", mediaErrors, "count", null),
            Standard("NVMe", "errorLogEntries", "Error information log entries", errorEntries, "count", null),
            Standard("NVMe", "dataUnitsRead", "Data units read", UInt128Decimal(log[32..48]), "1000 × 512 bytes", "Converted byte total uses the NVMe-defined data-unit scale."),
            Standard("NVMe", "dataUnitsWritten", "Data units written", UInt128Decimal(log[48..64]), "1000 × 512 bytes", "Converted byte total uses the NVMe-defined data-unit scale."),
        };

        return new ParsedSmartData(health, basis, temperature, warningTemperature, criticalTemperature, wear,
            wear is null ? null : Math.Clamp(100 - wear.Value, 0, 100), spare, spareThreshold, hours, cycles,
            mediaErrors, errorEntries, readBytes, writtenBytes, attributes);
    }

    public static IReadOnlyList<StorageSmartAttribute> ParseAtaAttributes(ReadOnlySpan<byte> smartData,
        ReadOnlySpan<byte> thresholdData = default)
    {
        if (smartData.Length != SectorSize) throw new InvalidDataException("ATA SMART data must contain exactly 512 bytes.");
        var thresholds = thresholdData.Length == SectorSize ? ParseAtaThresholds(thresholdData) : new Dictionary<int, int>();
        var result = new List<StorageSmartAttribute>();
        for (var offset = 2; offset + 12 <= 362; offset += 12)
        {
            var id = smartData[offset];
            if (id == 0) continue;
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(smartData.Slice(offset + 1, 2));
            var raw = Convert.ToHexString(smartData.Slice(offset + 5, 6));
            var threshold = thresholds.GetValueOrDefault(id);
            var normalized = smartData[offset + 3];
            result.Add(new StorageSmartAttribute("ATA", id.ToString("D3"), AtaNames.GetValueOrDefault(id, $"Vendor-defined attribute {id}"),
                Metric<string>.Available(raw, "ATA SMART attribute raw bytes"),
                Metric<int>.Available(normalized, "ATA SMART normalized value"),
                Metric<int>.Available(smartData[offset + 4], "ATA SMART normalized worst value"),
                thresholds.ContainsKey(id) ? Metric<int>.Available(threshold, "ATA SMART threshold table") : Metric<int>.Unavailable("ATA SMART threshold table"),
                (flags & 1) != 0 ? "Marked as a pre-failure attribute by the device." : "Device-defined semantics; raw value is not interpreted without model-specific documentation.",
                null));
        }
        return result;
    }

    public static StorageHealth AssessAtaHealth(bool smartSupported, bool smartEnabled, bool? thresholdExceeded,
        IReadOnlyList<StorageSmartAttribute> attributes)
    {
        if (!smartSupported || !smartEnabled) return StorageHealth.Unknown;
        if (thresholdExceeded == true) return StorageHealth.Critical;
        if (thresholdExceeded == false) return StorageHealth.Healthy;
        var tripped = attributes.Any(attribute => attribute.Interpretation?.StartsWith("Marked as a pre-failure", StringComparison.Ordinal) == true &&
            attribute.NormalizedValue.HasValue && attribute.ThresholdValue.HasValue && attribute.NormalizedValue.Value <= attribute.ThresholdValue.Value);
        return tripped ? StorageHealth.Critical : StorageHealth.Unknown;
    }

    public static IReadOnlyDictionary<int, int> ParseAtaThresholds(ReadOnlySpan<byte> data)
    {
        if (data.Length != SectorSize) return new Dictionary<int, int>();
        var result = new Dictionary<int, int>();
        for (var offset = 2; offset + 12 <= 362; offset += 12)
            if (data[offset] != 0) result[data[offset]] = data[offset + 1];
        return result;
    }

    public static int? KelvinToCelsius(ushort kelvin) => kelvin is >= 173 and <= 423 ? kelvin - 273 : null;

    private static int? ValidPercent(byte value) => value <= 100 ? value : null;
    private static string UInt128Decimal(ReadOnlySpan<byte> bytes, int multiplier = 1) =>
        (new BigInteger(bytes, isUnsigned: true, isBigEndian: false) * multiplier).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static StorageSmartAttribute Standard(string protocol, string id, string name, string? raw, string? unit, string? interpretation) =>
        new(protocol, id, name,
            raw is null ? Metric<string>.Unavailable("NVMe health information log") : Metric<string>.Available(raw, "NVMe health information log"),
            Metric<int>.Unsupported("NVMe health information log", "No normalized vendor-independent value is defined."),
            Metric<int>.Unsupported("NVMe health information log"),
            Metric<int>.Unsupported("NVMe health information log"), interpretation, unit);
}
