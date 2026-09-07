using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;

namespace RockBot.Tools.FileSystem.Tests;

[TestClass]
public class AnalyzeFileToolExecutorTests
{
    private string _root = null!;
    private FileSystemOptions _options = null!;
    private RecordingLlmClient _llm = null!;

    /// <summary>
    /// A one-pixel PNG is enough: nothing under test decodes the image, and the bytes are only
    /// checked for being passed through intact.
    /// </summary>
    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "rockbot-analyze-file-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _options = new FileSystemOptions { BasePath = _root };
        _llm = new RecordingLlmClient();
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private AnalyzeFileToolExecutor Executor(params ModelTier[] visionTiers) =>
        new(_options, _llm,
            visionTiers.Length > 0 ? visionTiers : [ModelTier.Balanced],
            NullLogger.Instance);

    private string WriteBinary(string relativePath, byte[] content)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    private static ToolInvokeRequest Request(object args) => new()
    {
        ToolCallId = "call_1",
        ToolName = "analyze_file",
        Arguments = JsonSerializer.Serialize(args)
    };

    [TestMethod]
    public async Task Execute_MissingPath_ReturnsError()
    {
        var result = await Executor().ExecuteAsync(Request(new { prompt = "What is this?" }), default);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content!, "path");
    }

    [TestMethod]
    public async Task Execute_MissingPrompt_ReturnsError()
    {
        WriteBinary("diagram.png", PngBytes);

        var result = await Executor().ExecuteAsync(Request(new { path = "diagram.png" }), default);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content!, "prompt");
    }

    [TestMethod]
    public async Task Execute_PathEscapingBase_ReturnsError()
    {
        var result = await Executor().ExecuteAsync(
            Request(new { path = "../../etc/passwd.png", prompt = "What is this?" }), default);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content!, "within the shared volume");
        Assert.AreEqual(0, _llm.Calls.Count, "no LLM call should be made for a rejected path");
    }

    [TestMethod]
    public async Task Execute_MissingFile_ReturnsError()
    {
        var result = await Executor().ExecuteAsync(
            Request(new { path = "absent.png", prompt = "What is this?" }), default);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content!, "not found");
    }

    [TestMethod]
    public async Task Execute_UnrecognisedExtension_ReturnsError()
    {
        WriteBinary("notes.txt", "plain text"u8.ToArray());

        var result = await Executor().ExecuteAsync(
            Request(new { path = "notes.txt", prompt = "What is this?" }), default);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content!, "unrecognised file type");
        Assert.AreEqual(0, _llm.Calls.Count);
    }

    [TestMethod]
    public async Task Execute_KnownTypeNotOnAllowlist_ReturnsError()
    {
        WriteBinary("report.pdf", PngBytes);

        var result = await Executor().ExecuteAsync(
            Request(new { path = "report.pdf", prompt = "Summarise it." }), default);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content!, "application/pdf");
        Assert.AreEqual(0, _llm.Calls.Count);
    }

    [TestMethod]
    public async Task Execute_TypeAddedToAllowlist_IsAccepted()
    {
        _options.AnalyzeFileMimeTypes = [.. _options.AnalyzeFileMimeTypes, "application/pdf"];
        WriteBinary("report.pdf", PngBytes);

        var result = await Executor().ExecuteAsync(
            Request(new { path = "report.pdf", prompt = "Summarise it." }), default);

        Assert.IsFalse(result.IsError, result.Content);
        Assert.AreEqual("application/pdf", _llm.Calls[0].Data!.MediaType);
    }

    [TestMethod]
    public async Task Execute_FileOverSizeLimit_ReturnsErrorBeforeCallingModel()
    {
        _options.AnalyzeFileMaxBytes = 10;
        WriteBinary("big.png", PngBytes);

        var result = await Executor().ExecuteAsync(
            Request(new { path = "big.png", prompt = "What is this?" }), default);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content!, "exceeds");
        Assert.AreEqual(0, _llm.Calls.Count);
    }

    [TestMethod]
    public async Task Execute_ValidImage_SendsPromptAndBytesAsMultimodalContent()
    {
        WriteBinary("diagrams/arch.png", PngBytes);
        _llm.ResponseText = "Three services arranged left to right.";

        var result = await Executor().ExecuteAsync(
            Request(new { path = "diagrams/arch.png", prompt = "Describe the components." }), default);

        Assert.IsFalse(result.IsError, result.Content);
        Assert.AreEqual("Three services arranged left to right.", result.Content);

        var call = _llm.Calls.Single();
        Assert.AreEqual("Describe the components.", call.Text);
        Assert.AreEqual("image/png", call.Data!.MediaType);
        CollectionAssert.AreEqual(PngBytes, call.Data.Data.ToArray());
    }

    [TestMethod]
    public async Task Execute_JpegExtensions_MapToJpegMime()
    {
        WriteBinary("photo.jpeg", PngBytes);

        var result = await Executor().ExecuteAsync(
            Request(new { path = "photo.jpeg", prompt = "What is this?" }), default);

        Assert.IsFalse(result.IsError, result.Content);
        Assert.AreEqual("image/jpeg", _llm.Calls[0].Data!.MediaType);
    }

    [TestMethod]
    public async Task Execute_RequestedTierSupportsImages_UsesIt()
    {
        WriteBinary("diagram.png", PngBytes);

        var result = await Executor(ModelTier.Balanced, ModelTier.High).ExecuteAsync(
            Request(new { path = "diagram.png", prompt = "What is this?", tier = "high" }), default);

        Assert.IsFalse(result.IsError, result.Content);
        Assert.AreEqual(ModelTier.High, _llm.Calls[0].Tier);
    }

    [TestMethod]
    public async Task Execute_RequestedTierCannotSee_SubstitutesACapableTier()
    {
        WriteBinary("diagram.png", PngBytes);

        var result = await Executor(ModelTier.High).ExecuteAsync(
            Request(new { path = "diagram.png", prompt = "What is this?", tier = "low" }), default);

        Assert.IsFalse(result.IsError, result.Content);
        Assert.AreEqual(ModelTier.High, _llm.Calls[0].Tier);
    }

    [TestMethod]
    public async Task Execute_NoTierRequested_DefaultsToBalancedWhenItCanSee()
    {
        WriteBinary("diagram.png", PngBytes);

        var result = await Executor(ModelTier.Low, ModelTier.Balanced, ModelTier.High).ExecuteAsync(
            Request(new { path = "diagram.png", prompt = "What is this?" }), default);

        Assert.IsFalse(result.IsError, result.Content);
        Assert.AreEqual(ModelTier.Balanced, _llm.Calls[0].Tier);
    }

    [TestMethod]
    public async Task Execute_ModelReturnsNoText_ReturnsError()
    {
        WriteBinary("diagram.png", PngBytes);
        _llm.ResponseText = "";

        var result = await Executor().ExecuteAsync(
            Request(new { path = "diagram.png", prompt = "What is this?" }), default);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content!, "no text");
    }

    [TestMethod]
    public async Task Execute_ModelThrows_ReturnsErrorRatherThanPropagating()
    {
        WriteBinary("diagram.png", PngBytes);
        _llm.Throw = new InvalidOperationException("provider said no");

        var result = await Executor().ExecuteAsync(
            Request(new { path = "diagram.png", prompt = "What is this?" }), default);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content!, "provider said no");
    }

    private sealed record RecordedCall(ModelTier Tier, string? Text, DataContent? Data);

    private sealed class RecordingLlmClient : ILlmClient
    {
        public List<RecordedCall> Calls { get; } = [];
        public string ResponseText { get; set; } = "ok";
        public Exception? Throw { get; set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            CancellationToken cancellationToken)
            => GetResponseAsync(messages, ModelTier.Balanced, options, cancellationToken);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ModelTier tier,
            ChatOptions? options,
            CancellationToken cancellationToken)
        {
            if (Throw is not null)
                throw Throw;

            var userMessage = messages.Last(m => m.Role == ChatRole.User);
            Calls.Add(new RecordedCall(
                tier,
                userMessage.Contents.OfType<TextContent>().FirstOrDefault()?.Text,
                userMessage.Contents.OfType<DataContent>().FirstOrDefault()));

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, ResponseText)));
        }
    }
}
