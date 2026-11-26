using StorageAgent.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StorageAgent.Core;
using StorageAgent.Service;

var builder = Host.CreateApplicationBuilder(args);
var fileLogger = new StorageFileLoggerProvider();
builder.Logging.AddProvider(fileLogger);
builder.Services.AddWindowsService(options => options.ServiceName = "StorageAgent");
builder.Services.AddSingleton(fileLogger);
builder.Services.AddSingleton<MachineIdentityStore>();
builder.Services.AddSingleton<IStorageCollector, WindowsDiskCollector>();
builder.Services.AddSingleton<StorageSnapshotStore>();
builder.Services.AddSingleton<StorageChangeHub>();
builder.Services.AddSingleton<StorageRuntimeState>();
builder.Services.AddSingleton<SystemRuntimeState>();
builder.Services.AddHostedService<SystemMonitorService>();
builder.Services.AddSingleton<ISecretProtector, WindowsDpapiSecretProtector>();
builder.Services.AddSingleton<StorageAgent.Core.IMachineKeyProtector>(provider =>
    provider.GetRequiredService<ISecretProtector>());
builder.Services.AddSingleton<RemoteConnectionVault>();
builder.Services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
    MaxResponseHeadersLength = 32
}) { Timeout = Timeout.InfiniteTimeSpan });
builder.Services.AddSingleton<RemoteProtocolClient>();
builder.Services.AddSingleton<RemoteConnectionManager>();
builder.Services.AddHostedService<StorageMonitorService>();
builder.Services.AddHostedService<RemoteSyncService>();
builder.Services.AddHostedService<NamedPipeServer>();
await builder.Build().RunAsync();
