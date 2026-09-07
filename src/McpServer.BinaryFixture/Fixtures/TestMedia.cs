using System.IO.Compression;
using System.Text;

namespace McpServer.BinaryFixture.Fixtures;

/// <summary>
/// Generates the fixture payloads in code rather than shipping binary assets.
/// </summary>
/// <remarks>
/// Generated media keeps the repository free of binary blobs, but the real reason is that the
/// content is <em>known</em>: the image is a bar chart whose colours and relative heights are
/// stated here, so an assertion about what a vision model saw can be checked against this file
/// instead of against someone's recollection of a photo.
/// </remarks>
public static class TestMedia
{
    /// <summary>
    /// What <see cref="BarChartPng"/> depicts, in the words a correct description would use.
    /// Kept next to the generator so the two cannot drift apart.
    /// </summary>
    public const string BarChartDescription =
        "Three vertical bars on a light background, left to right: red (medium height), " +
        "green (tallest), blue (shortest), sitting on a dark horizontal baseline.";

    private const int Width = 240;
    private const int Height = 120;

    private static readonly Lazy<byte[]> LazyBarChart = new(BuildBarChartPng);
    private static readonly Lazy<byte[]> LazyTone = new(BuildToneWav);
    private static readonly Lazy<byte[]> LazyNoise = new(BuildNoisePng);

    /// <summary>A 240×120 PNG bar chart. See <see cref="BarChartDescription"/>.</summary>
    public static byte[] BarChartPng => LazyBarChart.Value;

    /// <summary>A short mono WAV tone — enough to be a real audio file with a real header.</summary>
    public static byte[] ToneWav => LazyTone.Value;

    /// <summary>
    /// A deliberately incompressible PNG, for the mangled-binary fixture.
    /// </summary>
    /// <remarks>
    /// Size is the point. The mangled-binary guard only fires above a length floor, because its
    /// job is to stop a context flood and a few hundred characters of mojibake is not one. The bar
    /// chart is flat colour and compresses to under a kilobyte, which lands below that floor and
    /// makes the fixture prove nothing. Pixel noise gives a payload the size of a real repository
    /// image, which is what the guard exists for.
    /// </remarks>
    public static byte[] NoisePng => LazyNoise.Value;

    /// <summary>Plain UTF-8 text, used as the control case that must never be captured.</summary>
    public static byte[] ReadmeText { get; } = Encoding.UTF8.GetBytes(
        "# Fixture README\n\nThis file is text. Binary capture must leave it in the response.\n");

    private static byte[] BuildBarChartPng()
    {
        var background = new byte[] { 250, 250, 250 };
        var baseline = new byte[] { 40, 40, 40 };
        (int X, int BarHeight, byte[] Colour)[] bars =
        [
            (20, 60, [210, 60, 60]),   // red, medium
            (95, 95, [70, 150, 90]),   // green, tallest
            (170, 40, [70, 110, 200])  // blue, shortest
        ];

        // Raw scanlines: one filter byte (0 = None) then RGB triples.
        var raw = new byte[Height * (1 + Width * 3)];
        var pos = 0;
        for (var y = 0; y < Height; y++)
        {
            raw[pos++] = 0;
            for (var x = 0; x < Width; x++)
            {
                var colour = background;
                if (y == Height - 10 && x >= 10 && x < Width - 10)
                {
                    colour = baseline;
                }
                else
                {
                    foreach (var (barX, barHeight, barColour) in bars)
                    {
                        if (x >= barX && x < barX + 50 && y >= Height - 10 - barHeight && y < Height - 10)
                        {
                            colour = barColour;
                            break;
                        }
                    }
                }

                raw[pos++] = colour[0];
                raw[pos++] = colour[1];
                raw[pos++] = colour[2];
            }
        }

        using var output = new MemoryStream();
        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, Width);
        WriteBigEndian(ihdr, 4, Height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 2;  // colour type: truecolour
        WriteChunk(output, "IHDR", ihdr);
        WriteChunk(output, "IDAT", Deflate(raw));
        WriteChunk(output, "IEND", []);

        return output.ToArray();
    }

    private static byte[] BuildNoisePng()
    {
        const int width = 320;
        const int height = 240;

        // A fixed seed keeps the fixture byte-identical between runs, so a size quoted in a test
        // report stays true.
        var rng = new Random(20260906);
        var raw = new byte[height * (1 + width * 3)];
        var pos = 0;
        for (var y = 0; y < height; y++)
        {
            raw[pos++] = 0;
            for (var x = 0; x < width * 3; x++)
                raw[pos++] = (byte)rng.Next(256);
        }

        using var output = new MemoryStream();
        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, width);
        WriteBigEndian(ihdr, 4, height);
        ihdr[8] = 8;
        ihdr[9] = 2;
        WriteChunk(output, "IHDR", ihdr);
        WriteChunk(output, "IDAT", Deflate(raw));
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static byte[] BuildToneWav()
    {
        const int sampleRate = 8000;
        const int samples = sampleRate / 4;   // 250 ms
        const double frequency = 440.0;

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);

        var dataBytes = samples * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);                       // PCM header size
        writer.Write((short)1);                 // PCM
        writer.Write((short)1);                 // mono
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);           // byte rate
        writer.Write((short)2);                 // block align
        writer.Write((short)16);                // bits per sample
        writer.Write("data"u8);
        writer.Write(dataBytes);

        for (var i = 0; i < samples; i++)
            writer.Write((short)(Math.Sin(2 * Math.PI * frequency * i / sampleRate) * 12000));

        writer.Flush();
        return output.ToArray();
    }

    private static byte[] Deflate(byte[] data)
    {
        using var buffer = new MemoryStream();
        using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(data);
        return buffer.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        var length = new byte[4];
        WriteBigEndian(length, 0, data.Length);
        stream.Write(length);
        stream.Write(typeBytes);
        stream.Write(data);

        var crcInput = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(crcInput, 0);
        data.CopyTo(crcInput, typeBytes.Length);
        var crc = new byte[4];
        WriteBigEndian(crc, 0, unchecked((int)Crc32(crcInput)));
        stream.Write(crc);
    }

    private static void WriteBigEndian(byte[] target, int offset, int value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
