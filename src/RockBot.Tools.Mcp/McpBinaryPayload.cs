using System.Text;

namespace RockBot.Tools.Mcp;

/// <summary>
/// Reads the byte payload of an MCP image or audio content block.
/// </summary>
/// <remarks>
/// <para>
/// <c>ImageContentBlock.Data</c> is typed <c>ReadOnlyMemory&lt;byte&gt;</c>, which reads like "the
/// file's bytes". In SDK 1.4.0 it is not: the property carries the wire field verbatim, and the
/// wire field is base64 <em>text</em>. Taking <c>Data.ToArray()</c> therefore yields the ASCII of
/// "iVBORw0KGgo…" rather than a PNG — verified by capturing a 783-byte fixture image and finding
/// exactly 1044 bytes of base64 on disk.
/// </para>
/// <para>
/// Because the type says one thing and the contents say another, both readings are handled here
/// rather than assumed at each call site: the payload is decoded when it really is base64 text and
/// passed through when it is not. That also means a future SDK version that starts storing decoded
/// bytes needs no change here.
/// </para>
/// </remarks>
internal static class McpBinaryPayload
{
    /// <summary>Returns the payload's real bytes, decoding base64 text when that is what it holds.</summary>
    public static byte[] Decode(ReadOnlyMemory<byte> data)
    {
        if (data.IsEmpty) return [];

        if (TryReadBase64Text(data.Span, out var text)
            && TryFromBase64(text, out var decoded))
        {
            return decoded;
        }

        return data.ToArray();
    }

    /// <summary>
    /// Returns the payload as a base64 string, without re-encoding one it already holds. The
    /// naive <c>Convert.ToBase64String(block.Data.Span)</c> double-encodes.
    /// </summary>
    public static string ToBase64(ReadOnlyMemory<byte> data)
    {
        if (data.IsEmpty) return string.Empty;

        if (TryReadBase64Text(data.Span, out var text) && TryFromBase64(text, out _))
            return text;

        return Convert.ToBase64String(data.Span);
    }

    /// <summary>
    /// Whether the bytes are printable ASCII in the base64 alphabet — a cheap gate that keeps real
    /// binary (which is full of bytes outside it) away from the decode attempt.
    /// </summary>
    private static bool TryReadBase64Text(ReadOnlySpan<byte> data, out string text)
    {
        text = string.Empty;

        // Base64 encodes 3 bytes into 4, so real base64 text is always a multiple of four; PNG and
        // WAV headers are not ASCII at all and fail on the first byte.
        if (data.Length % 4 != 0) return false;

        foreach (var b in data)
        {
            var isBase64Char = b is >= (byte)'A' and <= (byte)'Z'
                or >= (byte)'a' and <= (byte)'z'
                or >= (byte)'0' and <= (byte)'9'
                or (byte)'+' or (byte)'/' or (byte)'=';
            if (!isBase64Char) return false;
        }

        text = Encoding.ASCII.GetString(data);
        return true;
    }

    private static bool TryFromBase64(string text, out byte[] decoded)
    {
        var buffer = new byte[text.Length / 4 * 3];
        if (Convert.TryFromBase64String(text, buffer, out var written))
        {
            decoded = written == buffer.Length ? buffer : buffer[..written];
            return true;
        }

        decoded = [];
        return false;
    }
}
