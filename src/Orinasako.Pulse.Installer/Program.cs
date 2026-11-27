using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using Orinasako.Pulse.Installer;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("The Orinasako Pulse installer requires Windows.");
    return 2;
}

var arguments = args.ToList();
if (arguments.Count == 0 || arguments[0] is "--help" or "-h")
{
    Console.WriteLine("Orinasako Pulse installer\n  detect\n  install --source <package-directory>\n  update --source <package-directory>\n  ensure --source <package-directory> --minimum-version <semver>\n  uninstall [--remove-data]");
    return arguments.Count == 0 ? 2 : 0;
}

try
{
    var command = arguments[0];
    var source = Option(arguments, "--source");
    if (command == "detect")
    {
        var path = ServiceInstaller.InstallDirectory;
        var exe = Path.Combine(path, "Orinasako.Pulse.Service.exe");
        var version = File.Exists(exe) ? FileVersionInfo.GetVersionInfo(exe).ProductVersion : null;
        var status = ServiceInstaller.QueryStatus();
        Console.WriteLine(JsonSerializer.Serialize(new { installed = File.Exists(exe), version, serviceState = status }));
        return 0;
    }

    if (command == "uninstall")
    {
        RequireAdministrator();
        ServiceInstaller.Uninstall(arguments.Contains("--remove-data", StringComparer.Ordinal));
        Console.WriteLine("Orinasako Pulse service removed. Client applications were not modified.");
        return 0;
    }

    if (command == "ensure")
    {
        var minimum = Version.Parse(Option(arguments, "--minimum-version") ?? throw new ArgumentException("--minimum-version is required."));
        var installed = ServiceInstaller.InstalledVersion();
        if (installed is not null && installed >= minimum && ServiceInstaller.QueryStatus() != "not-installed")
        {
            ServiceInstaller.StartIfInstalled();
            ServiceInstaller.WaitUntilRunning();
            Console.WriteLine($"Orinasako Pulse {installed} is installed and available.");
            return 0;
        }
        RequireAdministrator();
        var payload = source ?? throw new ArgumentException("--source is required to install or update.");
        var payloadVersion = Version.Parse(FileVersionInfo.GetVersionInfo(Path.Combine(payload, "Orinasako.Pulse.Service.exe")).FileVersion ?? "0.0.0");
        if (payloadVersion < minimum || (installed is not null && payloadVersion < installed))
            throw new InvalidDataException("The bundled service does not meet the minimum version or would downgrade the installed service.");
        ServiceInstaller.InstallOrUpdate(payload);
        ServiceInstaller.WaitUntilRunning();
        Console.WriteLine("Orinasako Pulse was installed or updated.");
        return 0;
    }

    if (command is "install" or "update")
    {
        RequireAdministrator();
        ServiceInstaller.InstallOrUpdate(source ?? throw new ArgumentException("--source is required."));
        Console.WriteLine(command == "install" ? "Orinasako Pulse installed." : "Orinasako Pulse updated.");
        return 0;
    }

    throw new ArgumentException($"Unsupported installer command: {command}");
}
catch (Exception error)
{
    Console.Error.WriteLine($"Orinasako Pulse installation failed: {error.GetType().Name} (0x{error.HResult:X8}).");
    return 1;
}

static string? Option(IReadOnlyList<string> args, string name)
{
    var index = -1;
    for (var position = 0; position < args.Count; position++)
        if (string.Equals(args[position], name, StringComparison.Ordinal)) { index = position; break; }
    return index >= 0 && index + 1 < args.Count ? args[index + 1] : null;
}

static void RequireAdministrator()
{
    using var identity = WindowsIdentity.GetCurrent();
    var principal = new WindowsPrincipal(identity);
    if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
        throw new UnauthorizedAccessException("Run the installer from an elevated Administrator session.");
}
