using System.Text;
using RockBot.Tools.Mcp;

namespace RockBot.Tools.Tests;

[TestClass]
public class McpBinaryPayloadTests
{
    /// <summary>Smallest valid PNG — starts with 0x89, which is not ASCII and never base64.</summary>
    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [TestMethod]
    public void Decode_Base64TextPayload_ReturnsDecodedBytes()
    {
        // What SDK 1.4.0 actually puts in ImageContentBlock.Data: the wire field verbatim,
        // which is base64 text rather than the file's bytes.
        var data = Encoding.ASCII.GetBytes(Convert.ToBase64String(PngBytes));

        CollectionAssert.AreEqual(PngBytes, McpBinaryPayload.Decode(data));
    }

    [TestMethod]
    public void Decode_RawBinaryPayload_IsPassedThrough()
    {
        // What the property's type says it holds. Both readings work, so an SDK that changes
        // its mind needs no change here.
        CollectionAssert.AreEqual(PngBytes, McpBinaryPayload.Decode(PngBytes));
    }

    [TestMethod]
    public void Decode_EmptyPayload_ReturnsEmpty()
    {
        Assert.AreEqual(0, McpBinaryPayload.Decode(ReadOnlyMemory<byte>.Empty).Length);
    }

    [TestMethod]
    public void Decode_AsciiTextThatIsNotBase64_IsPassedThrough()
    {
        var text = "hello, world!"u8.ToArray();

        CollectionAssert.AreEqual(text, McpBinaryPayload.Decode(text));
    }

    [TestMethod]
    public void ToBase64_Base64TextPayload_IsNotDoubleEncoded()
    {
        var expected = Convert.ToBase64String(PngBytes);
        var data = Encoding.ASCII.GetBytes(expected);

        Assert.AreEqual(expected, McpBinaryPayload.ToBase64(data));
    }

    [TestMethod]
    public void ToBase64_RawBinaryPayload_IsEncodedOnce()
    {
        Assert.AreEqual(Convert.ToBase64String(PngBytes), McpBinaryPayload.ToBase64(PngBytes));
    }

    [TestMethod]
    public void ToBase64_RoundTripsThroughDecode()
    {
        var wireShape = Encoding.ASCII.GetBytes(Convert.ToBase64String(PngBytes));

        var base64 = McpBinaryPayload.ToBase64(wireShape);

        CollectionAssert.AreEqual(PngBytes, Convert.FromBase64String(base64));
    }
}
