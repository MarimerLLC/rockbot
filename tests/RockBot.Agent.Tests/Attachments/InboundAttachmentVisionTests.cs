using System.IO.Compression;
using System.Buffers.Binary;
using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI;
using RockBot.Agent.McpBridge.Attachments;
using RockBot.Host;
using RockBot.UserProxy;

namespace RockBot.Agent.Tests.Attachments;

/// <summary>
/// Sends a real image, uploaded the way a person would, to a real vision model — and checks that
/// the model describes what is actually in it.
/// </summary>
/// <remarks>
/// Every other test in this area proves a mechanism: bytes survive a transport, a content part
/// gets appended, a rule rejects a file. None of them proves the thing that actually matters,
/// which is that the message shape RockBot builds is one a provider accepts and a model
/// understands. A `DataContent` on a user message is easy to construct and easy to get subtly
/// wrong — wrong media type, wrong part ordering, bytes the provider silently ignores — and every
/// one of those failures looks like success right up until the model answers about nothing.
///
/// <para>The image is generated here rather than committed: four solid quadrants in colours no
/// model would guess by chance, so a pass means it read the pixels. Set
/// <c>ROCKBOT_VISION_ENDPOINT</c>, <c>ROCKBOT_VISION_KEY</c> and <c>ROCKBOT_VISION_MODEL</c> to
/// enable; skipped otherwise, and it costs a single small request when it runs.</para>
/// </remarks>
[TestClass]
public class InboundAttachmentVisionTests
{
    private static readonly string? Endpoint = Environment.GetEnvironmentVariable("ROCKBOT_VISION_ENDPOINT");
    private static readonly string? ApiKey = Environment.GetEnvironmentVariable("ROCKBOT_VISION_KEY");
    private static readonly string? Model = Environment.GetEnvironmentVariable("ROCKBOT_VISION_MODEL");

    [TestMethod]
    [Timeout(180_000)]
    public async Task AnUploadedImage_IsActuallySeenByTheModel()
    {
        if (string.IsNullOrEmpty(Endpoint) || string.IsNullOrEmpty(ApiKey) || string.IsNullOrEmpty(Model))
        {
            Assert.Inconclusive(
                "Vision tier not configured (set ROCKBOT_VISION_ENDPOINT, ROCKBOT_VISION_KEY, ROCKBOT_VISION_MODEL)");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "rockbot-vision", Guid.NewGuid().ToString("N"));
        var storage = new AttachmentStorage(root);
        var service = new InboundAttachmentService(storage, NullLogger<InboundAttachmentService>.Instance);

        try
        {
            // ── Upload it exactly as a client would ──────────────────────────
            var png = QuadrantPng();
            var upload = await service.StoreAsync("quadrants.png", "image/png", png, "sess-vision", default);
            Assert.IsTrue(upload.Success, upload.Error);

            // ── Build the context the way the agent does ─────────────────────
            var chatMessages = new List<ChatMessage>
            {
                new(ChatRole.System, "Answer only from what is visibly present in the image."),
                new(ChatRole.User,
                    "This image is split into four equal quadrants. Name the colour of each one: " +
                    "top-left, top-right, bottom-left, bottom-right. Answer with just the four colours."),
            };

            await InboundAttachmentInjector.InjectAsync(
                chatMessages,
                [new ConversationTurnAttachment(upload.Attachment!.Mime, upload.Attachment.Path, "quadrants.png")],
                tierCanSee: true,
                readBytesAsync: storage.ReadAsync,
                NullLogger.Instance);

            Assert.IsTrue(chatMessages[^1].Contents.OfType<DataContent>().Any(),
                "Fixture check: the image must be on the message before it is worth calling a model.");

            // ── Same client construction the agent uses for an OpenAI-compatible tier ──
            var client = new OpenAIClient(
                    new ApiKeyCredential(ApiKey!),
                    new OpenAIClientOptions { Endpoint = new Uri(Endpoint!) })
                .GetChatClient(Model!)
                .AsIChatClient();

            var response = await client.GetResponseAsync(chatMessages);
            var text = response.Text ?? string.Empty;

            Console.WriteLine($"Model ({Model}) answered: {text}");

            // The quadrants are red, blue, green, yellow — clockwise from top-left. A model that
            // ignored the image cannot name four specific colours in the right places.
            foreach (var colour in new[] { "red", "blue", "green", "yellow" })
            {
                StringAssert.Contains(text.ToLowerInvariant(), colour,
                    $"The model did not mention {colour}. Full answer: {text}");
            }

            var lower = text.ToLowerInvariant();
            Assert.IsTrue(lower.IndexOf("red", StringComparison.Ordinal) < lower.IndexOf("yellow", StringComparison.Ordinal),
                $"Colours should be named in quadrant order (red first, yellow last). Answer: {text}");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A real 256×256 PNG: red top-left, blue top-right, green bottom-left, yellow bottom-right.
    /// Written by hand rather than committed as a binary so the fixture is readable and so the
    /// bytes are unambiguously a valid image rather than a header over noise.
    /// </summary>
    internal static byte[] QuadrantPng(int size = 256)
    {
        var half = size / 2;
        // Raw scanlines: one filter byte (0 = none) then RGB per pixel.
        var raw = new byte[size * (1 + size * 3)];
        var o = 0;
        for (var y = 0; y < size; y++)
        {
            raw[o++] = 0;
            for (var x = 0; x < size; x++)
            {
                var (r, g, b) = (x < half, y < half) switch
                {
                    (true, true) => ((byte)220, (byte)20, (byte)20),    // red
                    (false, true) => ((byte)20, (byte)20, (byte)220),   // blue
                    (true, false) => ((byte)20, (byte)180, (byte)20),   // green
                    (false, false) => ((byte)240, (byte)220, (byte)20), // yellow
                };
                raw[o++] = r; raw[o++] = g; raw[o++] = b;
            }
        }

        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(raw, 0, raw.Length);

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), size);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), size);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 2;  // colour type: truecolour
        WriteChunk(png, "IHDR", ihdr);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);

        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        var crc = Crc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var chunk in new[] { type, data })
        {
            foreach (var b in chunk)
            {
                crc ^= b;
                for (var i = 0; i < 8; i++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
