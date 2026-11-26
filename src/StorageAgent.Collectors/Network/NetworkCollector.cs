using StorageAgent.Contracts;
using static StorageAgent.Collectors.ProbeDefinitions;

namespace StorageAgent.Collectors;

internal static class NetworkCollectorFactory
{
    public static ISystemCollector Create() => new WmiCollector(SystemCategory.Network, 5,
            P("adapters", "Win32_NetworkAdapter", F("AdapterTypeID","typeCode"), F("Speed","linkSpeed","bit/s"), F("NetConnectionStatus","connectionStatusCode")),
            P("traffic", "Win32_PerfFormattedData_Tcpip_NetworkInterface", F("BytesReceivedPersec","received","bytes/s"), F("BytesSentPersec","sent","bytes/s"), F("PacketsReceivedErrors","receiveErrors"), F("PacketsOutboundErrors","sendErrors")));
}
