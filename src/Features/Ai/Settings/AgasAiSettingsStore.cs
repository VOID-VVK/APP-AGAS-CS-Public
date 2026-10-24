using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AppAgasCs.Features.Ai.Settings;

public sealed class AgasAiSettingsStore
{
    private static readonly Regex SafeIdPattern = new("^[A-Za-z0-9_-]{1,120}$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly object _gate;
    private readonly Action<AgasAiSettingsWriteTarget>? _beforeAtomicReplace;

    public AgasAiSettingsStore(string? homeDirectory = null)
    {
        HomeDirectory = AgasAiSettingsSharedGate.CanonicalizePath(
            homeDirectory ?? AgasAiSettingsPaths.ResolveHome());
        _gate = AgasAiSettingsSharedGate.ForProfilesPath(ProfilesPath);
    }

    internal AgasAiSettingsStore(string homeDirectory, Action<AgasAiSettingsWriteTarget> beforeAtomicReplace)
        : this(homeDirectory)
    {
        _beforeAtomicReplace = beforeAtomicReplace ?? throw new ArgumentNullException(nameof(beforeAtomicReplace));
    }

    public string HomeDirectory { get; }
    public string ProfilesPath => AgasAiSettingsPaths.ProfilesFile(HomeDirectory);
    public string? CredentialMigrationWarning { get; private set; }

    internal object SharedGate => _gate;

    public AgasAiProfilesDocument LoadProfiles()
    {
        lock (_gate)
        {
            return LoadProfilesUnsafe();
        }
    }

    private AgasAiProfilesDocument LoadProfilesUnsafe()
    {
        if (!File.Exists(ProfilesPath))
        {
            return AgasAiProfilesDocument.Empty;
        }

        try
        {
            string json = File.ReadAllText(ProfilesPath);
            AgasAiProfilesDocument loaded = DeserializeProfiles(json, out bool migrated);
            AgasAiProfilesDocument normalized = ValidateAndNormalize(loaded);
            if (migrated)
            {
                normalized = ImportLegacyCredentials(normalized);
                EnsurePrivateHomeDirectory();
                AtomicWriteJson(ProfilesPath, normalized, AgasAiSettingsWriteTarget.Profiles);
            }
            return normalized;
        }
        catch
        {
            throw new InvalidDataException("AI 中转站配置文件无效。");
        }
    }

    private AgasAiProfilesDocument ImportLegacyCredentials(AgasAiProfilesDocument document)
    {
        string path = Path.Combine(HomeDirectory, "credentials.json");
        if (!File.Exists(path) || !document.Profiles.Any(CanImportLegacyCredential))
            return document;
        try
        {
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException();
            using JsonDocument raw = JsonDocument.Parse(File.ReadAllText(path));
            if (!raw.RootElement.TryGetProperty("resourceVersion", out var version)
                || !version.TryGetInt32(out int resourceVersion) || resourceVersion != 1
                || !raw.RootElement.TryGetProperty("credentials", out var values)
                || values.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException();
            var credentials = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty property in values.EnumerateObject())
            {
                ValidateProfileId(property.Name);
                string? value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl)
                    || !credentials.TryAdd(property.Name, value))
                    throw new InvalidDataException();
            }
            return ValidateAndNormalize(new AgasAiProfilesDocument(
                document.ResourceVersion,
                document.ActiveProfileId,
                document.ActiveModelName,
                document.Profiles.Select(profile => CanImportLegacyCredential(profile)
                    && credentials.TryGetValue(profile.Id, out string? credential)
                        ? profile with { ApiKey = credential }
                        : profile).ToArray()));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
                                         or JsonException or InvalidOperationException or ArgumentException)
        {
            // Keep both the legacy file and every profile. No parser details or
            // credential contents may enter diagnostics; new connections still work.
            CredentialMigrationWarning = "旧凭据未能导入，原文件已保留。请为需要的旧连接重新填写 API Key。";
            return document;
        }
    }

    private static bool CanImportLegacyCredential(AgasAiProfile profile) =>
        profile.ApiKey == null && profile.ProviderId != AgasAiProviderIds.AxonCatNewApi;

    public void SaveProfiles(AgasAiProfilesDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        AgasAiProfilesDocument normalized = ValidateAndNormalize(document);
        lock (_gate)
        {
            EnsurePrivateHomeDirectory();
            AtomicWriteJson(ProfilesPath, normalized, AgasAiSettingsWriteTarget.Profiles);
        }
    }

    internal void SaveProfilesCoordinated(
        AgasAiProfilesDocument document,
        Action beforeAtomicReplace)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(beforeAtomicReplace);
        AgasAiProfilesDocument normalized = ValidateAndNormalize(document);
        lock (_gate)
        {
            EnsurePrivateHomeDirectory();
            AtomicWriteJson(
                ProfilesPath,
                normalized,
                AgasAiSettingsWriteTarget.Profiles,
                beforeAtomicReplace);
        }
    }

    internal IReadOnlyList<string> LoadCredentialValuesInsideSharedGate()
    {
        lock (_gate)
        {
            return Array.AsReadOnly(LoadProfilesUnsafe().Profiles
                .Select(profile => profile.ApiKey)
                .Where(value => !string.IsNullOrEmpty(value))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray());
        }
    }

    internal AgasAiSettingsPersistedFingerprint CapturePersistedFingerprint(
        AgasAiProfilesDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        lock (_gate)
        {
            FileInfo info = new(ProfilesPath);
            bool exists = info.Exists;
            byte[] contents = exists ? File.ReadAllBytes(ProfilesPath) : Array.Empty<byte>();
            string documentText = string.Join("\n", new[]
            {
                document.ResourceVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                document.ActiveProfileId ?? string.Empty,
                document.ActiveModelName,
                string.Join("\n", document.Profiles.Select(profile => string.Join("\u001f", new[]
                {
                    profile.Id,
                    profile.Name,
                    profile.ProviderId,
                    profile.ModelName,
                    profile.BaseUrl,
                }))),
            });
            var file = new AgasAiSettingsFileFingerprint(
                exists,
                exists ? info.Length : 0,
                exists ? info.LastWriteTimeUtc.Ticks : 0,
                exists ? info.CreationTimeUtc.Ticks : 0,
                exists ? info.Attributes.ToString() : string.Empty,
                exists ? info.LinkTarget ?? string.Empty : string.Empty,
                Convert.ToHexString(SHA256.HashData(contents)));
            return new AgasAiSettingsPersistedFingerprint(
                file,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(documentText))));
        }
    }

    public void SaveProfilesAndCredential(AgasAiProfilesDocument document, string profileId, string? credential)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateProfileId(profileId);
        if (credential != null && string.IsNullOrWhiteSpace(credential))
        {
            throw new ArgumentException("凭据不能为空。", nameof(credential));
        }

        AgasAiProfilesDocument normalized = ValidateAndNormalize(document);
        if (!normalized.Profiles.Any(profile => profile.Id == profileId))
        {
            throw new ArgumentException("AI 中转站不存在。", nameof(profileId));
        }

        if (credential != null)
        {
            normalized = WithProfileApiKey(normalized, profileId, credential);
        }

        lock (_gate)
        {
            FileSnapshot profilesBefore = Capture(ProfilesPath);
            try
            {
                EnsurePrivateHomeDirectory();
                AtomicWriteJson(ProfilesPath, normalized, AgasAiSettingsWriteTarget.Profiles);
            }
            catch
            {
                bool restored = TryRestoreSnapshot(profilesBefore);
                throw new IOException(restored
                    ? "AI 中转站保存失败，原配置已恢复。"
                    : "AI 中转站保存失败，且原配置恢复失败。");
            }
        }
    }

    public void DeleteProfileAndCredential(AgasAiProfilesDocument document, string profileId)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateProfileId(profileId);
        AgasAiProfilesDocument normalized = ValidateAndNormalize(document);
        if (normalized.Profiles.Any(profile => profile.Id == profileId))
        {
            throw new ArgumentException("待删除的 AI 中转站仍存在于配置中。", nameof(profileId));
        }

        lock (_gate)
        {
            FileSnapshot profilesBefore = Capture(ProfilesPath);
            try
            {
                EnsurePrivateHomeDirectory();
                AtomicWriteJson(ProfilesPath, normalized, AgasAiSettingsWriteTarget.Profiles);
            }
            catch
            {
                bool restored = TryRestoreSnapshot(profilesBefore);
                throw new IOException(restored
                    ? "AI 中转站删除失败，原配置已恢复。"
                    : "AI 中转站删除失败，且原配置恢复失败。");
            }
        }
    }

    public bool HasCredential(string profileId)
    {
        return !string.IsNullOrEmpty(GetCredential(profileId));
    }

    public string? GetCredential(string profileId)
    {
        ValidateProfileId(profileId);
        lock (_gate)
        {
            return FindProfileApiKey(LoadProfilesUnsafe(), profileId);
        }
    }

    public void SetCredential(string profileId, string credential)
    {
        ValidateProfileId(profileId);
        if (string.IsNullOrWhiteSpace(credential))
        {
            throw new ArgumentException("凭据不能为空。", nameof(credential));
        }

        lock (_gate)
        {
            AgasAiProfilesDocument document = LoadProfilesUnsafe();
            if (!document.Profiles.Any(profile => profile.Id == profileId))
            {
                throw new InvalidOperationException("AI 中转站不存在。");
            }

            EnsurePrivateHomeDirectory();
            AtomicWriteJson(
                ProfilesPath,
                WithProfileApiKey(document, profileId, credential),
                AgasAiSettingsWriteTarget.Profiles);
        }
    }

    public void DeleteCredential(string profileId)
    {
        ValidateProfileId(profileId);
        lock (_gate)
        {
            AgasAiProfilesDocument document = LoadProfilesUnsafe();
            if (FindProfileApiKey(document, profileId) == null)
            {
                return;
            }

            EnsurePrivateHomeDirectory();
            AtomicWriteJson(
                ProfilesPath,
                WithProfileApiKey(document, profileId, null),
                AgasAiSettingsWriteTarget.Profiles);
        }
    }

    private static string? FindProfileApiKey(AgasAiProfilesDocument document, string profileId) =>
        document.Profiles.FirstOrDefault(profile => profile.Id == profileId)?.ApiKey;

    private static AgasAiProfilesDocument WithProfileApiKey(
        AgasAiProfilesDocument document,
        string profileId,
        string? apiKey)
    {
        return new AgasAiProfilesDocument(
            document.ResourceVersion,
            document.ActiveProfileId,
            document.ActiveModelName,
            document.Profiles.Select(profile =>
                profile.Id == profileId ? profile with { ApiKey = apiKey } : profile).ToArray());
    }

    private void EnsurePrivateHomeDirectory()
    {
        Directory.CreateDirectory(HomeDirectory);
        TrySetPrivateDirectoryMode(HomeDirectory);
    }

    private void AtomicWriteJson<T>(
        string targetPath,
        T value,
        AgasAiSettingsWriteTarget writeTarget,
        Action? coordinatedBeforeAtomicReplace = null)
    {
        string tempPath = Path.Combine(
            Path.GetDirectoryName(targetPath) ?? throw new InvalidOperationException("设置目录无效。"),
            $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(json);
                stream.Flush(flushToDisk: true);
            }

            TrySetPrivateFileMode(tempPath);
            _beforeAtomicReplace?.Invoke(writeTarget);
            coordinatedBeforeAtomicReplace?.Invoke();
            File.Move(tempPath, targetPath, overwrite: true);
            TrySetPrivateFileMode(targetPath);
        }
        catch (AgasAiSettingsConflictException)
        {
            TryDeleteTempFile(tempPath);
            throw;
        }
        catch (AgasAiSettingsCommitRejectedException)
        {
            TryDeleteTempFile(tempPath);
            throw;
        }
        catch (OperationCanceledException)
        {
            TryDeleteTempFile(tempPath);
            throw;
        }
        catch
        {
            TryDeleteTempFile(tempPath);
            throw new IOException("AI 中转站配置保存失败。");
        }
    }

    private static FileSnapshot Capture(string path) => File.Exists(path)
        ? new FileSnapshot(path, true, File.ReadAllBytes(path))
        : new FileSnapshot(path, false, Array.Empty<byte>());

    private bool TryRestoreSnapshot(FileSnapshot snapshot)
    {
        try
        {
            if (!snapshot.Existed)
            {
                File.Delete(snapshot.Path);
                return true;
            }

            EnsurePrivateHomeDirectory();
            AtomicWriteBytes(snapshot.Path, snapshot.Contents);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void AtomicWriteBytes(string targetPath, byte[] contents)
    {
        string tempPath = Path.Combine(
            Path.GetDirectoryName(targetPath) ?? throw new InvalidOperationException("设置目录无效。"),
            $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }
            TrySetPrivateFileMode(tempPath);
            File.Move(tempPath, targetPath, overwrite: true);
            TrySetPrivateFileMode(targetPath);
        }
        catch
        {
            TryDeleteTempFile(tempPath);
            throw;
        }
    }

    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            File.Delete(tempPath);
        }
        catch
        {
            // Best effort cleanup; never add sensitive contents to the exception path.
        }
    }

    private static void TrySetPrivateDirectoryMode(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (PlatformNotSupportedException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static void TrySetPrivateFileMode(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (PlatformNotSupportedException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static AgasAiProfilesDocument ValidateAndNormalize(AgasAiProfilesDocument document)
    {
        if (document.ResourceVersion is not 1 and not 2 and not 3 and not 4
            && document.ResourceVersion != AgasAiProfilesDocument.CurrentResourceVersion)
        {
            throw new InvalidDataException("不支持的 AI 中转站配置版本。");
        }

        List<AgasAiProfile> profiles = document.Profiles?.ToList() ?? new List<AgasAiProfile>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < profiles.Count; i++)
        {
            AgasAiProfile profile = NormalizeApiKey(profiles[i]);
            ValidateProfile(profile);
            if (!ids.Add(profile.Id))
            {
                throw new InvalidDataException("AI 中转站 ID 重复。");
            }

            profiles[i] = profile;
        }

        string? activeId = string.IsNullOrWhiteSpace(document.ActiveProfileId) ? null : document.ActiveProfileId;
        if (activeId != null && !ids.Contains(activeId))
        {
            throw new InvalidDataException("当前 AI 中转站不存在。");
        }

        string activeModelName = document.ResourceVersion < AgasAiProfilesDocument.CurrentResourceVersion
            ? AgasAiProfilesDocument.ResolveLegacyActiveModel(activeId, profiles)
            : document.ActiveModelName.Trim();
        if (!AgasAiProfilesDocument.IsValidActiveModelName(activeModelName))
        {
            throw new InvalidDataException("AI 模型名必须为 1-200 个字符且不能包含控制字符。");
        }

        return new AgasAiProfilesDocument(
            AgasAiProfilesDocument.CurrentResourceVersion,
            activeId,
            activeModelName,
            profiles);
    }

    private static void ValidateProfile(AgasAiProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ValidateProfileId(profile.Id);
        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 120)
        {
            throw new InvalidDataException("AI 中转站名称必须为 1-120 个字符。");
        }
        if (string.IsNullOrWhiteSpace(profile.ProviderId)
            || !Regex.IsMatch(profile.ProviderId, "^[a-z0-9][a-z0-9._-]{0,79}$", RegexOptions.CultureInvariant))
        {
            throw new InvalidDataException("AI Provider ID 无效。");
        }
        if (!Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidDataException("AI Base URL 必须是无用户信息、查询参数和片段的安全 HTTP(S) 绝对地址。");
        }
        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
        {
            throw new InvalidDataException("AI Base URL 必须使用 HTTPS；仅 loopback 地址允许 HTTP。");
        }
        if (profile.ApiKey != null && (profile.ApiKey.Length > 4096 || profile.ApiKey.Any(char.IsControl)))
        {
            throw new InvalidDataException("AI API Key 无效。");
        }
    }

    private static AgasAiProfile NormalizeApiKey(AgasAiProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.ApiKey))
        {
            return profile with { ApiKey = null };
        }

        return profile with { ApiKey = profile.ApiKey.Trim() };
    }

    private static void ValidateProfileId(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId) || !SafeIdPattern.IsMatch(profileId))
        {
            throw new ArgumentException("AI 中转站 ID 无效。", nameof(profileId));
        }
    }

    private static AgasAiProfilesDocument DeserializeProfiles(string json, out bool migrated)
    {
        using JsonDocument raw = JsonDocument.Parse(json);
        if (!raw.RootElement.TryGetProperty("resourceVersion", out JsonElement versionElement)
            || !versionElement.TryGetInt32(out int version))
        {
            throw new InvalidDataException();
        }
        if (version is 3 or 4 || version == AgasAiProfilesDocument.CurrentResourceVersion)
        {
            migrated = version < AgasAiProfilesDocument.CurrentResourceVersion;
            return JsonSerializer.Deserialize<AgasAiProfilesDocument>(json, JsonOptions)
                ?? throw new InvalidDataException();
        }
        if (version is not 1 and not 2)
        {
            throw new InvalidDataException();
        }

        LegacyProfilesDocument legacy = JsonSerializer.Deserialize<LegacyProfilesDocument>(json, JsonOptions)
            ?? throw new InvalidDataException();
        List<AgasAiProfile> profiles = legacy.Profiles.Select(profile =>
        {
            var migratedProfile = new AgasAiProfile(
                profile.Id,
                profile.Name,
                AgasAiProviderIds.FromLegacyProviderKind(profile.LegacyProviderKind),
                profile.ModelName,
                profile.BaseUrl);
            return version == 1 ? AgasAiModelCatalog.MigrateVersion1Profile(migratedProfile) : migratedProfile;
        }).ToList();
        migrated = true;
        return new AgasAiProfilesDocument(
            AgasAiProfilesDocument.CurrentResourceVersion,
            legacy.ActiveProfileId,
            AgasAiProfilesDocument.ResolveLegacyActiveModel(legacy.ActiveProfileId, profiles),
            profiles);
    }

    private sealed record LegacyProfilesDocument(
        int ResourceVersion,
        string? ActiveProfileId,
        IReadOnlyList<LegacyProfile> Profiles);

    private sealed record LegacyProfile(
        string Id,
        string Name,
        [property: JsonPropertyName("modelType")] string LegacyProviderKind,
        string ModelName,
        string BaseUrl);

    private sealed record FileSnapshot(string Path, bool Existed, byte[] Contents);
}

internal enum AgasAiSettingsWriteTarget
{
    Profiles,
}
