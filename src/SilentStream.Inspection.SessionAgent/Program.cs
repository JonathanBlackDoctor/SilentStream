using SilentStream.Inspection;
using SilentStream.Inspection.SessionAgent;

Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
Application.EnableVisualStyles();
var host = new SessionAgentHost();
await host.RunAsync(CancellationToken.None);
