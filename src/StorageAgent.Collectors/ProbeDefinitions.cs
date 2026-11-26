namespace StorageAgent.Collectors;

internal static class ProbeDefinitions
{
    internal const string Cim = @"root\cimv2";
    internal static Field F(string property, string name, string? unit = null) => new(property, name, unit);
    internal static Probe P(string name, string cls, params Field[] fields) => new(name, Cim, cls, fields);
    internal static Probe Inventory(string name, string cls, params Field[] fields) => new(name, Cim, cls, fields, RefreshSeconds: 300);
}
