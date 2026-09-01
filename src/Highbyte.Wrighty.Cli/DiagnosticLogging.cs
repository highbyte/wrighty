using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace Highbyte.Wrighty.Cli;

internal static class DiagnosticLogging
{
    public static ILoggerFactory CreateFactory() => LoggerFactory.Create(builder =>
    {
        builder.SetMinimumLevel(LogLevel.Information);
        builder.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffzzz ";
        });
        builder.Services.Configure<ConsoleLoggerOptions>(options =>
            options.LogToStandardErrorThreshold = LogLevel.Trace);
    });
}

internal static partial class CliDiagnostics
{
    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "Both project and user Wrighty skills are installed for {AgentLabel}. Agent hosts resolve duplicate skill names differently. Remove one with 'wrighty skill uninstall --agent {AgentSelection} --scope project' or 'wrighty skill uninstall --agent {AgentSelection} --scope user'.")]
    public static partial void DuplicateSkillInstallations(
        ILogger logger,
        string agentLabel,
        string agentSelection);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Warning,
        Message = "The {SkillScope} Wrighty skill for {AgentLabel} at '{SkillPath}' is {SkillState}{VersionDetails}. Run '{MaintenanceCommand}'.")]
    public static partial void SkillNeedsAttention(
        ILogger logger,
        string skillScope,
        string agentLabel,
        string skillPath,
        string skillState,
        string versionDetails,
        string maintenanceCommand);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Warning,
        Message = "{WarningMessage}")]
    public static partial void WorkerRuntimeWarning(
        ILogger logger,
        string warningMessage);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Warning,
        Message = "{WarningMessage}")]
    public static partial void AgentContextWarning(
        ILogger logger,
        string warningMessage);
}
