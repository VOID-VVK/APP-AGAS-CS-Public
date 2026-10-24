#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace Agas.Features.Canvas;

/// <summary>
/// Owns filesystem freshness checks and Godot texture creation for canvas images.
/// The workspace only decides when the cache lifetime ends.
/// </summary>
internal sealed class CanvasTextureCache
{
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);

    public Texture2D? GetOrLoad(string absolutePath)
    {
        if (!File.Exists(absolutePath))
        {
            return null;
        }

        FileInfo info = new(absolutePath);
        if (_entries.TryGetValue(absolutePath, out CacheEntry? cached) && cached.Matches(info))
        {
            return cached.Texture;
        }

        using Image image = Image.LoadFromFile(absolutePath);
        if (image == null || image.IsEmpty())
        {
            GD.PushWarning($"Image.LoadFromFile failed for '{absolutePath}'");
            return null;
        }

        ImageTexture texture = ImageTexture.CreateFromImage(image);
        _entries[absolutePath] = new CacheEntry(texture, info.LastWriteTimeUtc, info.Length);
        return texture;
    }

    public void Clear() => _entries.Clear();

    private sealed record CacheEntry(Texture2D Texture, DateTime LastWriteUtc, long Length)
    {
        public bool Matches(FileInfo info) => info.LastWriteTimeUtc == LastWriteUtc && info.Length == Length;
    }
}
