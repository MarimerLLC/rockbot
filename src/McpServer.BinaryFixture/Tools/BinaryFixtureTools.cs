using System.ComponentModel;
using System.Text;
using System.Text.Json;
using McpServer.BinaryFixture.Fixtures;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServer.BinaryFixture.Tools;

/// <summary>
/// Every shape an MCP server can hand binary content back in, on demand.
/// </summary>
/// <remarks>
/// <para>
/// The bridge's binary capture and the <c>analyze_file</c> tool are covered by unit tests, but
/// nothing in a normal deployment returns the shapes they exist for — the servers RockBot talks
/// to either write files to disk or corrupt their bytes. That leaves the interesting paths
/// verifiable only in isolation, which is how the mangled-binary case (issue #513) went
/// unnoticed until it was smoke-tested against a live server.
/// </para>
/// <para>
/// This server closes that gap. Each tool returns one deliberate shape, so a live run can prove
/// what capture does with it end to end. The payloads are generated in code and their content is
/// documented in <see cref="TestMedia"/>, so a description produced by a vision model can be
/// checked against a known answer rather than against an impression of a photo.
/// </para>
/// </remarks>
[McpServerToolType]
public sealed class BinaryFixtureTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    /// <summary>
    /// Wire-format shim for the MCP SDK's content-block payloads.
    /// </summary>
    /// <remarks>
    /// <c>ImageContentBlock.Data</c> is a <c>ReadOnlyMemory&lt;byte&gt;</c>, which reads like "put
    /// the file's bytes here" — but in SDK 1.4.0 the serializer writes those bytes out as a UTF-8
    /// string rather than base64-encoding them. Handing it raw PNG bytes therefore puts mojibake
    /// on the wire and the receiving client rejects the block outright. What the property wants is
    /// the base64 text, as bytes. Verified against the wire JSON, not inferred.
    /// </remarks>
    private static byte[] Base64Bytes(byte[] payload) =>
        Encoding.UTF8.GetBytes(Convert.ToBase64String(payload));

    [McpServerTool(Name = "get_image")]
    [Description(
        "Returns a small PNG bar chart as a typed MCP image content block. Exercises capture's " +
        "no-configuration path: the block should be written to the shared volume and replaced " +
        "with a {path, name, size, mime} descriptor.")]
    public static ContentBlock GetImage() =>
        new ImageContentBlock { Data = Base64Bytes(TestMedia.BarChartPng), MimeType = "image/png" };

    [McpServerTool(Name = "get_audio")]
    [Description(
        "Returns a short WAV tone as a typed MCP audio content block. Same capture path as " +
        "get_image, for a non-image media type.")]
    public static ContentBlock GetAudio() =>
        new AudioContentBlock { Data = Base64Bytes(TestMedia.ToneWav), MimeType = "audio/wav" };

    [McpServerTool(Name = "get_image_with_text")]
    [Description(
        "Returns a text block followed by an image block. Capture should rewrite only the image " +
        "and leave the surrounding text untouched.")]
    public static IEnumerable<ContentBlock> GetImageWithText() =>
    [
        new TextContentBlock { Text = "Here is the chart you asked for:" },
        new ImageContentBlock { Data = Base64Bytes(TestMedia.BarChartPng), MimeType = "image/png" }
    ];

    [McpServerTool(Name = "get_file_base64")]
    [Description(
        "Returns a file the way a repository server does: metadata plus base64 content in an " +
        "ordinary JSON response. Pass kind='image' for a PNG or kind='text' for markdown. " +
        "Exercises the declarative capture rule — the image should be captured to the shared " +
        "volume, and the text should be left in the response as content.")]
    public static string GetFileBase64(
        [Description("Which fixture to return: 'image' (PNG) or 'text' (markdown).")]
        string kind = "image")
    {
        var isImage = !string.Equals(kind, "text", StringComparison.OrdinalIgnoreCase);
        var bytes = isImage ? TestMedia.BarChartPng : TestMedia.ReadmeText;
        var name = isImage ? "chart.png" : "README.md";

        return JsonSerializer.Serialize(new
        {
            name,
            path = $"fixtures/{name}",
            sha = "0000000000000000000000000000000000000000",
            size = bytes.Length,
            encoding = "base64",
            content = Convert.ToBase64String(bytes)
        }, JsonOptions);
    }

    [McpServerTool(Name = "get_file_mangled")]
    [Description(
        "Returns a PNG's bytes decoded as UTF-8 text, reproducing the failure mode of servers " +
        "that stringify binary instead of encoding it. The bytes are unrecoverable by design; " +
        "capture should drop the field and explain why rather than let it into context.")]
    public static string GetFileMangled()
    {
        // Encoding.UTF8.GetString substitutes U+FFFD for every byte sequence it cannot decode,
        // which is exactly what the real server does and exactly why the result is unusable.
        var mangled = Encoding.UTF8.GetString(TestMedia.NoisePng);

        return JsonSerializer.Serialize(new
        {
            name = "chart.png",
            path = "fixtures/chart.png",
            sha = "0000000000000000000000000000000000000000",
            size = TestMedia.NoisePng.Length,
            content = mangled
        }, JsonOptions);
    }

    [McpServerTool(Name = "get_text")]
    [Description(
        "Returns plain text. The control case: nothing here should ever be captured, whatever " +
        "rules are configured.")]
    public static string GetText() => Encoding.UTF8.GetString(TestMedia.ReadmeText);

    [McpServerTool(Name = "describe_fixtures")]
    [Description(
        "Returns what the fixture image actually depicts, so a vision model's description can be " +
        "checked against a known answer. Call this AFTER analysing the image, not before.")]
    public static string DescribeFixtures() => TestMedia.BarChartDescription;
}
