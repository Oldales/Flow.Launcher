using System;
using System.Buffers.Binary;
using System.IO;

namespace Flow.Launcher.Infrastructure;

/// <summary>
/// Reads "mozLz4" files, which Firefox-based browsers use for session data: an 8-byte magic,
/// the decompressed size as a little-endian int32, then a single raw LZ4 block.
/// </summary>
public static class MozLz4
{
    private const int HeaderLength = 12;
    private const int MaxDecompressedSize = 256 * 1024 * 1024;

    private static ReadOnlySpan<byte> Magic => "mozLz40\0"u8;

    /// <exception cref="InvalidDataException">The data is not a valid mozLz4 file.</exception>
    public static byte[] Decompress(ReadOnlySpan<byte> file)
    {
        if (file.Length < HeaderLength || !file[..Magic.Length].SequenceEqual(Magic))
            throw new InvalidDataException("Not a mozLz4 file");

        var size = BinaryPrimitives.ReadInt32LittleEndian(file[Magic.Length..HeaderLength]);
        if (size < 0 || size > MaxDecompressedSize)
            throw new InvalidDataException($"Unexpected mozLz4 size {size}");

        var output = new byte[size];
        try
        {
            DecodeBlock(file[HeaderLength..], output);
        }
        catch (ArgumentOutOfRangeException e)
        {
            throw new InvalidDataException("Truncated or corrupt LZ4 block", e);
        }
        return output;
    }

    // LZ4 block format: sequences of [token][literal length bytes][literals][offset][match length bytes]
    private static void DecodeBlock(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var src = 0;
        var dst = 0;
        while (src < source.Length)
        {
            var token = source[src++];

            var literalLength = token >> 4;
            if (literalLength == 15)
                literalLength += ReadExtraLength(source, ref src);

            source.Slice(src, literalLength).CopyTo(destination[dst..]);
            src += literalLength;
            dst += literalLength;

            // The last sequence carries literals only
            if (src >= source.Length)
                break;

            var offset = source[src] | (source[src + 1] << 8);
            src += 2;
            if (offset == 0 || offset > dst)
                throw new InvalidDataException("Invalid LZ4 match offset");

            var matchLength = (token & 0x0F) + 4;
            if ((token & 0x0F) == 15)
                matchLength += ReadExtraLength(source, ref src);

            var from = dst - offset;
            if (offset >= matchLength)
            {
                destination.Slice(from, matchLength).CopyTo(destination[dst..]);
            }
            else
            {
                // Overlapping match repeats recent output, so copy byte by byte
                for (var i = 0; i < matchLength; i++)
                    destination[dst + i] = destination[from + i];
            }
            dst += matchLength;
        }

        if (dst != destination.Length)
            throw new InvalidDataException("LZ4 block size does not match the header");
    }

    private static int ReadExtraLength(ReadOnlySpan<byte> source, ref int src)
    {
        var length = 0;
        byte value;
        do
        {
            value = source[src++];
            length += value;
        } while (value == 255);
        return length;
    }
}
