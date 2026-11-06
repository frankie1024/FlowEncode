using System.Buffers.Binary;
using System.IO.Compression;

namespace FlowEncode.Application;

/// <summary>
/// Minimal PNG writer for the BGRA8 frames the VapourSynth preview host produces, so headless
/// callers can save frames without a UI imaging stack.
/// </summary>
public static class BgraImageEncoder
{
    private const int BytesPerPixel = 4;
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static byte[] EncodePng(ReadOnlySpan<byte> bgraPixels, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
        }

        var stride = checked(width * BytesPerPixel);
        if (bgraPixels.Length < checked(stride * height))
        {
            throw new ArgumentException("Pixel buffer is shorter than the image size.", nameof(bgraPixels));
        }

        using var output = new MemoryStream();
        output.Write(PngSignature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;   // bit depth
        header[9] = 6;   // colour type: RGBA
        header[10] = 0;  // deflate
        header[11] = 0;  // adaptive filtering
        header[12] = 0;  // no interlace
        WriteChunk(output, "IHDR"u8, header);

        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                var row = new byte[stride + 1];
                for (var y = 0; y < height; y++)
                {
                    row[0] = 0; // filter: none
                    var source = bgraPixels.Slice(y * stride, stride);
                    for (var x = 0; x < stride; x += BytesPerPixel)
                    {
                        row[x + 1] = source[x + 2];
                        row[x + 2] = source[x + 1];
                        row[x + 3] = source[x];
                        row[x + 4] = source[x + 3];
                    }

                    zlib.Write(row);
                }
            }

            WriteChunk(output, "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        }

        WriteChunk(output, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    /// <summary>
    /// Places frames next to each other (horizontal) or on top of each other, padding with black.
    /// </summary>
    public static (byte[] Pixels, int Width, int Height) Stack(
        IReadOnlyList<(byte[] Pixels, int Width, int Height)> frames,
        bool horizontal)
    {
        if (frames.Count == 0)
        {
            throw new ArgumentException("At least one frame is required.", nameof(frames));
        }

        var width = horizontal ? frames.Sum(static frame => frame.Width) : frames.Max(static frame => frame.Width);
        var height = horizontal ? frames.Max(static frame => frame.Height) : frames.Sum(static frame => frame.Height);
        var stride = checked(width * BytesPerPixel);
        var pixels = new byte[checked(stride * height)];
        for (var index = 3; index < pixels.Length; index += BytesPerPixel)
        {
            pixels[index] = 0xFF;
        }

        var offsetX = 0;
        var offsetY = 0;
        foreach (var frame in frames)
        {
            var frameStride = frame.Width * BytesPerPixel;
            for (var y = 0; y < frame.Height; y++)
            {
                Buffer.BlockCopy(
                    frame.Pixels,
                    y * frameStride,
                    pixels,
                    ((offsetY + y) * stride) + (offsetX * BytesPerPixel),
                    frameStride);
            }

            if (horizontal)
            {
                offsetX += frame.Width;
            }
            else
            {
                offsetY += frame.Height;
            }
        }

        return (pixels, width, height);
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        output.Write(buffer);
        output.Write(type);
        output.Write(data);

        var crc = UpdateCrc(UpdateCrc(0xFFFFFFFFu, type), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(buffer, crc);
        output.Write(buffer);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < table.Length; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
