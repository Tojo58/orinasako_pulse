using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orinasako.Pulse.Contracts;

public static class StorageProtocol
{
    public const int Major = 1;
    public const int Minor = 0;
    public const string PipeName = "storage-health-agent.v1";
    public const int MaxFrameBytes = 1_048_576;

    public static bool RequiresAdministrator(StorageOperation operation) => operation is not
        (StorageOperation.Status or StorageOperation.Inventory or StorageOperation.Diagnostics or
         StorageOperation.ResolvePath or StorageOperation.Subscribe);
}

public enum StorageOperation { Status, Inventory, Diagnostics, ResolvePath, Refresh, Subscribe, Connect, ConnectionsList, Disconnect, Sync, Logs, SystemReport, FileWatchConfigure, FileWatchStatus, FileInventoryPage, FileChanges, FileWatchRefresh, FileWatchPauseVolume }
public enum MetricStatus { Available, Unavailable, Unsupported, Error }
public enum StorageHealth { Healthy, Degraded, Critical, Unavailable, Unknown }
public enum StorageTechnology { Hdd, Ssd, Nvme, Scm, Unknown }
public enum StorageTransport { Ata, Sata, Scsi, Nvme, Usb, Raid, Sas, Virtual, Other, Unknown, FireWire, Sd, Mmc }
public enum StorageIdentityConfidence { HardwareId, DeviceInstance, SessionOnly }
public enum StorageVolumeKind { Local, Removable, Network, Optical, Unknown }
public enum StorageChangeKind { DeviceArrived, DeviceRemoved, HealthChanged, MetricsChanged }

public sealed record Metric<T>(MetricStatus Status, T? Value, string Source, string? Detail = null)
{
    public bool HasValue => Status == MetricStatus.Available;
    public static Metric<T> Available(T value, string source) => new(MetricStatus.Available, value, source);
    public static Metric<T> Unavailable(string source, string? detail = null) => new(MetricStatus.Unavailable, default, source, detail);
    public static Metric<T> Unsupported(string source, string? detail = null) => new(MetricStatus.Unsupported, default, source, detail);
    public static Metric<T> Error(string source, string detail) => new(MetricStatus.Error, default, source, detail);
}

/// <summary>Hardware serials are deliberately absent from every transportable contract.</summary>
public sealed record StorageDisk(
    string Id,
    StorageIdentityConfidence IdentityConfidence,
    int? DeviceNumber,
    string Name,
    Metric<string> Manufacturer,
    Metric<string> Model,
    Metric<string> Firmware,
    Metric<long> CapacityBytes,
    StorageTechnology Technology,
    StorageTransport Transport,
    Metric<int> RotationRateRpm,
    StorageHealth Health,
    string? HealthBasis,
    Metric<int> TemperatureC,
    Metric<int> WarningTemperatureC,
    Metric<int> CriticalTemperatureC,
    Metric<int> WearUsedPercent,
    Metric<int> LifeRemainingPercent,
    Metric<int> AvailableSparePercent,
    Metric<int> AvailableSpareThresholdPercent,
    Metric<string> PowerOnHours,
    Metric<string> PowerCycleCount,
    Metric<string> StartStopCycleCount,
    Metric<string> ReallocatedSectors,
    Metric<string> PendingSectors,
    Metric<string> UncorrectableSectors,
    Metric<string> ReadErrors,
    Metric<string> WriteErrors,
    Metric<string> CommunicationErrors,
    Metric<string> MediaErrors,
    Metric<string> ErrorLogEntries,
    Metric<string> DataReadBytes,
    Metric<string> DataWrittenBytes,
    Metric<bool> SmartSupported,
    IReadOnlyList<StorageSmartAttribute> SmartAttributes,
    IReadOnlyList<StorageDiagnostic> Diagnostics,
    DateTimeOffset ObservedAt);

public sealed record StorageSmartAttribute(
    string Protocol,
    string Id,
    string Name,
    Metric<string> RawValueHex,
    Metric<int> NormalizedValue,
    Metric<int> WorstValue,
    Metric<int> ThresholdValue,
    string? Interpretation,
    string? Unit);

public sealed record StorageDiagnostic(string Code, string Message, string Source, DateTimeOffset ObservedAt);

public sealed record StorageVolume(
    string Id,
    string? Label,
    string? FileSystem,
    StorageVolumeKind Kind,
    IReadOnlyList<string> MountPoints,
    Metric<long> CapacityBytes,
    Metric<long> FreeBytes,
    Metric<long> UsedBytes,
    IReadOnlyList<string> PhysicalDiskIds,
    string? Diagnostic,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LocalMountFingerprint = null);

public sealed record StorageSnapshot(
    int ContractMajor,
    int ContractMinor,
    DateTimeOffset ObservedAt,
    IReadOnlyList<StorageDisk> Disks,
    IReadOnlyList<StorageVolume> Volumes,
    IReadOnlyList<StorageDiagnostic> Diagnostics);

public sealed record OrinasakoPulseStatus(
    string ServiceVersion,
    int ContractMajor,
    int ContractMinor,
    bool Ready,
    DateTimeOffset StartedAt,
    DateTimeOffset LastInventoryAt,
    string? LastError);

public sealed record StorageChange(StorageChangeKind Kind, string DeviceId, StorageHealth? PreviousHealth,
    StorageHealth? CurrentHealth, DateTimeOffset OccurredAt);

public sealed record StorageRequest(
    int ContractMajor,
    int ContractMinor,
    string RequestId,
    StorageOperation Operation,
    string? DeviceId = null,
    string? Path = null,
    bool Force = false,
    int? WaitSeconds = null,
    string? ServerUrl = null,
    string? EnrollmentCode = null,
    string? ConnectionId = null,
    int? LogLines = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<SystemCategory>? SystemCategories = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? AllowStorage = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FileWatchRequest? FileWatch = null);

public sealed record StorageResponse(
    int ContractMajor,
    int ContractMinor,
    string RequestId,
    bool Ok,
    string? ErrorCode,
    string? Error,
    JsonElement? Payload);

public sealed record ResolvedStoragePath(
    string RequestedPath,
    bool IsNetwork,
    IReadOnlyList<string> PhysicalDiskIds,
    IReadOnlyList<string> VolumeIds,
    string? Diagnostic);

public sealed record StorageControlResult(bool Completed, string? Message = null);

public enum RemoteConnectionState { Connected, ReauthorizationRequired, RevocationPending }
public enum RemoteCommandKind { RefreshInventory, ReadDiskDiagnostics }

/// <summary>Local-only connection metadata; access and refresh tokens are never included.</summary>
public sealed record RemoteConnectionInfo(
    string ConnectionId,
    string ServerUrl,
    string AgentId,
    DateTimeOffset ConnectedAt,
    DateTimeOffset? LastSyncAt,
    DateTimeOffset? LastSnapshotAt,
    RemoteConnectionState State,
    string? LastError,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<SystemCategory>? SystemCategories = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? LastSystemSnapshotAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? AllowStorage = null);

public sealed record RemoteEnrollmentRequest(
    string RequestId,
    string EnrollmentCode,
    string AgentId,
    int ProtocolMajor,
    int ProtocolMinor,
    string AgentVersion);

public sealed record RemoteEnrollmentResponse(
    string ConnectionId,
    string AgentId,
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessTokenExpiresAt,
    DateTimeOffset RefreshTokenExpiresAt);

public sealed record RemoteTokenRefreshRequest(string RequestId, string RefreshToken);
public sealed record RemoteTokenRefreshResponse(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessTokenExpiresAt,
    DateTimeOffset RefreshTokenExpiresAt);

public sealed record RemoteSnapshotEnvelope(string DeliveryId, string AgentId, StorageSnapshot Snapshot,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SystemTelemetry? System = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? StorageIncluded = null);
public sealed record RemoteChangeEnvelope(string EventId, string AgentId, StorageChange Change);
public sealed record RemoteChangeBatch(string AgentId, IReadOnlyList<RemoteChangeEnvelope> Events);
public sealed record RemoteCommand(
    string CommandId,
    RemoteCommandKind Kind,
    string? DeviceId,
    DateTimeOffset ExpiresAt);
public sealed record RemoteCommandBatch(IReadOnlyList<RemoteCommand> Commands);
public sealed record RemoteCommandResult(
    string CommandId,
    bool Succeeded,
    string? ErrorCode,
    StorageSnapshot? Snapshot,
    StorageDisk? Disk,
    DateTimeOffset CompletedAt);
public sealed record RemoteSyncResult(int SnapshotsSent, int EventsSent, int CommandsProcessed, DateTimeOffset CompletedAt);

public static class StorageContractJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            MaxDepth = 24
        };
        options.Converters.Add(new MetricJsonConverterFactory());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public static StorageResponse Success<T>(string requestId, T payload) => new(
        StorageProtocol.Major, StorageProtocol.Minor, requestId, true, null, null,
        JsonSerializer.SerializeToElement(payload, Options));

    public static StorageResponse Failure(string requestId, string code, string message) => new(
        StorageProtocol.Major, StorageProtocol.Minor, requestId, false, code, message, null);

    public static T? ReadPayload<T>(StorageResponse response) => response.Payload is { } payload
        ? payload.Deserialize<T>(Options)
        : default;
}

public sealed class MetricJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsGenericType &&
        typeToConvert.GetGenericTypeDefinition() == typeof(Metric<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var valueType = typeToConvert.GetGenericArguments()[0];
        return (JsonConverter)(Activator.CreateInstance(typeof(MetricJsonConverter<>).MakeGenericType(valueType))
            ?? throw new InvalidOperationException("Metric JSON converter could not be created."));
    }

    private sealed class MetricJsonConverter<T> : JsonConverter<Metric<T>>
    {
        public override Metric<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            var status = root.GetProperty("status").Deserialize<MetricStatus>(options);
            var source = root.GetProperty("source").GetString();
            if (string.IsNullOrWhiteSpace(source)) throw new JsonException("Metric source is required.");
            var detail = root.TryGetProperty("detail", out var detailElement) && detailElement.ValueKind == JsonValueKind.String
                ? detailElement.GetString() : null;
            var valueElement = root.GetProperty("value");
            if (status != MetricStatus.Available)
            {
                if (valueElement.ValueKind != JsonValueKind.Null) throw new JsonException("Unavailable metric values must be null on the wire.");
                return new Metric<T>(status, default, source, detail);
            }
            if (valueElement.ValueKind == JsonValueKind.Null) throw new JsonException("Available metrics must contain a value.");
            var value = valueElement.Deserialize<T>(options);
            if (value is null) throw new JsonException("Available metric value could not be read.");
            return new Metric<T>(status, value, source, detail);
        }

        public override void Write(Utf8JsonWriter writer, Metric<T> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("status", JsonNamingPolicy.CamelCase.ConvertName(value.Status.ToString()));
            writer.WritePropertyName("value");
            if (value.Status == MetricStatus.Available) JsonSerializer.Serialize(writer, value.Value, options);
            else writer.WriteNullValue();
            writer.WriteString("source", value.Source);
            if (value.Detail is not null) writer.WriteString("detail", value.Detail);
            writer.WriteEndObject();
        }
    }
}
