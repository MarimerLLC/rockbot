using Spectre.Console.Cli;

namespace RockBot.Cli.Tests;

/// <summary>A Spectre <see cref="CommandContext"/> for calling a command's <c>ExecuteAsync</c> directly.</summary>
internal static class TestCommandContext
{
    public static CommandContext Create(string name = "chat") =>
        new([], new NoRemainingArguments(), name: name, data: null);

    private sealed class NoRemainingArguments : IRemainingArguments
    {
        public ILookup<string, string?> Parsed { get; } =
            Array.Empty<KeyValuePair<string, string?>>().ToLookup(p => p.Key, p => p.Value);

        public IReadOnlyList<string> Raw { get; } = [];
    }
}
