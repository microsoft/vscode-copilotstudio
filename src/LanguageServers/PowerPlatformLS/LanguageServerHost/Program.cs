using Microsoft.ApplicationInsights.Channel;
using Microsoft.CommonLanguageServerProtocol.Framework;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
using Microsoft.PowerPlatformLS.Contracts.Internal.Common.DependencyInjection;
using Microsoft.PowerPlatformLS.Impl.Core.DependencyInjection;
using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.DependencyInjection;
using Microsoft.PowerPlatformLS.Impl.Language.PowerFx.DependencyInjection;
using Microsoft.PowerPlatformLS.Impl.Language.Yaml.DependencyInjection;
using Microsoft.PowerPlatformLS.Impl.PullAgent.DependencyInjection;
using Microsoft.PowerPlatformLS.LanguageServerHost;
using System.Diagnostics;

// Lifespan must be top-level for whole app.
using var channel = new InMemoryChannel();

try
{
    ConsoleLog("Configuring services...");
    var builder = Host.CreateApplicationBuilder(args);
    builder.AddLsp(args);

    bool isDebuggerRequested = builder.Configuration.IsDebuggerRequested();
    if (isDebuggerRequested)
    {
        Debugger.Launch();
    }

    builder.Services.AddVersionInfo();
    builder.Services.AddSingleton<ILspModule, PowerFxLspModule>();
    builder.Services.AddSingleton<ILspModule, YamlLspModule>();
    builder.Services.AddSingleton<ILspModule, McsLspModule>();
    builder.Services.AddSingleton<ILspModule>(sp => new PullAgentLspModule(
        sp.GetRequiredService<BuildVersionInfo>(),
        sp.GetRequiredService<ILoggerFactory>()));

    // Forward MEL entries to the LSP client via window/logMessage so the
    // extension renders them in its LogOutputChannel with [error]/[warning]/[info]
    // prefix and timestamp. Also disables the default Console provider.
    builder.UseLspWindowLogMessageLogging();

    var isTelemetryEnabled = ParseTelemetryEnabledFromArgs(args);
    builder.Services.ConfigureAppInsightsLogging(channel, isTelemetryEnabled);

    var sessionId = ParseSessionIdFromArgs(args);
    var sessionInfo = new SessionInformation
    {
        SessionId = sessionId
    };
    builder.Services.AddSingleton(sessionInfo);

    ConsoleLog("Building host...");
    var host = builder.Build();

    var lspLogger = host.Services.GetRequiredService<ILspLogger>();
    LogStartup(lspLogger, isDebuggerRequested, sessionId, isTelemetryEnabled);

    host.Run();
}
finally
{
    // Explicitly call Flush() followed by Delay, as required in console apps.
    // This ensures that even if the application terminates, telemetry is sent to the back end.
    channel.Flush();
    await Task.Delay(TimeSpan.FromMilliseconds(1000));
}

// Log a startup message. This can be used to determine unique machines.
// Most logging is done via ILspLogger, so use that. 
void LogStartup(ILspLogger logger, bool isDev, string? sessionId, bool isTelemetryEnabled)
{
    // AppInsights will automatically capture machine name as "cloud_RoleInstance";
    var id = System.Diagnostics.Process.GetCurrentProcess().Id;
    string osver = Environment.OSVersion.VersionString;
    string dotnetver = Environment.Version.ToString();
    string osArch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(); // "X64", etc 
    string processArch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
    string os = System.Runtime.InteropServices.RuntimeInformation.OSDescription; // "Windows 10 Pro", etc
    string sid = sessionId ?? string.Empty;
    string telemetry = isTelemetryEnabled ? "enabled" : "disabled";

    logger.LogInformation("MCS-LSP Startup: pid={id}, sessionId={sid}, telemetry={telemetry}, dev={isDev}, os={os}, osVersion={osver}, dotnet={dotnetver}, arch={osArch}/{processArch}", id, sid, telemetry, isDev, os, osver, dotnetver, osArch, processArch);
}

// Helper method to parse sessionId from command line arguments
string? ParseSessionIdFromArgs(string[] args)
{
    string sessionIdPrefix = "--sessionid=";
    foreach (var arg in args)
    {
        if (arg.StartsWith(sessionIdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return arg.Substring(sessionIdPrefix.Length);
        }
    }
    return null;
}

// Helper method to parse telemetry enabled state from command line arguments
bool ParseTelemetryEnabledFromArgs(string[] args)
{
    string enableTelemetryPrefix = "--enabletelemetry=";
    foreach (var arg in args)
    {
        if (arg.StartsWith(enableTelemetryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return bool.TryParse(arg.Substring(enableTelemetryPrefix.Length), out bool isTelemetryEnabled) && isTelemetryEnabled;
        }
    }
    return false;
}

// Writes a message to stdout for early bootstrap diagnostics
// (before the MEL/LSP logging pipeline is available).
// VS Code's LogOutputChannel adds its own timestamp and level prefix.
void ConsoleLog(string message)
{
    Console.WriteLine(message);
}
