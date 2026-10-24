using System;
using System.IO;
using Godot;

namespace AppAgasCs.Core.Persistence;

public sealed class AgasResourceRepository
{
    public AgasPersistenceResult Save(Resource resource, string absoluteTargetPath)
    {
        if (resource is null)
        {
            return AgasPersistenceResult.Fail("保存失败：Resource 为空。");
        }

        try
        {
            string targetPath = Path.GetFullPath(absoluteTargetPath);
            string? directory = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return AgasPersistenceResult.Fail($"保存失败：目标路径无效：{absoluteTargetPath}");
            }

            Directory.CreateDirectory(directory);
            string tempPath = targetPath + ".tmp.tres";
            if (Directory.Exists(tempPath))
            {
                return AgasPersistenceResult.Fail($"保存失败：临时路径被目录占用：{tempPath}");
            }

            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            Error saveError = ResourceSaver.Save(resource, tempPath);
            if (saveError != Error.Ok)
            {
                TryDeleteTemp(tempPath);
                return AgasPersistenceResult.Fail($"保存失败：ResourceSaver.Save 返回 {saveError}：{targetPath}");
            }

            if (!File.Exists(tempPath))
            {
                return AgasPersistenceResult.Fail($"保存失败：临时文件未生成：{tempPath}");
            }

            File.Move(tempPath, targetPath, overwrite: true);
            TryDeleteTemp(tempPath);
            return AgasPersistenceResult.Ok();
        }
        catch (Exception ex)
        {
            TryDeleteTemp(Path.GetFullPath(absoluteTargetPath) + ".tmp.tres");
            return AgasPersistenceResult.Fail($"保存失败：{ex.Message}");
        }
    }

    public AgasLoadResult<T> Load<T>(string absolutePath) where T : Resource
    {
        try
        {
            string fullPath = Path.GetFullPath(absolutePath);
            if (!File.Exists(fullPath))
            {
                return AgasLoadResult<T>.Fail($"加载失败：文件不存在：{fullPath}");
            }

            Resource? loaded = ResourceLoader.Load(fullPath);
            if (loaded is T typed)
            {
                return AgasLoadResult<T>.Ok(typed);
            }

            string actualType = loaded?.GetType().FullName ?? "null";
            return AgasLoadResult<T>.Fail($"加载失败：Resource 类型不匹配，期望 {typeof(T).FullName}，实际 {actualType}：{fullPath}");
        }
        catch (Exception ex)
        {
            return AgasLoadResult<T>.Fail($"加载失败：{ex.Message}");
        }
    }

    /// <summary>
    /// Loads a resource without consulting or populating the shared ResourceLoader cache.
    /// Callers remain responsible for invoking this narrow port on the Godot main thread.
    /// </summary>
    public AgasLoadResult<T> LoadFresh<T>(string absolutePath) where T : Resource
        => LoadCore<T>(absolutePath, ResourceLoader.CacheMode.Ignore);

    private static AgasLoadResult<T> LoadCore<T>(
        string absolutePath,
        ResourceLoader.CacheMode cacheMode) where T : Resource
    {
        try
        {
            string fullPath = Path.GetFullPath(absolutePath);
            if (!File.Exists(fullPath))
            {
                return AgasLoadResult<T>.Fail($"加载失败：文件不存在：{fullPath}");
            }

            Resource? loaded = ResourceLoader.Load(fullPath, cacheMode: cacheMode);
            if (loaded is T typed)
            {
                return AgasLoadResult<T>.Ok(typed);
            }

            string actualType = loaded?.GetType().FullName ?? "null";
            return AgasLoadResult<T>.Fail($"加载失败：Resource 类型不匹配，期望 {typeof(T).FullName}，实际 {actualType}：{fullPath}");
        }
        catch (Exception ex)
        {
            return AgasLoadResult<T>.Fail($"加载失败：{ex.Message}");
        }
    }

    private static void TryDeleteTemp(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch
        {
            // 删除残留临时文件失败不覆盖原始保存错误。
        }
    }
}
