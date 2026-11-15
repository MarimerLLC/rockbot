using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace RockBot.UserProxy.Cli;

/// <summary>
/// Prints the agent's name and version (round-trip ping). With <c>--models</c>, <c>--pricing</c>
/// or <c>--all</c> it also prints what the agent is configured with, which the agent reports
/// without invoking its LLM (#834).
/// </summary>
internal sealed class InfoCommand : AsyncCommand<InfoCommand.Settings>
{
    public sealed class Settings : CommonSettings
    {
        [CommandOption("--models")]
        [Description("Also show the configured models: each tier with its fallback chain, embeddings and the decision model")]
        public bool Models { get; init; }

        [CommandOption("--pricing")]
        [Description("Also show the pricing table the agent estimates cost with (USD per million tokens)")]
        public bool Pricing { get; init; }

        [CommandOption("--all")]
        [Description("Show every section the agent reports")]
        public bool All { get; init; }

        [CommandOption("--json")]
        [Description("Write the agent's answer as JSON instead of text")]
        public bool Json { get; init; }

        /// <summary>The sections asked for; empty for the plain name-and-version ping.</summary>
        public IReadOnlyList<string> Sections =>
            All ? IntrospectionSections.All
            : [.. (Models ? [IntrospectionSections.Models] : Array.Empty<string>()),
               .. (Pricing ? [IntrospectionSections.Pricing] : Array.Empty<string>())];
    }

    private static readonly JsonSerializerOptions JsonOutput = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        using var host = HostFactory.Build(settings, useRichFrontend: false);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetRequiredService<UserProxyService>();
            var sections = settings.Sections;

            if (sections.Count == 0 && !settings.Json)
            {
                var info = await proxy.GetAgentInfoAsync();
                if (info is null)
                {
                    Console.Error.WriteLine("error: agent did not respond before timeout");
                    return 2;
                }

                Console.Out.WriteLine($"{info.AgentName} {info.AgentVersion}");
                return 0;
            }

            var introspection = await proxy.GetIntrospectionAsync(sections);
            if (introspection is null)
            {
                Console.Error.WriteLine(
                    "error: agent did not respond before timeout (an agent older than this CLI doesn't answer introspection requests)");
                return 2;
            }

            if (settings.Json)
                Console.Out.WriteLine(JsonSerializer.Serialize(introspection, JsonOutput));
            else
                IntrospectionText.Write(Console.Out, introspection, sections);
            return 0;
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
