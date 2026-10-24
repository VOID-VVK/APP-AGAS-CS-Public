using Godot;

namespace AppAgasCs.Mcp;

[GlobalClass]
public partial class AgasMcpSettingsResource : Resource
{
    public const string FixedHost = "127.0.0.1";
    public const int FixedPort = 54119;
    public const string FixedEndpointPath = "/mcp";
    public const string FixedEndpoint = "http://127.0.0.1:54119/mcp";
    public const bool DefaultEnabled = false;

    [Export] public int ResourceVersion { get; set; } = 1;
    [Export] public bool Enabled { get; set; } = DefaultEnabled;

    public string Host => FixedHost;
    public int Port => FixedPort;
    public string EndpointPath => FixedEndpointPath;
    public string Endpoint => FixedEndpoint;
}
