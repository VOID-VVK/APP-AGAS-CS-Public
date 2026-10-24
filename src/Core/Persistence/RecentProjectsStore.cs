using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AppAgasCs.Core.Resources;
using Godot;

namespace AppAgasCs.Core.Persistence;

public sealed class RecentProjectsStore
{
    public const string RecentProjectsPath = "user://recent-projects.tres";
    private const int MaxEntries = 20;
    private readonly string _storagePath;

    public RecentProjectsStore(string storagePath = RecentProjectsPath)
    {
        _storagePath = string.IsNullOrWhiteSpace(storagePath) ? RecentProjectsPath : storagePath;
    }

    public IReadOnlyList<RecentProjectEntryResource> LoadEntries()
    {
        RecentProjectsResource resource = LoadResourceOrDefault();
        bool changed = NormalizeEntries(resource, pruneMissingPaths: true);
        if (changed)
        {
            Save(resource);
        }

        return Array.AsReadOnly(resource.Entries.ToArray());
    }

    public void RecordOpened(string projectRoot, string projectName)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return;
        }

        string fullPath = Path.GetFullPath(projectRoot);
        if (!Directory.Exists(fullPath))
        {
            return;
        }

        RecentProjectsResource resource = LoadResourceOrDefault();
        NormalizeEntries(resource, pruneMissingPaths: true);

        RecentProjectEntryResource? existing = resource.Entries.FirstOrDefault(entry =>
            string.Equals(NormalizePathOrEmpty(entry.ProjectPath), fullPath, StringComparison.Ordinal));

        if (existing == null)
        {
            existing = new RecentProjectEntryResource();
            resource.Entries.Add(existing);
        }

        existing.ProjectPath = fullPath;
        existing.ProjectName = string.IsNullOrWhiteSpace(projectName) ? ProjectNameFromPath(fullPath) : projectName.Trim();
        existing.LastOpenedUnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        NormalizeEntries(resource, pruneMissingPaths: true);
        Save(resource);
    }

    public void Remove(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return;
        }

        string fullPath = NormalizePathOrEmpty(projectRoot);
        RecentProjectsResource resource = LoadResourceOrDefault();
        for (int i = resource.Entries.Count - 1; i >= 0; i--)
        {
            if (string.Equals(NormalizePathOrEmpty(resource.Entries[i].ProjectPath), fullPath, StringComparison.Ordinal))
            {
                resource.Entries.RemoveAt(i);
            }
        }

        Save(resource);
    }

    private RecentProjectsResource LoadResourceOrDefault()
    {
        if (!Godot.FileAccess.FileExists(_storagePath))
        {
            return new RecentProjectsResource();
        }

        try
        {
            Resource? loaded = ResourceLoader.Load(_storagePath, cacheMode: ResourceLoader.CacheMode.Ignore);
            return loaded as RecentProjectsResource ?? new RecentProjectsResource();
        }
        catch
        {
            return new RecentProjectsResource();
        }
    }

    private static bool NormalizeEntries(RecentProjectsResource resource, bool pruneMissingPaths)
    {
        bool changed = false;
        var unique = new Dictionary<string, RecentProjectEntryResource>(StringComparer.Ordinal);

        foreach (RecentProjectEntryResource entry in resource.Entries)
        {
            string fullPath = NormalizePathOrEmpty(entry.ProjectPath);
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                changed = true;
                continue;
            }

            if (pruneMissingPaths && !Directory.Exists(fullPath))
            {
                changed = true;
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.ProjectName))
            {
                entry.ProjectName = ProjectNameFromPath(fullPath);
                changed = true;
            }

            entry.ProjectPath = fullPath;
            if (unique.TryGetValue(fullPath, out RecentProjectEntryResource? existing))
            {
                if (entry.LastOpenedUnixTime > existing.LastOpenedUnixTime)
                {
                    unique[fullPath] = entry;
                }
                changed = true;
                continue;
            }

            unique.Add(fullPath, entry);
        }

        List<RecentProjectEntryResource> ordered = unique.Values
            .OrderByDescending(entry => entry.LastOpenedUnixTime)
            .ThenBy(entry => entry.ProjectName, StringComparer.CurrentCulture)
            .Take(MaxEntries)
            .ToList();

        if (resource.Entries.Count != ordered.Count)
        {
            changed = true;
        }

        resource.Entries.Clear();
        foreach (RecentProjectEntryResource entry in ordered)
        {
            resource.Entries.Add(entry);
        }

        return changed;
    }

    private void Save(RecentProjectsResource resource)
    {
        Error saveError = ResourceSaver.Save(resource, _storagePath);
        if (saveError != Error.Ok)
        {
            GD.PushWarning($"保存最近项目失败：{saveError}");
        }
    }

    private static string NormalizePathOrEmpty(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ProjectNameFromPath(string projectRoot)
    {
        string trimmed = projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? projectRoot : name;
    }
}
