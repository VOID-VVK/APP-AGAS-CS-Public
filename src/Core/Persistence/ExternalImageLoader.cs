using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace AppAgasCs.Core.Persistence;

public static class ExternalImageLoader
{
    private static readonly string[] SupportedExtensionValues = { ".png", ".jpg", ".jpeg", ".webp" };

    public static IReadOnlyList<string> SupportedExtensions => Array.AsReadOnly(SupportedExtensionValues);

    public static AgasLoadResult<Image> LoadProjectImage(string projectRoot, string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return AgasLoadResult<Image>.Fail("图片加载失败：ImagePath 为空。");
        }

        if (Path.IsPathRooted(imagePath) || imagePath.Contains("://", StringComparison.Ordinal) || imagePath.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return AgasLoadResult<Image>.Fail("图片加载失败：ImagePath 必须是项目相对路径，不能是绝对路径、URL 或 data URI。");
        }

        string extension = Path.GetExtension(imagePath);
        if (!Array.Exists(SupportedExtensionValues, item => string.Equals(item, extension, StringComparison.OrdinalIgnoreCase)))
        {
            return AgasLoadResult<Image>.Fail($"图片加载失败：不支持的图片扩展名：{extension}");
        }

        string absolutePath;
        try
        {
            absolutePath = AgasPaths.ToAbsoluteProjectPath(projectRoot, imagePath);
        }
        catch (Exception ex)
        {
            return AgasLoadResult<Image>.Fail($"图片加载失败：{ex.Message}");
        }

        if (!File.Exists(absolutePath))
        {
            return AgasLoadResult<Image>.Fail($"图片加载失败：文件不存在：{absolutePath}");
        }

        Image? image = Image.LoadFromFile(absolutePath);
        if (image is null || image.IsEmpty())
        {
            return AgasLoadResult<Image>.Fail($"图片加载失败：Image.LoadFromFile 未返回有效图片：{absolutePath}");
        }

        return AgasLoadResult<Image>.Ok(image);
    }
}
