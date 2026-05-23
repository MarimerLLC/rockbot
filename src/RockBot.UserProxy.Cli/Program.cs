using RockBot.UserProxy.Cli;
using RockBot.UserProxy.Cli.Auth;
using Spectre.Console.Cli;

var app = new CommandApp<ChatCommand>();

app.Configure(config =>
{
    config.SetApplicationName("rockbot");

    config.AddCommand<ChatCommand>("chat")
        .WithDescription("Interactive chat (default), or one-shot with --message <TEXT>.")
        .WithExample(["chat", "--message", "hello"])
        .WithExample(["chat", "--rabbitmq-host", "localhost"]);

    config.AddCommand<InfoCommand>("info")
        .WithDescription("Print the agent's name and version.");

    config.AddCommand<StatusCommand>("status")
        .WithDescription("Show the agent's current activity (processing, subagents).");

    config.AddCommand<HistoryCommand>("history")
        .WithDescription("Print stored conversation history for a session.")
        .WithExample(["history", "--session", "cli-session"]);

    config.AddCommand<ClearCommand>("clear")
        .WithDescription("Reset the agent's in-memory conversation context for a session.");

    config.AddCommand<CancelCommand>("cancel")
        .WithDescription("Ask the agent to cancel in-flight work for a session.");

    config.AddBranch("auth", auth =>
    {
        auth.SetDescription("Authentication flows that connect the agent to external services.");
        auth.AddCommand<WorkIqAuthCommand>("workiq")
            .WithDescription("Sign in to Microsoft 365 (Work IQ) via device code and publish the cache to the agent.");
    });
});

return await app.RunAsync(args);
