using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AppAgasCs.Features.Ai.Generation;

/// <summary>Centralizes the project-local layout and safety checks for AI image artifacts.</summary>
internal static class AgasGeneratedImagePathPolicy
{
    public const string RootDirectoryName = "agas";
    public const string LegacyRelativeDirectory = "assets/generated";
    private const string CandidateDirectoryName = ".candidates";
    private const string CandidateFilePrefix = "Candidate ";
    private const string ResultFilePrefix = "Result ";
    private const string ReservationSuffix = ".reserve";

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp"
    };
    private static readonly HashSet<string> WindowsReservedNames = CreateWindowsReservedNames();

    public static string PrepareOutputDirectory(string projectRoot, AiGenerationStorageScope scope)
    {
        string root = ValidateProjectRoot(projectRoot);
        string sceneDirectoryName = SanitizeSegment(scope.SceneName, "scene", 80);
        string layerDirectoryName = SanitizeSegment(scope.LayerName, "layer", 80);
        string agasDirectory = Path.Combine(root, RootDirectoryName);
        string sceneDirectory = Path.Combine(agasDirectory, sceneDirectoryName);
        string layerDirectory = Path.Combine(sceneDirectory, layerDirectoryName);
        EnsureInside(root, layerDirectory);

        CreateCheckedDirectory(root, agasDirectory);
        agasDirectory = ResolveExistingDirectoryPath(root, RootDirectoryName);
        sceneDirectory = Path.Combine(agasDirectory, sceneDirectoryName);
        CreateCheckedDirectory(root, sceneDirectory);
        sceneDirectory = ResolveExistingDirectoryPath(agasDirectory, sceneDirectoryName);
        layerDirectory = Path.Combine(sceneDirectory, layerDirectoryName);
        CreateCheckedDirectory(root, layerDirectory);
        layerDirectory = ResolveExistingDirectoryPath(sceneDirectory, layerDirectoryName);
        EnsureSafeDirectoryChain(root, layerDirectory);
        return layerDirectory;
    }

    public static GeneratedImageFileReservation ReserveNextResultFile(
        string projectRoot,
        AiGenerationStorageScope scope,
        string extension,
        bool asynchronous) =>
        ReserveNextNumberedFile(projectRoot, scope, extension, ResultFilePrefix, candidate: false, asynchronous);

    public static GeneratedImageFileReservation ReserveNextCandidateFile(
        string projectRoot,
        AiGenerationStorageScope scope,
        string extension,
        bool asynchronous) =>
        ReserveNextNumberedFile(projectRoot, scope, extension, CandidateFilePrefix, candidate: true, asynchronous);

    private static GeneratedImageFileReservation ReserveNextNumberedFile(
        string projectRoot,
        AiGenerationStorageScope scope,
        string extension,
        string filePrefix,
        bool candidate,
        bool asynchronous)
    {
        if (!SupportedExtensions.Contains(extension))
        {
            throw new AiGenerationException(AiGenerationErrorCode.UnsupportedImage, "The generated image extension is unsupported.");
        }

        string root = ValidateProjectRoot(projectRoot);
        string outputDirectory = PrepareNumberedOutputDirectory(root, scope, candidate);
        int resultNumber = FindNextNumber(outputDirectory, filePrefix);
        while (true)
        {
            outputDirectory = PrepareNumberedOutputDirectory(root, scope, candidate);
            string resultName = $"{filePrefix}{resultNumber}";
            string finalPath = Path.Combine(outputDirectory, resultName + extension.ToLowerInvariant());
            string reservationPath = Path.Combine(outputDirectory, $".{resultName}{ReservationSuffix}");
            try
            {
                var stream = new FileStream(
                    reservationPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    asynchronous ? FileOptions.Asynchronous : FileOptions.None);
                return new GeneratedImageFileReservation(stream, reservationPath, finalPath);
            }
            catch (IOException) when (NumberExists(outputDirectory, filePrefix, resultNumber))
            {
                if (resultNumber == int.MaxValue)
                {
                    throw new AiGenerationException(AiGenerationErrorCode.SaveFailed, "The generated image result number is exhausted.");
                }
                resultNumber++;
            }
        }
    }

    private static string PrepareNumberedOutputDirectory(
        string projectRoot,
        AiGenerationStorageScope scope,
        bool candidate)
    {
        string outputDirectory = PrepareOutputDirectory(projectRoot, scope);
        if (!candidate)
        {
            return outputDirectory;
        }

        string candidateDirectory = Path.Combine(outputDirectory, CandidateDirectoryName);
        CreateCheckedDirectory(ValidateProjectRoot(projectRoot), candidateDirectory);
        EnsureSafeDirectoryChain(projectRoot, candidateDirectory);
        return candidateDirectory;
    }

    public static bool TryResolveGeneratedFile(
        string projectRoot,
        string relativePath,
        bool requireFile,
        out string absolutePath)
    {
        absolutePath = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(relativePath)
                || Path.IsPathRooted(relativePath)
                || LooksLikeWindowsAbsolute(relativePath))
            {
                return false;
            }

            string normalized = relativePath.Trim().Replace('\\', '/');
            string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(segment => segment is "." or "..")
                || !SupportedExtensions.Contains(Path.GetExtension(normalized)))
            {
                return false;
            }

            bool isLegacy = segments.Length == 3
                && string.Equals(segments[0], "assets", StringComparison.Ordinal)
                && string.Equals(segments[1], "generated", StringComparison.Ordinal);
            bool isCurrent = segments.Length == 4
                && string.Equals(segments[0], RootDirectoryName, StringComparison.Ordinal);
            bool isCandidate = segments.Length == 5
                && string.Equals(segments[0], RootDirectoryName, StringComparison.Ordinal)
                && string.Equals(segments[3], CandidateDirectoryName, StringComparison.Ordinal);
            if (!isLegacy && !isCurrent && !isCandidate)
            {
                return false;
            }

            string root = ValidateProjectRoot(projectRoot);
            string candidate = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
            EnsureInside(root, candidate);
            string allowedDirectory = isLegacy
                ? Path.Combine(root, "assets", "generated")
                : isCandidate
                    ? Path.Combine(root, RootDirectoryName, segments[1], segments[2], CandidateDirectoryName)
                    : Path.Combine(root, RootDirectoryName, segments[1], segments[2]);
            EnsureInside(Path.GetFullPath(allowedDirectory), candidate);
            EnsureSafeDirectoryChain(root, Path.GetDirectoryName(candidate)!);
            if (requireFile && !File.Exists(candidate))
            {
                return false;
            }
            if (File.Exists(candidate) && IsReparsePoint(new FileInfo(candidate)))
            {
                return false;
            }

            absolutePath = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string ToProjectRelativePath(string projectRoot, string absolutePath)
    {
        string root = ValidateProjectRoot(projectRoot);
        string fullPath = Path.GetFullPath(absolutePath);
        EnsureInside(root, fullPath);
        return Path.GetRelativePath(root, fullPath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static int FindNextNumber(string outputDirectory, string filePrefix)
    {
        int maximum = 0;
        foreach (string path in Directory.EnumerateFileSystemEntries(outputDirectory))
        {
            if (TryParseNumber(Path.GetFileName(path), filePrefix, out int number))
            {
                maximum = Math.Max(maximum, number);
            }
        }
        if (maximum == int.MaxValue)
        {
            throw new AiGenerationException(AiGenerationErrorCode.SaveFailed, "The generated image result number is exhausted.");
        }
        return maximum + 1;
    }

    private static bool NumberExists(string outputDirectory, string filePrefix, int expectedNumber)
    {
        foreach (string path in Directory.EnumerateFileSystemEntries(outputDirectory))
        {
            if (TryParseNumber(Path.GetFileName(path), filePrefix, out int number) && number == expectedNumber)
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryParseNumber(string fileName, string filePrefix, out int number)
    {
        number = 0;
        string resultStem;
        if (fileName.StartsWith('.'))
        {
            if (!fileName.EndsWith(ReservationSuffix, StringComparison.Ordinal))
            {
                return false;
            }
            resultStem = fileName[1..^ReservationSuffix.Length];
        }
        else
        {
            int extensionSeparator = fileName.IndexOf('.');
            resultStem = extensionSeparator < 0 ? fileName : fileName[..extensionSeparator];
        }
        return resultStem.StartsWith(filePrefix, StringComparison.Ordinal)
            && int.TryParse(
                resultStem.AsSpan(filePrefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out number)
            && number > 0;
    }

    private static string SanitizeSegment(string? value, string fallback, int maximumLength)
    {
        string source = (value ?? string.Empty).Trim().Normalize(NormalizationForm.FormKC);
        var result = new StringBuilder(Math.Min(source.Length, maximumLength));
        bool previousWasReplacement = false;
        foreach (char character in source)
        {
            bool unsafeCharacter = char.IsControl(character)
                || character is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|';
            char next = unsafeCharacter ? '-' : character;
            if (next == '-' && previousWasReplacement)
            {
                continue;
            }
            result.Append(next);
            previousWasReplacement = next == '-';
            if (result.Length >= maximumLength)
            {
                break;
            }
        }

        string sanitized = result.ToString().Trim(' ', '.', '-');
        if (sanitized.Length == 0 || sanitized is "." or "..")
        {
            return fallback;
        }
        if (IsWindowsReservedName(sanitized))
        {
            sanitized = "_" + sanitized;
        }
        return sanitized;
    }

    private static bool IsWindowsReservedName(string value)
    {
        int dot = value.IndexOf('.');
        string stem = (dot >= 0 ? value[..dot] : value).TrimEnd(' ', '.');
        return WindowsReservedNames.Contains(stem);
    }

    private static HashSet<string> CreateWindowsReservedNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CON", "PRN", "AUX", "NUL" };
        for (int index = 1; index <= 9; index++)
        {
            names.Add("COM" + index);
            names.Add("LPT" + index);
        }
        return names;
    }

    private static string ValidateProjectRoot(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            throw new AiGenerationException(AiGenerationErrorCode.SaveFailed, "The project root is unavailable or unsafe.");
        }
        string root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root) || IsReparsePoint(new DirectoryInfo(root)))
        {
            throw new AiGenerationException(AiGenerationErrorCode.SaveFailed, "The project root is unavailable or unsafe.");
        }
        return root;
    }

    private static void CreateCheckedDirectory(string root, string directory)
    {
        EnsureInside(root, directory);
        if (File.Exists(directory))
        {
            throw new AiGenerationException(AiGenerationErrorCode.SaveFailed, "The generated image directory is unsafe.");
        }
        if (Directory.Exists(directory) && IsReparsePoint(new DirectoryInfo(directory)))
        {
            throw new AiGenerationException(AiGenerationErrorCode.SaveFailed, "The generated image directory contains a symbolic link or reparse point.");
        }
        Directory.CreateDirectory(directory);
        if (IsReparsePoint(new DirectoryInfo(directory)))
        {
            throw new AiGenerationException(AiGenerationErrorCode.SaveFailed, "The generated image directory contains a symbolic link or reparse point.");
        }
    }

    private static string ResolveExistingDirectoryPath(string parentDirectory, string requestedName)
    {
        StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        foreach (string directory in Directory.EnumerateDirectories(parentDirectory))
        {
            if (string.Equals(Path.GetFileName(directory), requestedName, comparison))
            {
                return directory;
            }
        }
        throw new AiGenerationException(AiGenerationErrorCode.SaveFailed, "The generated image directory could not be resolved.");
    }

    private static void EnsureSafeDirectoryChain(string root, string directory)
    {
        EnsureInside(root, directory);
        string relative = Path.GetRelativePath(root, Path.GetFullPath(directory));
        string current = root;
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (string.IsNullOrEmpty(segment) || segment == ".")
            {
                continue;
            }
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) && IsReparsePoint(new DirectoryInfo(current)))
            {
                throw new AiGenerationException(AiGenerationErrorCode.SaveFailed, "The generated image path contains a symbolic link or reparse point.");
            }
        }
    }

    private static void EnsureInside(string root, string path)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        if (Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new AiGenerationException(AiGenerationErrorCode.SaveFailed, "The generated image path is outside its allowed directory.");
        }
    }

    private static bool LooksLikeWindowsAbsolute(string path) =>
        path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && path[2] is '/' or '\\';

    private static bool IsReparsePoint(FileSystemInfo info)
    {
        try
        {
            return (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget != null;
        }
        catch
        {
            throw new AiGenerationException(AiGenerationErrorCode.SaveFailed, "The generated image path could not be validated.");
        }
    }
}

internal sealed class GeneratedImageFileReservation : IDisposable
{
    private readonly string _reservationPath;
    private bool _committed;

    internal GeneratedImageFileReservation(FileStream stream, string reservationPath, string finalPath)
    {
        Stream = stream;
        _reservationPath = reservationPath;
        FinalPath = finalPath;
    }

    public FileStream Stream { get; }
    public string FinalPath { get; }

    public void Commit()
    {
        Stream.Dispose();
        File.Move(_reservationPath, FinalPath);
        _committed = true;
    }

    public void Dispose()
    {
        Stream.Dispose();
        if (_committed)
        {
            return;
        }
        try
        {
            File.Delete(_reservationPath);
        }
        catch
        {
            // Cleanup failure must not replace the primary save error.
        }
    }
}
