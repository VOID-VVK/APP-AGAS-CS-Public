using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AppAgasCs.Features.Ai.Settings;

/// <summary>One settings/credential boundary for UI, MCP and execution model views.</summary>
public sealed class AgasAiConnectionModelAccess
{
    private readonly AgasAiSettingsStore _store;
    private readonly AgasAiModelDiscovery _discovery;
    private readonly Func<AgasAiProviderCatalog> _providerCatalog;

    public AgasAiConnectionModelAccess(AgasAiSettingsStore store, AgasAiModelDiscovery? discovery = null,
        Func<AgasAiProviderCatalog>? providerCatalog = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _discovery = discovery ?? AgasAiModelDiscovery.Shared;
        _providerCatalog = providerCatalog ?? (() => AgasAiProviderCatalog.Load(_store.HomeDirectory));
    }

    public AgasAiProfile? Connection(string? profileId = null)
    {
        lock (_store.SharedGate)
        {
            AgasAiProfilesDocument document = _store.LoadProfiles();
            string? id = profileId ?? document.ActiveProfileId;
            AgasAiProfile? connection = document.Profiles.FirstOrDefault(profile =>
                string.Equals(profile.Id, id, StringComparison.Ordinal));
            return connection == null ? null : connection with { ApiKey = _store.GetCredential(connection.Id) };
        }
    }

    public AgasAiModelSnapshot Snapshot(string? profileId = null)
    {
        AgasAiProfile? connection = Connection(profileId);
        return Unavailable(connection) ?? _discovery.GetSnapshot(connection!);
    }

    public Task<AgasAiModelSnapshot> RefreshAsync(string? profileId = null, CancellationToken cancellationToken = default)
    {
        AgasAiProfile? connection = Connection(profileId);
        AgasAiModelSnapshot? unavailable = Unavailable(connection);
        return unavailable != null ? Task.FromResult(unavailable) : _discovery.RefreshAsync(connection!, cancellationToken);
    }

    public bool TryGet(string? modelName, out AgasAiUnifiedModel model, string? profileId = null)
    {
        AgasAiModelSnapshot snapshot = Snapshot(profileId);
        model = null!;
        return snapshot.IsReady && snapshot.TryGet(modelName, out model) && model.IsAvailable;
    }

    private AgasAiModelSnapshot? Unavailable(AgasAiProfile? connection)
    {
        string? message = connection == null ? "请先配置 AI 连接。"
            : AgasAiConnectionModelResolver.UsesRetiredProtocol(connection, _providerCatalog())
                ? "旧 Google 私有协议已停用。请在设置中改为统一 API 地址并重新保存连接及 Key。"
                : string.IsNullOrWhiteSpace(connection.ApiKey) ? "请为当前连接填写 API Key。" : null;
        return message == null ? null : new AgasAiModelSnapshot(
            Array.Empty<AgasAiUnifiedModel>(), "error", message, string.Empty, false);
    }
}
