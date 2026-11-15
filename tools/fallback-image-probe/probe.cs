#:project ../../src/RockBot.Llm/RockBot.Llm.csproj
#:project ../../src/RockBot.Llm.Anthropic/RockBot.Llm.Anthropic.csproj
#:package Microsoft.Extensions.AI.OpenAI@10.3.0
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true

// Live check of FallbackChatClient's image routing (#818) against real providers.
// See README.md. Makes three or four small paid calls; never runs in CI.
//
// Chain under test:
//   [0] Anthropic API, deliberately invalid key, declares images -> must fail (401) and be passed over
//   [1] OpenAI-compatible model, flagged text-only               -> must NOT be called for the image
//   [2] Anthropic Messages API on OpenRouter, declares images    -> must answer the image
// Then a text request on the same chain, which [1] must answer.
//
// [1] is best a model that CAN see: if the skip were broken it would answer the image, and the
// check below would catch it. A genuinely blind model would 400 instead, which hides the bug.
using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using RockBot.Llm;
using RockBot.Llm.Anthropic;

var openRouterKey = Env("PROBE_OPENROUTER_KEY");
var textOnlyKey = Env("PROBE_TEXTONLY_KEY");
var textOnlyEndpoint = Env("PROBE_TEXTONLY_ENDPOINT");
var anthropicModel = Env("PROBE_ANTHROPIC_MODEL", "claude-haiku-5-5");
var textOnlyModel = Env("PROBE_TEXTONLY_MODEL", "gpt-5.6-terra");
var openRouterModel = Env("PROBE_OPENROUTER_MODEL", "anthropic/claude-haiku-5.5");
var imagePath = args.Length > 0 ? args[0] : Path.Combine(AppContext.GetData("EntryPointFileDirectoryPath") as string ?? ".", "vision-test.png");

// What the test image says. A reply containing it proves the model read the pixels.
const string ImageCode = "7Q4X";

var anthropicOptions = new AnthropicProviderOptions();
var broken = new Tracing($"{anthropicModel} (Anthropic API, invalid key)", AnthropicChatClientFactory.Create(
    "sk-ant-api03-deliberately-invalid-probe-key", anthropicModel, null, null, anthropicOptions, sdkRetries: false));
var textOnly = new Tracing($"{textOnlyModel} (flagged text-only)",
    new OpenAIClient(new ApiKeyCredential(textOnlyKey), new OpenAIClientOptions { Endpoint = new Uri(textOnlyEndpoint) })
        .GetChatClient(textOnlyModel).AsIChatClient());
var openRouter = new Tracing($"{openRouterModel} (OpenRouter)", AnthropicChatClientFactory.Create(
    openRouterKey, openRouterModel, "https://openrouter.ai/api/v1", null, anthropicOptions, sdkRetries: false));

using var chain = new FallbackChatClient(
    [(anthropicModel, broken), (textOnlyModel, textOnly), (openRouterModel, openRouter)],
    new ConsoleLog(),
    retryDelay: TimeSpan.Zero,
    maxRetries: 0,
    supportsImageInput: [true, false, true]);
chain.OnFallback += (from, to, why) => Console.WriteLine($"  [OnFallback] {from} -> {to} ({why})");

var failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}");
    if (!ok) failures++;
}

Console.WriteLine("=== 1. image request ===");
var image = await chain.GetResponseAsync(
[
    new ChatMessage(ChatRole.User,
    [
        new TextContent("List each shape with its colour, then read any text verbatim. Be brief."),
        new DataContent(await File.ReadAllBytesAsync(imagePath), "image/png")
    ])
], new ChatOptions { MaxOutputTokens = 200 });
Console.WriteLine($"  answered by: {image.ModelId}");
Console.WriteLine($"  reply: {image.Text.ReplaceLineEndings(" | ")}");
Check(broken.Calls == 1, "the broken primary was tried once and failed over");
Check(textOnly.Calls == 0, "the text-only model was skipped for the image");
Check(openRouter.Calls == 1, "the image went to the next model that declares image input");
Check(image.Text.Contains(ImageCode, StringComparison.OrdinalIgnoreCase), $"the reply read the image (contains {ImageCode})");

Console.WriteLine("=== 2. text request on the same chain ===");
var text = await chain.GetResponseAsync(
    [new ChatMessage(ChatRole.User, "Reply with exactly: text-ok")],
    new ChatOptions { MaxOutputTokens = 50 });
Console.WriteLine($"  answered by: {text.ModelId}");
Console.WriteLine($"  reply: {text.Text}");
Check(textOnly.Calls == 1, "the text-only model answered the text request");
Check(openRouter.Calls == 1, "the text request did not need the last model");

Console.WriteLine(failures == 0 ? "=== all checks passed ===" : $"=== {failures} check(s) FAILED ===");
return failures == 0 ? 0 : 1;

static string Env(string name, string? fallback = null) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v
    : fallback ?? throw new InvalidOperationException($"{name} is not set. See README.md.");

/// <summary>Counts and announces each call that reaches a chain entry.</summary>
sealed class Tracing(string name, IChatClient inner) : DelegatingChatClient(inner)
{
    public int Calls { get; private set; }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        Console.WriteLine($"  -> calling {name}");
        return base.GetResponseAsync(messages, options, cancellationToken);
    }
}

/// <summary>Shows FallbackChatClient's own log lines, which name each skip and failover.</summary>
sealed class ConsoleLog : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        Console.WriteLine($"  [{logLevel}] {(message.Length > 260 ? message[..260] + "…" : message)}");
    }
}
