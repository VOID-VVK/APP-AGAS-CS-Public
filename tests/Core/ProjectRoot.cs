namespace AppAgasCs.Tests.Core;

internal static class ProjectRoot
{
    public static string Path
    {
        get
        {
            string directory = AppContext.BaseDirectory;
            while (!string.IsNullOrEmpty(directory))
            {
                if (File.Exists(System.IO.Path.Combine(directory, "APP-AGAS-CS.csproj")))
                {
                    return directory;
                }

                DirectoryInfo? parent = Directory.GetParent(directory);
                if (parent is null)
                {
                    break;
                }

                directory = parent.FullName;
            }

            throw new DirectoryNotFoundException("未找到 APP-AGAS-CS.csproj 所在项目根目录。");
        }
    }
}
