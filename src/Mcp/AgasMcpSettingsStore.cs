using Godot;

namespace AppAgasCs.Mcp;

public sealed class AgasMcpSettingsStore
{
    public const string SettingsPath = "user://agas-mcp-settings.tres";

    public AgasMcpSettingsResource Load()
    {
        if (!FileAccess.FileExists(SettingsPath))
        {
            return new AgasMcpSettingsResource();
        }

        try
        {
            Resource? loaded = ResourceLoader.Load(SettingsPath);
            return loaded as AgasMcpSettingsResource ?? new AgasMcpSettingsResource();
        }
        catch
        {
            return new AgasMcpSettingsResource();
        }
    }

    public Error Save(AgasMcpSettingsResource settings)
    {
        return ResourceSaver.Save(settings, SettingsPath);
    }

    public AgasMcpSettingsResource LoadOrCreate()
    {
        AgasMcpSettingsResource settings = Load();
        if (!FileAccess.FileExists(SettingsPath))
        {
            Save(settings);
        }

        return settings;
    }
}
