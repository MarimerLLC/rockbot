using System.ClientModel;
using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenAI;
using RockBot.Host;
using RockBot.Llm;
using RockBot.McpMeasure.Hosting;
using RockBot.McpMeasure.Measurement;
using RockBot.Tools;
using RockBot.Tools.Mcp;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RockBot.McpMeasure;

/// <summary>A provider endpoint. Keys come from user-secrets or the environment, never the JSON file.</summary>
public sealed class ProviderConfig
{
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
}

/// <summary>A model to measure, named after the RockBot tier it stands for.</summary>
public sealed class ModelConfig
{
    public string Tier { get; set; } = "Balanced";
    public string Provider { get; set; } = "";
    public string ModelId { get; set; } = "";
}

public sealed class MeasureOptions
{
    public Dictionary<string, ProviderConfig> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ModelConfig> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string[] DefaultModels { get; set; } = [];
}

/// <summary>Runs the measurement: models × modes × runs × tasks, then writes the tables.</summary>
internal sealed class MeasureCommand : AsyncCommand<MeasureCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--models <NAMES>")]
        [Description("Comma-separated model names from Measure:Models. Default: Measure:DefaultModels.")]
        public string? Models { get; init; }

        [CommandOption("--modes <MODES>")]
        [Description("Comma-separated wrapper modes. Default: Off,Eager,Lazy,Pinned.")]
        public string Modes { get; init; } = "Off,Eager,Lazy,Pinned";

        [CommandOption("--runs <N>")]
        [Description("Runs of each task per model and mode. Default: 10.")]
        public int Runs { get; init; } = 10;

        [CommandOption("--tasks <IDS>")]
        [Description("Comma-separated task ids. Default: all.")]
        public string? Tasks { get; init; }

        [CommandOption("--out <DIR>")]
        [Description("Output directory. Default: measure-results/<timestamp>.")]
        public string? Out { get; init; }

        [CommandOption("--prompt <KIND>")]
        [Description("'agent' (RockBot's real system prompt, the default) or 'minimal' (mcp-aggregator#42's).")]
        public string Prompt { get; init; } = "agent";

        [CommandOption("--max-iterations <N>")]
        [Description("Tool-loop iteration cap per turn. Default: 10.")]
        public int MaxIterations { get; init; } = 10;

        [CommandOption("--scripted")]
        [Description("Use a scripted model instead of the configured ones; needs no API key.")]
        public bool Scripted { get; init; }

        [CommandOption("--dry-run")]
        [Description("Print what would run, and the baseline context per mode, without calling a model.")]
        public bool DryRun { get; init; }

        [CommandOption("--verbose")]
        [Description("Print every call.")]
        public bool Verbose { get; init; }

        [CommandOption("--resume")]
        [Description("Keep the task runs already in --out's results.jsonl that finished without an error; run only the rest.")]
        public bool Resume { get; init; }

        [CommandOption("--summarize <DIR>")]
        [Description("Don't run: write summary.md for every results.jsonl under DIR (e.g. one per model, run in parallel).")]
        public string? Summarize { get; init; }

        [CommandOption("--log-level <LEVEL>")]
        [Description("Log level for RockBot's own logs. Default: Warning.")]
        public LogLevel LogLevel { get; init; } = LogLevel.Warning;

        public override ValidationResult Validate() =>
            Prompt is not ("agent" or "minimal") ? ValidationResult.Error("--prompt must be 'agent' or 'minimal'")
            : Runs < 1 ? ValidationResult.Error("--runs must be at least 1")
            : ValidationResult.Success();
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var ct = cts.Token;

        if (settings.Summarize is { } dir)
        {
            var files = Directory.GetFiles(dir, "results.jsonl", SearchOption.AllDirectories);
            var all = files.SelectMany(Report.Read).ToList();
            var text = Report.Summarize(all,
                $"# MCP wrapper modes — {DateTime.Now:yyyy-MM-dd}\n\nCombined from {files.Length} results file(s), {all.Count} turns.");
            await File.WriteAllTextAsync(Path.Combine(dir, "summary.md"), text, ct);
            AnsiConsole.WriteLine(text);
            return 0;
        }

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("measure.json", optional: false)
            .AddUserSecrets<MeasureCommand>(optional: true)
            .AddEnvironmentVariables()
            .Build();
        var options = new MeasureOptions();
        config.GetSection("Measure").Bind(options);

        using var loggers = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(settings.LogLevel));

        var modes = settings.Modes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(m => Enum.Parse<McpWrapperMode>(m, ignoreCase: true)).ToList();
        var tasks = settings.Tasks is null
            ? MeasureTasks.All
            : [.. MeasureTasks.All.Where(t => settings.Tasks.Split(',', StringSplitOptions.TrimEntries).Contains(t.Id))];

        List<ModelUnderTest> models;
        try
        {
            models = settings.Scripted
                ? [new ModelUnderTest("scripted", "Balanced", ScriptedChatClient.ModelId, new ScriptedChatClient(), ModelBehavior.Default)]
                : BuildModels(settings, options, loggers);
        }
        catch (InvalidOperationException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 2;
        }

        var systemPrompt = settings.Prompt == "minimal" ? Prompts.Minimal() : Prompts.Agent(AppContext.BaseDirectory);
        var turnsPerRun = tasks.Sum(t => t.Turns.Count);

        await using var fixtures = await FixtureHost.StartAsync();
        var rigs = new List<MeasureRig>();
        try
        {
            foreach (var mode in modes)
                rigs.Add(await MeasureRig.StartAsync(fixtures, mode, loggers, ct));

            PrintPlan(settings, models, rigs, tasks, turnsPerRun, systemPrompt);
            if (settings.DryRun)
                return 0;

            var outDir = settings.Out ?? Path.Combine("measure-results", DateTime.Now.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(outDir);
            var resultsPath = Path.Combine(outDir, "results.jsonl");
            var done = settings.Resume ? await KeepCompletedAsync(resultsPath, ct) : [];
            if (!settings.Resume)
                File.Delete(resultsPath);

            var runner = new Runner(fixtures, loggers);
            var runSettings = new RunSettings(settings.MaxIterations, systemPrompt, settings.Verbose);
            var graders = rigs.ToDictionary(r => r.Mode, r => new Grader(fixtures.Servers, r.Catalog));
            var total = models.Count * rigs.Count * settings.Runs * tasks.Count;

            await AnsiConsole.Progress().StartAsync(async progress =>
            {
                var bar = progress.AddTask("runs", maxValue: total);
                bar.Increment(done.Count);
                // Modes are interleaved within each run, so drift in a provider over the session
                // doesn't land on one mode.
                foreach (var model in models)
                {
                    for (var run = 1; run <= settings.Runs; run++)
                    {
                        foreach (var task in tasks)
                        {
                            foreach (var rig in rigs)
                            {
                                ct.ThrowIfCancellationRequested();
                                if (done.Contains((model.Label, rig.Mode.ToString(), task.Id, run)))
                                    continue;

                                bar.Description = $"{model.Label} {rig.Mode} {task.Id} #{run}";
                                var rows = await runner.RunAsync(rig, graders[rig.Mode], model, task, run, runSettings, ct);

                                // A rate limit is the provider's, not the model's: wait it out and
                                // run the task again rather than record it.
                                for (var attempt = 1; attempt <= MaxRateLimitRetries && rows.Any(IsRateLimited); attempt++)
                                {
                                    bar.Description = $"{model.Label} {rig.Mode} {task.Id} #{run} (429, retry {attempt})";
                                    await Task.Delay(TimeSpan.FromSeconds(20 * attempt), ct);
                                    rows = await runner.RunAsync(rig, graders[rig.Mode], model, task, run, runSettings, ct);
                                }

                                await Report.AppendAsync(resultsPath, rows, ct);
                                bar.Increment(1);
                            }
                        }
                    }
                }
            });

            var header = $"# MCP wrapper modes — {DateTime.Now:yyyy-MM-dd}\n\n" +
                         $"{settings.Runs} run(s) × {tasks.Count} task(s) per model and mode; prompt: {settings.Prompt}; " +
                         $"max iterations: {settings.MaxIterations}. Models: " +
                         string.Join(", ", models.Select(m => $"{m.Label} = {m.ModelId}")) + ".";
            var summary = Report.Summarize(Report.Read(resultsPath), header);
            await File.WriteAllTextAsync(Path.Combine(outDir, "summary.md"), summary, ct);

            AnsiConsole.WriteLine(summary);
            AnsiConsole.MarkupLineInterpolated($"[green]Wrote {resultsPath} and summary.md[/]");
            return 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            AnsiConsole.MarkupLine("[yellow]Cancelled.[/]");
            return 130;
        }
        finally
        {
            foreach (var rig in rigs)
                await rig.DisposeAsync();
        }
    }

    private const int MaxRateLimitRetries = 5;

    private static bool IsRateLimited(TurnResult row) =>
        row.Error?.Contains("429", StringComparison.Ordinal) == true;

    /// <summary>
    /// --resume: keeps the task runs in <paramref name="resultsPath"/> whose every turn finished
    /// without an error, drops the rest from the file, and returns the kept ones' keys.
    /// </summary>
    private static async Task<HashSet<(string Model, string Mode, string Task, int Run)>> KeepCompletedAsync(
        string resultsPath, CancellationToken ct)
    {
        if (!File.Exists(resultsPath))
            return [];

        var rows = Report.Read(resultsPath);
        var byRun = rows.GroupBy(r => (r.Model, r.Mode, Task: r.Task.Split('#')[0], r.Run)).ToList();
        var complete = byRun
            .Where(g => g.All(r => r.Error is null)
                        && g.Count() == MeasureTasks.All.Single(t => t.Id == g.Key.Task).Turns.Count)
            .ToList();

        File.Delete(resultsPath);
        await Report.AppendAsync(resultsPath, complete.SelectMany(g => g), ct);
        AnsiConsole.MarkupLineInterpolated(
            $"Resuming: kept {complete.Count} completed task run(s), dropped {byRun.Count - complete.Count}.");
        return [.. complete.Select(g => g.Key)];
    }

    private static List<ModelUnderTest> BuildModels(Settings settings, MeasureOptions options, ILoggerFactory loggers)
    {
        var names = settings.Models?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    ?? options.DefaultModels;
        if (names.Length == 0)
            throw new InvalidOperationException("No models: pass --models or set Measure:DefaultModels.");

        // The agent's own ModelBehaviors (appsettings.json and model-behaviors/, copied with the
        // agent project) decide native vs text-based tool calling and the extra prompt, as in production.
        var agentConfig = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
            .Build();
        var behaviors = new ServiceCollection()
            .AddSingleton(loggers)
            .AddLogging()
            .AddModelBehaviors(o =>
            {
                agentConfig.GetSection("ModelBehaviors").Bind(o);
                o.BasePath = Path.Combine(AppContext.BaseDirectory, "model-behaviors");
            })
            .BuildServiceProvider()
            .GetRequiredService<IModelBehaviorProvider>();

        var models = new List<ModelUnderTest>();
        foreach (var name in names)
        {
            if (!options.Models.TryGetValue(name, out var model))
                throw new InvalidOperationException($"Unknown model '{name}'. Configured: {string.Join(", ", options.Models.Keys)}.");
            if (!options.Providers.TryGetValue(model.Provider, out var provider)
                || string.IsNullOrWhiteSpace(provider.Endpoint) || string.IsNullOrWhiteSpace(provider.ApiKey))
                throw new InvalidOperationException(
                    $"Model '{name}' needs Measure:Providers:{model.Provider}:Endpoint and :ApiKey " +
                    $"(user-secrets, or env Measure__Providers__{model.Provider}__ApiKey).");

            var client = new OpenAIClient(new ApiKeyCredential(provider.ApiKey),
                    new OpenAIClientOptions { Endpoint = new Uri(provider.Endpoint), NetworkTimeout = TimeSpan.FromMinutes(5) })
                .GetChatClient(model.ModelId)
                .AsIChatClient();
            models.Add(new ModelUnderTest(name, model.Tier, model.ModelId, client, behaviors.GetBehavior(model.ModelId)));
        }
        return models;
    }

    private static void PrintPlan(
        Settings settings, List<ModelUnderTest> models, List<MeasureRig> rigs, IReadOnlyList<MeasureTask> tasks,
        int turnsPerRun, string systemPrompt)
    {
        var table = new Table().AddColumns("Mode", "Tools offered at start", "Tool schema chars", "≈ baseline tokens / request");
        foreach (var rig in rigs)
        {
            var tools = rig.Registry.GetTools()
                .Where(t => rig.Mode == McpWrapperMode.Eager || t.DownstreamName is null)
                .Where(t => rig.Mode is McpWrapperMode.Lazy or McpWrapperMode.Pinned || t.Name != McpTypedToolSurface.FindToolsName)
                .ToList();
            var schemaChars = tools.Sum(t => t.Name.Length + (t.Description?.Length ?? 0) + (t.ParametersSchema?.Length ?? 0));
            table.AddRow(rig.Mode.ToString(), tools.Count.ToString(CultureInfo.InvariantCulture),
                schemaChars.ToString("N0", CultureInfo.InvariantCulture),
                ((schemaChars + systemPrompt.Length) / 4).ToString("N0", CultureInfo.InvariantCulture));
        }

        var turns = models.Count * rigs.Count * settings.Runs * turnsPerRun;
        AnsiConsole.MarkupLineInterpolated(
            $"[bold]{models.Count} model(s) × {rigs.Count} mode(s) × {settings.Runs} run(s) × {tasks.Count} task(s) ({turnsPerRun} turn(s)) = {turns} turns[/]");
        AnsiConsole.MarkupLineInterpolated($"Models: {string.Join(", ", models.Select(m => $"{m.Label}={m.ModelId}"))}");
        AnsiConsole.MarkupLineInterpolated($"System prompt: {settings.Prompt} ({systemPrompt.Length:N0} chars)");
        AnsiConsole.Write(table);
    }
}
