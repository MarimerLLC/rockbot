using System.Diagnostics.Metrics;

namespace RockBot.Tools.Mcp.Recovery;

/// <summary>
/// Counters and histograms for the MCP recovery layer. Reuses the
/// <c>RockBot.Tools</c> Meter so events flow through the existing
/// telemetry registration in <see cref="RockBot.Telemetry"/>.
/// </summary>
internal static class RecoveryDiagnostics
{
    public static readonly Counter<long> Attempts =
        ToolDiagnostics.Meter.CreateCounter<long>(
            "rockbot.mcp.recovery.attempts",
            unit: "{attempt}",
            description: "MCP recovery attempts. Tags: server, tool, field, stage, outcome, provider.");

    public static readonly Histogram<double> Duration =
        ToolDiagnostics.Meter.CreateHistogram<double>(
            "rockbot.mcp.recovery.duration",
            unit: "ms",
            description: "Duration of an MCP recovery attempt (Stage A or B), end-to-end.");
}
