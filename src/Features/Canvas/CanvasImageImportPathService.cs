#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using AppAgasCs.Application;
using Godot;
using Godot.Collections;

namespace Agas.Features.Canvas;

/// <summary>
/// Normalizes drag/drop and API image paths through the application-owned
/// project path resolver. CanvasWorkspace owns mutation orchestration; this
/// service owns only import input decoding and project-boundary validation.
/// </summary>
internal sealed class CanvasImageImportPathService
{
    private readonly ProjectImagePathResolver _resolver = new();

    internal string NormalizeProjectRoot(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(projectRoot)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return string.Empty;
        }
    }

    internal ProjectImagePathResult ResolveImportPath(string projectRoot, string inputPath)
    {
        string root = NormalizeProjectRoot(projectRoot);
        if (root.Length == 0 || string.IsNullOrWhiteSpace(inputPath))
        {
            return ProjectImagePathResult.Rejected;
        }

        try
        {
            string input = inputPath.Trim();
            string relative = Path.IsPathRooted(input)
                ? Path.GetRelativePath(root, Path.GetFullPath(input))
                : input;
            return _resolver.Resolve(
                root,
                relative.Replace(Path.DirectorySeparatorChar, '/'),
                mustExist: true);
        }
        catch
        {
            return ProjectImagePathResult.Rejected;
        }
    }

    internal ProjectImagePathResult ResolveStoredProjectPath(string projectRoot, string relativePath) =>
        _resolver.Resolve(NormalizeProjectRoot(projectRoot), relativePath, mustExist: true);

    internal bool CanImportDroppedData(string projectRoot, Variant data)
    {
        foreach (string path in ExtractDroppedPaths(data))
        {
            if (ResolveImportPath(projectRoot, path).Success)
            {
                return true;
            }
        }
        return false;
    }

    internal IEnumerable<string> ExtractDroppedPaths(Variant data)
    {
        switch (data.VariantType)
        {
            case Variant.Type.String:
                yield return data.AsString();
                yield break;
            case Variant.Type.PackedStringArray:
                foreach (string path in data.AsStringArray())
                {
                    yield return path;
                }
                yield break;
            case Variant.Type.Array:
                foreach (Variant item in data.AsGodotArray())
                {
                    foreach (string path in ExtractDroppedPaths(item))
                    {
                        yield return path;
                    }
                }
                yield break;
            case Variant.Type.Dictionary:
                Dictionary dictionary = data.AsGodotDictionary();
                foreach (string key in new[] { "files", "paths", "path", "absolute_path", "relative_path", "ImagePath" })
                {
                    if (dictionary.TryGetValue(key, out Variant value))
                    {
                        foreach (string path in ExtractDroppedPaths(value))
                        {
                            yield return path;
                        }
                    }
                }
                yield break;
        }
    }
}
