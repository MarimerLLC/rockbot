using System.Buffers.Binary;

namespace RockBot.Host;

/// <summary>
/// Estimates what an image costs in an LLM request, in tokens, from its pixel dimensions.
///
/// <para>Providers do not bill an image by its byte count. The OpenAI-compatible APIs RockBot
/// speaks scale the image down to a bounded box, tile it, and charge a flat base plus a fixed
/// number of tokens per tile — so a 4 MB photo and a 400 KB screenshot of the same dimensions
/// cost exactly the same, and a 5 KB icon costs a small fraction of either. Any byte-derived
/// proxy therefore gets the ordering wrong as often as the magnitude: it over-charges small
/// images and, once capped, charges every real photo the same ceiling.</para>
///
/// <para>Dimensions come from the image header, which for every format below sits in the first
/// few dozen bytes. Nothing here decodes pixels.</para>
/// </summary>
internal static class ImageTokenEstimator
{
    /// <summary>Flat cost charged for an image regardless of size.</summary>
    internal const int BaseTokens = 85;

    /// <summary>Cost of each tile the scaled image is divided into.</summary>
    internal const int TokensPerTile = 170;

    /// <summary>Edge length of one tile, in pixels.</summary>
    internal const int TileSize = 512;

    /// <summary>The image is first scaled down to fit inside this square.</summary>
    internal const int MaxDimension = 2048;

    /// <summary>It is then scaled down until its shortest side is at most this. Never upscaled.</summary>
    internal const int ShortestSide = 768;

    /// <summary>
    /// Most tiles any image can reduce to: after both scalings the shortest side is at most
    /// 768px (2 tiles) and the longest at most 2048px (4 tiles).
    /// </summary>
    internal const int MaxTiles = 8;

    /// <summary>Cost of the largest image the scaling rules permit — 1,445 tokens.</summary>
    internal const int MaxTokens = BaseTokens + (TokensPerTile * MaxTiles);

    /// <summary>
    /// Reads <paramref name="data"/>'s image header and estimates its token cost. Returns
    /// <c>false</c> when the bytes are not a format this understands (or are truncated), which
    /// is the caller's signal to fall back to a byte-derived proxy.
    /// </summary>
    public static bool TryEstimateTokens(ReadOnlySpan<byte> data, out int tokens)
    {
        if (TryReadDimensions(data, out var width, out var height))
        {
            tokens = EstimateTokens(width, height);
            return true;
        }

        tokens = 0;
        return false;
    }

    /// <summary>
    /// Applies the provider's scale-then-tile cost model to a pixel size. Scaling is
    /// down-only: an image smaller than one tile costs one tile, not a scaled-up grid of them.
    /// </summary>
    public static int EstimateTokens(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return BaseTokens + TokensPerTile;

        double w = width, h = height;

        // Fit inside MaxDimension × MaxDimension.
        var longest = Math.Max(w, h);
        if (longest > MaxDimension)
        {
            var scale = MaxDimension / longest;
            w *= scale;
            h *= scale;
        }

        // Then bring the shortest side down to ShortestSide.
        var shortest = Math.Min(w, h);
        if (shortest > ShortestSide)
        {
            var scale = ShortestSide / shortest;
            w *= scale;
            h *= scale;
        }

        var tiles = (int)Math.Ceiling(w / TileSize) * (int)Math.Ceiling(h / TileSize);
        // Clamp rather than trust the arithmetic: a rounding artefact that produced a 9th tile
        // would over-charge silently, which is the failure mode this whole estimate exists to
        // avoid.
        tiles = Math.Clamp(tiles, 1, MaxTiles);

        return BaseTokens + (TokensPerTile * tiles);
    }

    /// <summary>
    /// Reads pixel dimensions from a PNG, JPEG, GIF, WebP or BMP header. Returns <c>false</c>
    /// for anything else, including a truncated or corrupt header — the caller decides what an
    /// unreadable image costs; guessing a size here would be the same silent under-count this
    /// class replaced.
    /// </summary>
    public static bool TryReadDimensions(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (TryReadPng(data, out width, out height)) return true;
        if (TryReadJpeg(data, out width, out height)) return true;
        if (TryReadGif(data, out width, out height)) return true;
        if (TryReadWebP(data, out width, out height)) return true;
        if (TryReadBmp(data, out width, out height)) return true;

        width = 0;
        height = 0;
        return false;
    }

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static bool TryReadPng(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;

        // 8-byte signature, then a chunk length and the "IHDR" tag, then width and height as
        // big-endian 32-bit values.
        if (data.Length < 24 || !data[..8].SequenceEqual(PngSignature)) return false;
        if (data[12] != (byte)'I' || data[13] != (byte)'H' || data[14] != (byte)'D' || data[15] != (byte)'R')
            return false;

        width = BinaryPrimitives.ReadInt32BigEndian(data[16..20]);
        height = BinaryPrimitives.ReadInt32BigEndian(data[20..24]);
        return width > 0 && height > 0;
    }

    private static bool TryReadJpeg(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return false;

        // Walk the segment chain to the start-of-frame marker, which carries the dimensions.
        // Everything before it (JFIF/Exif/quantisation/Huffman tables) is skipped by length.
        var i = 2;
        while (i + 3 < data.Length)
        {
            if (data[i] != 0xFF) { i++; continue; }

            var marker = data[i + 1];

            // 0xFF used as padding, and the standalone markers that carry no length.
            if (marker == 0xFF) { i++; continue; }
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD9)) { i += 2; continue; }

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(data[(i + 2)..(i + 4)]);
            if (segmentLength < 2) return false;

            // SOF0–SOF15 hold the frame size. C4/C8/CC are Huffman, JPG-extension and
            // arithmetic-coding tables sharing the same marker range.
            if (marker is >= 0xC0 and <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
            {
                if (i + 9 >= data.Length) return false;
                height = BinaryPrimitives.ReadUInt16BigEndian(data[(i + 5)..(i + 7)]);
                width = BinaryPrimitives.ReadUInt16BigEndian(data[(i + 7)..(i + 9)]);
                return width > 0 && height > 0;
            }

            // Start of compressed scan data — no frame header was found before it.
            if (marker == 0xDA) return false;

            i += 2 + segmentLength;
        }

        return false;
    }

    private static bool TryReadGif(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;

        // "GIF87a" or "GIF89a", then the logical screen size as little-endian 16-bit values.
        if (data.Length < 10) return false;
        if (data[0] != (byte)'G' || data[1] != (byte)'I' || data[2] != (byte)'F' || data[3] != (byte)'8')
            return false;
        if (data[4] is not ((byte)'7' or (byte)'9') || data[5] != (byte)'a') return false;

        width = BinaryPrimitives.ReadUInt16LittleEndian(data[6..8]);
        height = BinaryPrimitives.ReadUInt16LittleEndian(data[8..10]);
        return width > 0 && height > 0;
    }

    private static bool TryReadWebP(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;

        // "RIFF" ... "WEBP", then one of three chunk layouts.
        if (data.Length < 30) return false;
        if (data[0] != (byte)'R' || data[1] != (byte)'I' || data[2] != (byte)'F' || data[3] != (byte)'F')
            return false;
        if (data[8] != (byte)'W' || data[9] != (byte)'E' || data[10] != (byte)'B' || data[11] != (byte)'P')
            return false;

        var chunk = data[12..16];

        // Lossy: a VP8 keyframe header, dimensions as 14-bit values after the start code.
        if (chunk[0] == (byte)'V' && chunk[1] == (byte)'P' && chunk[2] == (byte)'8' && chunk[3] == (byte)' ')
        {
            if (data[23] != 0x9D || data[24] != 0x01 || data[25] != 0x2A) return false;
            width = BinaryPrimitives.ReadUInt16LittleEndian(data[26..28]) & 0x3FFF;
            height = BinaryPrimitives.ReadUInt16LittleEndian(data[28..30]) & 0x3FFF;
            return width > 0 && height > 0;
        }

        // Lossless: 14-bit dimensions minus one, bit-packed across four bytes.
        if (chunk[0] == (byte)'V' && chunk[1] == (byte)'P' && chunk[2] == (byte)'8' && chunk[3] == (byte)'L')
        {
            if (data[20] != 0x2F) return false;
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(data[21..25]);
            width = (int)(bits & 0x3FFF) + 1;
            height = (int)((bits >> 14) & 0x3FFF) + 1;
            return width > 0 && height > 0;
        }

        // Extended: canvas size as 24-bit values minus one.
        if (chunk[0] == (byte)'V' && chunk[1] == (byte)'P' && chunk[2] == (byte)'8' && chunk[3] == (byte)'X')
        {
            width = (data[24] | (data[25] << 8) | (data[26] << 16)) + 1;
            height = (data[27] | (data[28] << 8) | (data[29] << 16)) + 1;
            return width > 0 && height > 0;
        }

        return false;
    }

    private static bool TryReadBmp(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;

        // "BM", then a BITMAPINFOHEADER whose height is negative for top-down bitmaps.
        if (data.Length < 26 || data[0] != (byte)'B' || data[1] != (byte)'M') return false;

        width = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(data[18..22]));
        height = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(data[22..26]));
        return width > 0 && height > 0;
    }
}
