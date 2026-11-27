using Orinasako.Pulse.Contracts;
using static Orinasako.Pulse.Collectors.ProbeDefinitions;

namespace Orinasako.Pulse.Collectors;

internal static class SecurityCollectorFactory
{
    public static ISystemCollector Create() => new SecurityCollector(new WmiCollector(SystemCategory.Security, 300,
            new("antivirus", @"root\SecurityCenter2", "AntiVirusProduct", [F("productState","providerStateCode")]),
            new("firewall", @"root\StandardCimv2", "MSFT_NetFirewallProfile", [F("Enabled","enabled")]),
            new("bitlocker", @"root\CIMV2\Security\MicrosoftVolumeEncryption", "Win32_EncryptableVolume", [F("ProtectionStatus","protectionStatusCode")]),
            new("tpm", @"root\CIMV2\Security\MicrosoftTpm", "Win32_Tpm", [F("IsEnabled_InitialValue","enabled"), F("IsActivated_InitialValue","activated")])));
}
