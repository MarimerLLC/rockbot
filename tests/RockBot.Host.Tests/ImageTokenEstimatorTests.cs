using System.Buffers.Binary;
using RockBot.Host;

namespace RockBot.Host.Tests;

/// <summary>
/// Tests for the pixel-dimension image cost model (issue #564). The context estimate used to
/// size an image from its byte count, which gets both the magnitude and the ordering wrong:
/// providers scale an image into a bounded tile grid and charge per tile, so a 4 MB photo and a
/// 400 KB screenshot of the same size cost the same, and a 5 KB icon costs a fraction of either.
/// </summary>
[TestClass]
public class ImageTokenEstimatorTests
{
    // ── The cost model ───────────────────────────────────────────────────────

    [TestMethod]
    public void EstimateTokens_1024Square_Costs765()
    {
        // The provider's own worked example. Shortest side 1024 scales to 768, giving a 768×768
        // image, which is a 2×2 tile grid: 85 + 4 × 170.
        Assert.AreEqual(765, ImageTokenEstimator.EstimateTokens(1024, 1024));
    }

    [TestMethod]
    public void EstimateTokens_2048By4096_Costs1105()
    {
        // Second worked example, exercising both scaling steps: fit to 2048 → 1024×2048, then
        // shortest side to 768 → 768×1536, a 2×3 grid.
        Assert.AreEqual(1105, ImageTokenEstimator.EstimateTokens(2048, 4096));
    }

    [TestMethod]
    public void EstimateTokens_SmallImage_CostsOneTileAndIsNotUpscaled()
    {
        // Scaling is down-only. An icon occupies one tile; scaling it *up* to the 768px
        // shortest-side target would charge it four.
        Assert.AreEqual(255, ImageTokenEstimator.EstimateTokens(64, 64));
        Assert.AreEqual(255, ImageTokenEstimator.EstimateTokens(150, 150));
        Assert.AreEqual(255, ImageTokenEstimator.EstimateTokens(512, 512));
    }

    [TestMethod]
    public void EstimateTokens_HugeImage_IsBoundedByTheTileCeiling()
    {
        // However large the source, the scaling rules bound it at 768×2048 — an 8-tile grid.
        Assert.AreEqual(ImageTokenEstimator.MaxTokens, ImageTokenEstimator.EstimateTokens(8000, 24000));
        Assert.AreEqual(1445, ImageTokenEstimator.MaxTokens);

        for (var edge = 600; edge <= 20_000; edge += 373)
        {
            Assert.IsTrue(ImageTokenEstimator.EstimateTokens(edge, edge * 3) <= ImageTokenEstimator.MaxTokens,
                $"{edge}×{edge * 3} must not exceed the tile ceiling.");
        }
    }

    [TestMethod]
    public void EstimateTokens_DegenerateDimensions_CostOneTile()
    {
        Assert.AreEqual(255, ImageTokenEstimator.EstimateTokens(0, 0));
        Assert.AreEqual(255, ImageTokenEstimator.EstimateTokens(-10, 40));
    }

    [TestMethod]
    public void EstimateTokens_IsMonotonicInArea()
    {
        // A larger image never costs less than a smaller one — the property that made byte
        // count unusable (a heavily compressed large photo can be smaller than a small PNG).
        var previous = 0;
        foreach (var edge in new[] { 32, 128, 512, 640, 1024, 1536, 2048, 4096 })
        {
            var tokens = ImageTokenEstimator.EstimateTokens(edge, edge);
            Assert.IsTrue(tokens >= previous, $"{edge}² cost {tokens}, less than the smaller image's {previous}.");
            previous = tokens;
        }
    }

    // ── Header parsing ───────────────────────────────────────────────────────

    [TestMethod]
    public void TryReadDimensions_Png_ReadsIhdr()
    {
        Assert.IsTrue(ImageTokenEstimator.TryReadDimensions(Png(2048, 1536), out var w, out var h));
        Assert.AreEqual(2048, w);
        Assert.AreEqual(1536, h);
    }

    [TestMethod]
    public void TryReadDimensions_Jpeg_WalksSegmentsToTheFrameHeader()
    {
        // The frame header sits behind a JFIF segment, which must be skipped by its length.
        Assert.IsTrue(ImageTokenEstimator.TryReadDimensions(Jpeg(1920, 1080), out var w, out var h));
        Assert.AreEqual(1920, w);
        Assert.AreEqual(1080, h);
    }

    [TestMethod]
    public void TryReadDimensions_Gif_ReadsLogicalScreenSize()
    {
        Assert.IsTrue(ImageTokenEstimator.TryReadDimensions(Gif(640, 480), out var w, out var h));
        Assert.AreEqual(640, w);
        Assert.AreEqual(480, h);
    }

    [TestMethod]
    public void TryReadDimensions_WebPExtended_ReadsCanvasSize()
    {
        Assert.IsTrue(ImageTokenEstimator.TryReadDimensions(WebPExtended(3000, 2000), out var w, out var h));
        Assert.AreEqual(3000, w);
        Assert.AreEqual(2000, h);
    }

    [TestMethod]
    public void TryReadDimensions_WebPLossy_ReadsKeyframeHeader()
    {
        Assert.IsTrue(ImageTokenEstimator.TryReadDimensions(WebPLossy(1280, 720), out var w, out var h));
        Assert.AreEqual(1280, w);
        Assert.AreEqual(720, h);
    }

    [TestMethod]
    public void TryReadDimensions_Bmp_ReadsInfoHeaderAndAbsorbsTopDownHeight()
    {
        Assert.IsTrue(ImageTokenEstimator.TryReadDimensions(Bmp(800, -600), out var w, out var h));
        Assert.AreEqual(800, w);
        Assert.AreEqual(600, h, "A negative height means a top-down bitmap, not a negative size.");
    }

    [TestMethod]
    public void TryReadDimensions_UnknownOrTruncated_ReturnsFalse()
    {
        Assert.IsFalse(ImageTokenEstimator.TryReadDimensions(new byte[1_000], out _, out _),
            "A block of zeroes is not an image header.");
        Assert.IsFalse(ImageTokenEstimator.TryReadDimensions(Png(100, 100)[..12], out _, out _),
            "A PNG truncated before IHDR must not be guessed at.");
        Assert.IsFalse(ImageTokenEstimator.TryReadDimensions([], out _, out _));
        Assert.IsFalse(ImageTokenEstimator.TryReadDimensions("<svg viewBox=\"0 0 10 10\"/>"u8, out _, out _),
            "SVG has no binary header this parses; it must fall back, not misread.");
    }

    [TestMethod]
    public void TryEstimateTokens_RealHeader_SizesFromDimensions()
    {
        // A 2048×1536 image padded out to 1.8 MB. Byte count is irrelevant to the answer.
        var png = new byte[1_800_000];
        Png(2048, 1536).CopyTo(png.AsSpan());

        Assert.IsTrue(ImageTokenEstimator.TryEstimateTokens(png, out var tokens));
        Assert.AreEqual(ImageTokenEstimator.EstimateTokens(2048, 1536), tokens);
        Assert.AreEqual(765, tokens);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    internal static byte[] Png(int width, int height)
    {
        var b = new byte[24];
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(b);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(b.AsSpan(12));
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(20, 4), height);
        return b;
    }

    internal static byte[] Jpeg(int width, int height)
    {
        // SOI, a 16-byte APP0/JFIF segment to be skipped, then SOF0 carrying the size.
        var b = new byte[2 + 18 + 11];
        b[0] = 0xFF; b[1] = 0xD8;
        b[2] = 0xFF; b[3] = 0xE0;
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4, 2), 16);
        "JFIF"u8.CopyTo(b.AsSpan(6));

        var sof = 20;
        b[sof] = 0xFF; b[sof + 1] = 0xC0;
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(sof + 2, 2), 9);
        b[sof + 4] = 8; // sample precision
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(sof + 5, 2), (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(sof + 7, 2), (ushort)width);
        return b;
    }

    internal static byte[] Gif(int width, int height)
    {
        var b = new byte[13];
        "GIF89a"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6, 2), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8, 2), (ushort)height);
        return b;
    }

    internal static byte[] WebPExtended(int width, int height)
    {
        var b = new byte[30];
        "RIFF"u8.CopyTo(b);
        "WEBP"u8.CopyTo(b.AsSpan(8));
        "VP8X"u8.CopyTo(b.AsSpan(12));
        var w = width - 1;
        var h = height - 1;
        b[24] = (byte)w; b[25] = (byte)(w >> 8); b[26] = (byte)(w >> 16);
        b[27] = (byte)h; b[28] = (byte)(h >> 8); b[29] = (byte)(h >> 16);
        return b;
    }

    internal static byte[] WebPLossy(int width, int height)
    {
        var b = new byte[30];
        "RIFF"u8.CopyTo(b);
        "WEBP"u8.CopyTo(b.AsSpan(8));
        "VP8 "u8.CopyTo(b.AsSpan(12));
        // 3-byte frame tag at 20, then the keyframe start code, then 14-bit dimensions.
        b[23] = 0x9D; b[24] = 0x01; b[25] = 0x2A;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(26, 2), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(28, 2), (ushort)height);
        return b;
    }

    internal static byte[] Bmp(int width, int height)
    {
        var b = new byte[26];
        "BM"u8.CopyTo(b);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(18, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(22, 4), height);
        return b;
    }
}
