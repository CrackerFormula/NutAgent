using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WinNUT;
using WinNUT.Config;

// Command line overrides: --mode server|client --ups-name <name> --remote-host <ip>
// All other config comes from appsettings.json or the Windows registry (via appsettings path).

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<AgentConfig>(builder.Configuration.GetSection("Agent"));
builder.Services.AddHostedService<Worker>();

// First-class Windows service support: graceful start/stop via SCM
builder.Services.AddWindowsService(opts =>
{
    opts.ServiceName = "WinNUT UPS Agent";
});

var host = builder.Build();
await host.RunAsync();
