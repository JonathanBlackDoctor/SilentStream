using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SilentStream.Core.Safety;
using SilentStream.Inspection.Broker;
using SilentStream.Platform.Windows;

if (args.Length == 3 && args[0] == "--nic-watchdog")
{
    await BrokerWorker.EnableNicAfterDelayAsync(args[1], int.Parse(args[2]), CancellationToken.None);
    return;
}
if (args.Length == 6 && args[0] == "--apply-agent-update")
{
    await AgentUpdateApplier.ApplyAsync(args[1], args[2], args[3], args[4], args[5]);
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "SilentStream Inspection Privileged Broker");
builder.Services.AddSingleton<BrokerConfiguration>();
builder.Services.AddSingleton<ISystemPowerSafetyProbe, WindowsSystemPowerSafetyProbe>();
builder.Services.AddHostedService<BrokerWorker>();
await builder.Build().RunAsync();
