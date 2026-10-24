#nullable enable

using System;
using System.IO;

namespace AppAgasCs.Features.Tools.Execution;

internal static class ImageToolImageHeader
{
    private static ReadOnlySpan<byte> PngSignature => [137, 80, 78, 71, 13, 10, 26, 10];

    public static bool TryRead(string path, ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = height = 0;
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".png" when bytes.Length >= 24 && bytes[..8].SequenceEqual(PngSignature):
                width = BigEndian32(bytes.Slice(16, 4));
                height = BigEndian32(bytes.Slice(20, 4));
                return width > 0 && height > 0;
            case ".jpg" or ".jpeg" when bytes.Length >= 4 && bytes[0] == 0xff && bytes[1] == 0xd8:
                return ReadJpeg(bytes, out width, out height);
            case ".webp" when bytes.Length >= 30
                && bytes[..4].SequenceEqual("RIFF"u8)
                && bytes.Slice(8, 4).SequenceEqual("WEBP"u8):
                return ReadWebp(bytes, out width, out height);
            default:
                return false;
        }
    }

    private static bool ReadJpeg(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = height = 0;
        int offset = 2;
        while (offset + 4 <= bytes.Length)
        {
            if (bytes[offset] != 0xff) { offset++; continue; }
            byte marker = bytes[offset + 1];
            offset += 2;
            if (marker is 0xd8 or 0xd9 or >= 0xd0 and <= 0xd7) continue;
            if (offset + 2 > bytes.Length) return false;
            int length = (bytes[offset] << 8) | bytes[offset + 1];
            if (length < 2 || offset + length > bytes.Length) return false;
            bool sizeMarker = marker is >= 0xc0 and <= 0xc3
                or >= 0xc5 and <= 0xc7
                or >= 0xc9 and <= 0xcb
                or >= 0xcd and <= 0xcf;
            if (sizeMarker && length >= 7)
            {
                height = (bytes[offset + 3] << 8) | bytes[offset + 4];
                width = (bytes[offset + 5] << 8) | bytes[offset + 6];
                return width > 0 && height > 0;
            }
            offset += length;
        }
        return false;
    }

    private static bool ReadWebp(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = height = 0;
        if (bytes.Slice(12, 4).SequenceEqual("VP8X"u8))
        {
            width = 1 + bytes[24] + (bytes[25] << 8) + (bytes[26] << 16);
            height = 1 + bytes[27] + (bytes[28] << 8) + (bytes[29] << 16);
        }
        else if (bytes.Slice(12, 4).SequenceEqual("VP8L"u8) && bytes[20] == 0x2f)
        {
            width = 1 + bytes[21] + ((bytes[22] & 0x3f) << 8);
            height = 1 + (bytes[22] >> 6) + (bytes[23] << 2) + ((bytes[24] & 0x0f) << 10);
        }
        else if (bytes.Slice(12, 4).SequenceEqual("VP8 "u8)
                 && bytes[23] == 0x9d && bytes[24] == 0x01 && bytes[25] == 0x2a)
        {
            width = (bytes[26] | (bytes[27] << 8)) & 0x3fff;
            height = (bytes[28] | (bytes[29] << 8)) & 0x3fff;
        }
        return width > 0 && height > 0;
    }

    private static int BigEndian32(ReadOnlySpan<byte> bytes)
    {
        uint value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        return value <= int.MaxValue ? (int)value : 0;
    }
}
