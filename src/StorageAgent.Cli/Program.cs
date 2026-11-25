using System.Security;
using System.Security.Cryptography;
using StorageAgent.Client;

if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
{
    PrintHelp();
    return 0;
}

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("The Storage Agent CLI communicates with its Windows service and is available on Windows only.");
    return 2;
}

try
{
    var client = new StorageAgentClient();
    switch (args[0].ToLowerInvariant())
    {
        case "connect":
        {
            if (args.Length != 3 || args[1] != "--server") throw new ArgumentException("Usage: storageagent connect --server <https-url>");
            Console.Write("One-time connection code (input hidden): ");
            var code = ReadSecret();
            try
            {
                var connection = await client.ConnectAsync(args[2], code);
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
                Console.WriteLine($"{connection.ConnectionId}\t{connection.State}\t{connection.ServerUrl}\tlast sync: {connection.LastSyncAt?.ToLocalTime().ToString("u") ?? "never"}\t{connection.LastError ?? string.Empty}");
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
            var connections = await client.ListConnectionsAsync();
            Console.WriteLine($"Service: {(status.Ready ? "ready" : "starting")}; version {status.ServiceVersion}; local inventory {status.LastInventoryAt.ToLocalTime():u}; remote connections {connections.Count}.");
            if (status.LastError is not null) Console.WriteLine($"Last local collector error: {status.LastError}");
            break;
        }
        case "logs":
        {
            var lines = args.Length == 2 && int.TryParse(args[1], out var requested) ? requested : 50;
            foreach (var line in await client.GetLogsAsync(lines)) Console.WriteLine(line);
            break;
        }
        default:
            throw new ArgumentException("Unsupported command. Run 'storageagent help' for usage.");
    }
    return 0;
}
catch (StorageAgentException error)
{
    Console.Error.WriteLine($"Storage Agent request failed ({error.Code}): {error.Message}");
    return 1;
}
catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException or SecurityException)
{
    Console.Error.WriteLine($"Storage Agent command failed: {error.Message}");
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
    Console.WriteLine("Storage Agent administration");
    Console.WriteLine("  storageagent connect --server <https-url>  Pair with a server using a one-time code (prompted securely).");
    Console.WriteLine("  storageagent connections list               List independent server connections.");
    Console.WriteLine("  storageagent disconnect <connection-id>    Revoke and remove a server connection.");
    Console.WriteLine("  storageagent sync                           Send queued inventory and process safe diagnostic requests.");
    Console.WriteLine("  storageagent status                         Show service and connection status.");
    Console.WriteLine("  storageagent logs [count]                   Show recent local service logs.");
}
