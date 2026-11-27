using System.Security;
using System.Security.Cryptography;
using Orinasako.Pulse.Client;
using Orinasako.Pulse.Contracts;
using System.Text.Json;

if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
{
    PrintHelp();
    return 0;
}

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("The Orinasako Pulse CLI communicates with its Windows service and is available on Windows only.");
    return 2;
}

try
{
    var client = new OrinasakoPulseClient();
    switch (args[0].ToLowerInvariant())
    {
        case "connect":
        {
            if ((args.Length != 3 && args.Length != 5) || args[1] != "--server" || (args.Length == 5 && args[3] != "--collectors"))
                throw new ArgumentException("Usage: pulse connect --server <https-url> [--collectors cpu,memory,...]");
            var categories = new List<SystemCategory>();
            var includeStorage = args.Length == 3;
            if (args.Length == 5)
                foreach (var name in args[4].Split(',', StringSplitOptions.TrimEntries))
                {
                    if (string.Equals(name, "storage", StringComparison.OrdinalIgnoreCase)) { includeStorage = true; continue; }
                    if (!Enum.TryParse<SystemCategory>(name, true, out var category) || !Enum.IsDefined(category) ||
                        !string.Equals(category.ToString(), name, StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("Unknown system collector category.");
                    if (!categories.Contains(category)) categories.Add(category);
                }
            Console.Write("One-time connection code (input hidden): ");
            var code = ReadSecret();
            try
            {
                var connection = categories.Count == 0 ? await client.ConnectAsync(args[2], code) :
                    await client.ConnectSystemAsync(args[2], code, categories, includeStorage: includeStorage);
                Console.WriteLine($"Connected to {connection.ServerUrl}. Connection id: {connection.ConnectionId}");
            }
            finally { code = string.Empty; }
            break;
        }
        case "connections" when args.Length == 2 && args[1] == "list":
        {
            var connections = await client.ListConnectionsAsync();
            if (connections.Count == 0) { Console.WriteLine("No remote server connections are configured."); break; }
            foreach (var connection in connections)
            {
                var scopes = (connection.AllowStorage == false ? Enumerable.Empty<string>() : new[] { "storage" }).Concat(
                    (connection.SystemCategories ?? []).Select(c => c.ToString()));
                Console.WriteLine($"{connection.ConnectionId}\t{connection.State}\t{connection.ServerUrl}\tlast sync: {connection.LastSyncAt?.ToLocalTime().ToString("u") ?? "never"}\t{connection.LastError ?? string.Empty}\tcollectors: {string.Join(",", scopes)}");
            }
            break;
        }
        case "disconnect" when args.Length == 2:
        {
            var result = await client.DisconnectAsync(args[1]);
            Console.WriteLine(result.Message ?? "Connection disabled.");
            break;
        }
        case "sync" when args.Length == 1:
        {
            var result = await client.SynchronizeAsync();
            Console.WriteLine($"Synchronized: {result.SnapshotsSent} snapshot(s), {result.EventsSent} event(s), {result.CommandsProcessed} read-only command(s).");
            break;
        }
        case "status" when args.Length == 1:
        {
            var status = await client.GetStatusAsync();
            Console.WriteLine($"Service: {(status.Ready ? "ready" : "starting")}; version {status.ServiceVersion}; local inventory {status.LastInventoryAt.ToLocalTime():u}.");
            if (status.LastError is not null) Console.WriteLine($"Last local collector error: {status.LastError}");
            break;
        }
        case "watch" when args.Length == 3 && args[1] == "clear":
            await client.ConfigureFileWatchesAsync(args[2], []);
            Console.WriteLine("Filesystem subscriptions removed.");
            break;
        case "watch" when args.Length == 3 && args[1] == "list":
            Console.WriteLine(JsonSerializer.Serialize(await client.GetFileWatchStatusAsync(args[2]), StorageContractJson.Options));
            break;
        case "watch" when args.Length >= 4 && args[1] == "configure":
            await client.ConfigureFileWatchesAsync(args[2], args[3..]);
            Console.WriteLine("Filesystem subscriptions configured.");
            break;
        case "system" when args.Length == 1 || (args.Length == 2 && args[1] == "--json"):
            Console.WriteLine(JsonSerializer.Serialize(await client.GetSystemReportAsync(), StorageContractJson.Options));
            break;
        case "logs":
        {
            var lines = args.Length == 2 && int.TryParse(args[1], out var requested) ? requested : 50;
            foreach (var line in await client.GetLogsAsync(lines)) Console.WriteLine(line);
            break;
        }
        default:
            throw new ArgumentException("Unsupported command. Run 'pulse help' for usage.");
    }
    return 0;
}
catch (OrinasakoPulseException error)
{
    Console.Error.WriteLine($"Orinasako Pulse request failed ({error.Code}): {error.Message}");
    return 1;
}
catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException or SecurityException)
{
    Console.Error.WriteLine($"Orinasako Pulse command failed: {error.Message}");
    return 1;
}

static string ReadSecret()
{
    var chars = new char[256];
    var length = 0;
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace)
        {
            if (length > 0) chars[--length] = '\0';
            continue;
        }
        if (!char.IsControl(key.KeyChar) && length < chars.Length) chars[length++] = key.KeyChar;
    }
    Console.WriteLine();
    try { return new string(chars, 0, length); }
    finally { CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(chars.AsSpan())); }
}

static void PrintHelp()
{
    Console.WriteLine("Orinasako Pulse administration");
    Console.WriteLine("  pulse connect --server <https-url>  Pair with a server using a one-time code (prompted securely).");
    Console.WriteLine("  pulse connections list               List independent server connections.");
    Console.WriteLine("  pulse disconnect <connection-id>    Revoke and remove a server connection.");
    Console.WriteLine("  pulse sync                           Send queued inventory and process safe diagnostic requests.");
    Console.WriteLine("  pulse system --json                  Full local system report (administrator).");
    Console.WriteLine("  pulse connect --server <url> --collectors cpu,memory,...   Opt into system telemetry for this server.");
    Console.WriteLine("  pulse status                         Show service and connection status.");
    Console.WriteLine("  pulse watch configure <consumer> <root> [...]   Register local filesystem roots (administrator).");
    Console.WriteLine("  pulse watch list <consumer> / watch clear <consumer>   Inspect or remove subscriptions.");
    Console.WriteLine("  pulse logs [count]                   Show recent local service logs.");
}
