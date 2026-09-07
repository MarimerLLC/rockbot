using System.Buffers.Binary;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Agent.McpBridge.Attachments;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.UserProxy;

namespace RockBot.Agent.Tests.Attachments;

/// <summary>
/// Walks a real file the whole way from a client's upload to the content part the model
/// receives, with the real <see cref="AttachmentStorage"/>, the real
/// <see cref="AttachmentUploadHandler"/>, the real envelope serialisation, and the real
/// <see cref="InboundAttachmentInjector"/> — no stubs in the data path.
/// </summary>
/// <remarks>
/// The per-piece tests each pin one rule. This pins that the pieces actually connect: a byte
/// array handed in at the client end comes out the other end, unchanged, as image content on a
/// user message. The seams it covers are exactly the ones that would fail silently — a byte[]
/// mangled by JSON serialisation on the way through the envelope, a path reference that does not
/// resolve back to the file that was written, or a MIME type that survives the write but not the
/// round trip.
/// </remarks>
[TestClass]
public class InboundAttachmentEndToEndTests
{
    private string _root = null!;
    private AttachmentStorage _storage = null!;
    private InboundAttachmentService _service = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "rockbot-attach-e2e", Guid.NewGuid().ToString("N"));
        _storage = new AttachmentStorage(_root);
        _service = new InboundAttachmentService(_storage, NullLogger<InboundAttachmentService>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public async Task Screenshot_TravelsFromUploadToImageContentUnchanged()
    {
        // A 1.4 MB "screenshot": a real 1920×1080 PNG header over enough bytes to be a plausible
        // payload, so both the size cap and the envelope's base64 path are genuinely exercised.
        var original = Screenshot(1920, 1080, totalBytes: 1_400_000);

        // ── 1. The client's request survives the wire ────────────────────────
        var request = new AttachmentUploadRequest
        {
            FileName = "screenshot.png",
            Mime = "image/png",
            Data = original,
            SessionId = "sess-e2e",
            UserId = "user-e2e",
        };

        var envelope = request.ToEnvelope<AttachmentUploadRequest>(source: "blazor-e2e");
        var received = envelope.GetPayload<AttachmentUploadRequest>();

        Assert.IsNotNull(received);
        CollectionAssert.AreEqual(original, received!.Data,
            "Bytes must survive the envelope round trip — this is the one message that carries them.");

        // ── 2. The agent writes it and answers with a path reference ─────────
        var response = await _service.StoreAsync(
            received.FileName, received.Mime, received.Data, received.SessionId, default);

        Assert.IsTrue(response.Success, response.Error);
        var attachment = response.Attachment!;
        Assert.AreEqual("image/png", attachment.Mime);
        Assert.AreEqual("screenshot.png", attachment.FileName);
        Assert.IsFalse(Path.IsPathRooted(attachment.Path),
            "The wire carries a relative reference, never a filesystem path.");

        // ── 3. The reference resolves back to the file that was written ──────
        var onDisk = await _storage.ReadAsync(attachment.Path, default);
        CollectionAssert.AreEqual(original, onDisk,
            "The path reference must resolve to the exact bytes the user uploaded.");

        // ── 4. The message the user sends carries only the reference ─────────
        var message = new UserMessage
        {
            Content = "what is in this picture?",
            SessionId = "sess-e2e",
            UserId = "user-e2e",
            ClientCapabilities = ClientCapabilityPresets.Blazor,
            ChannelName = "blazor",
            Attachments = [attachment],
        };

        var messageEnvelope = message.ToEnvelope<UserMessage>(source: "blazor-e2e");
        var receivedMessage = messageEnvelope.GetPayload<UserMessage>()!;

        Assert.AreEqual(1, receivedMessage.Attachments!.Count);
        Assert.IsTrue(messageEnvelope.Body.Length < 4_000,
            $"The conversation message must stay small — it carries a reference, not 1.4 MB of " +
            $"image (was {messageEnvelope.Body.Length:N0} bytes).");

        // ── 5. The model is handed the actual image ──────────────────────────
        var chatMessages = new List<ChatMessage>
        {
            new(ChatRole.System, "system prompt"),
            new(ChatRole.User, receivedMessage.Content),
        };

        await InboundAttachmentInjector.InjectAsync(
            chatMessages,
            [.. receivedMessage.Attachments.Select(a => new ConversationTurnAttachment(a.Mime, a.Path, a.FileName))],
            tierCanSee: true,
            readBytesAsync: _storage.ReadAsync,
            NullLogger.Instance);

        var data = chatMessages[^1].Contents.OfType<DataContent>().Single();
        Assert.AreEqual("image/png", data.MediaType);
        CollectionAssert.AreEqual(original, data.Data.ToArray(),
            "The bytes the user picked are the bytes the model sees.");
    }

    [TestMethod]
    public async Task Screenshot_OnABlindTier_ReachesTheModelAsAPathItCanAnalyse()
    {
        // The same journey with a model that cannot see. The file must still be written and still
        // be reachable — the failure mode this replaced was the file vanishing without a word.
        var response = await _service.StoreAsync(
            "whiteboard.jpg", "image/jpeg", [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3], "sess-e2e", default);

        Assert.IsTrue(response.Success, response.Error);

        var chatMessages = new List<ChatMessage>
        {
            new(ChatRole.System, "system prompt"),
            new(ChatRole.User, "what did we write on the board?"),
        };

        await InboundAttachmentInjector.InjectAsync(
            chatMessages,
            [new ConversationTurnAttachment(response.Attachment!.Mime, response.Attachment.Path, "whiteboard.jpg")],
            tierCanSee: false,
            readBytesAsync: _storage.ReadAsync,
            NullLogger.Instance);

        Assert.IsFalse(chatMessages[^1].Contents.OfType<DataContent>().Any(),
            "A blind model must not be sent bytes — the provider rejects the whole request.");

        var marker = chatMessages[^1].Contents.OfType<TextContent>().Last().Text!;
        StringAssert.Contains(marker, "whiteboard.jpg");
        StringAssert.Contains(marker, "analyze_file");

        // And the path named in that marker is real: analyze_file resolves it the same way.
        var named = $"attachments/{response.Attachment.Path}";
        StringAssert.Contains(marker, named);
        Assert.IsTrue(File.Exists(Path.Combine(_root, response.Attachment.Path)),
            "The path the model is told to analyse must actually exist.");
    }

    /// <summary>
    /// A valid PNG header of the given pixel size, padded to <paramref name="totalBytes"/>. Real
    /// dimensions matter because the context estimate prices an image from its pixels (#564).
    /// </summary>
    private static byte[] Screenshot(int width, int height, int totalBytes)
    {
        var bytes = new byte[totalBytes];
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);

        // Fill the rest so the payload is not a megabyte of zeroes that compresses to nothing on
        // the wire and hides a serialisation problem.
        for (var i = 24; i < bytes.Length; i++)
            bytes[i] = (byte)(i * 31 % 251);

        return bytes;
    }
}
