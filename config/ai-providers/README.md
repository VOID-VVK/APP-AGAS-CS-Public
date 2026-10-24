# AI Provider 配置与 Adapter SDK

应用首次加载时会把本目录的默认 JSON 描述文件复制到
`$AGAS_HOME/providers`（默认 `~/.agas/providers`）。之后读取用户目录；升级时只追加新版内置模板，不覆盖用户编辑：

默认连接为 **New API**（`https://ainewdev.cc`）。在设置中给该连接填写一次
生图 Key，即可在同一条目下切换 `gpt-image-2`、`gpt-image-2.5-flare`、
`gpt-image-2.5-sunburst`、`grok-imagine-image-quality`、`grok-imagine-image`、
`grok-imagine-image-2.0` 与 `gemini-image`。GPT/Grok 使用 Images 接口，Gemini 使用
Chat Completions 图片接口；可用性仍取决于 Key 的分组、渠道及计费配置。
Gemini 的尺寸通过提示词请求，返回尺寸由渠道决定，不保证精确像素。

首次升级会添加并选中不含 Key 的 New API 连接，原连接和凭据原样保留，旧 Key 不会迁到
新地址。之后保留用户选择，不重复创建；更换服务 origin 时清除原连接的旧 Key，需填写新 Key。
升级不会覆盖已经保存的连接或 Provider JSON；已有安装需在设置中自行修改服务地址，
并填写新服务的 Key。

- 增加同协议 Provider：新增一个独立 `.json` 文件，无需修改 C#、枚举或 UI。
- 修改名称、模型预设、能力、端点提示或排序：编辑对应 JSON 后在生图面板点击刷新；
  已打开的设置窗口需关闭后重新打开。
- `id` 是保存到 Profile 的稳定标识，不应随展示名变化。
- `adapterKind` 指向协议适配器。`newapi-compatible`、`openai-compatible` 与 `gemini-compatible` 已内置。
- 新协议可把实现 `IAiImageGenerationAdapter` 且具有 public 无参构造函数的 DLL 放进
  `$AGAS_HOME/ai-adapters`。DLL 文件和导出类型按名称确定性加载；坏插件会被隔离。
  新增或替换 DLL 后需重启应用；仅修改 Provider JSON 可直接在 UI 刷新。
- Provider 文件不得保存 API Key、token、secret 或 password；Key 写在
  `$AGAS_HOME/image-generation-profiles.json` 每个 profile 的 `apiKey` 字段。
- 正式约束见 `ai-provider.schema.json`（源码树中位于 `config/ai-provider.schema.json`）。
  运行时还强制 HTTPS（loopback 可用 HTTP）、
  唯一推荐模型、唯一 Provider ID、文件大小和 JSON 深度限制。

## Provider JSON 示例

下例复用内置 OpenAI-compatible 协议，因此只需要把 JSON 放进
`$AGAS_HOME/providers/30-company-relay.json`，刷新生图面板或重新打开设置窗口：

```json
{
  "schemaVersion": 1,
  "id": "company-relay",
  "displayName": "Company Image Relay",
  "adapterKind": "openai-compatible",
  "defaultBaseUrl": "https://images.example.com/v1/",
  "models": [
    { "modelName": "company-image-v2", "label": "Company Image v2", "recommended": true }
  ],
  "capabilities": ["textToImage", "referenceEdit"],
  "credential": { "kind": "profileSettings", "label": "API Key", "settingsKey": "apiKey" },
  "baseUrlHint": "Use the image relay API prefix.",
  "baseUrlPlaceholder": "https://images.example.com/v1/",
  "order": 30
}
```

目录只枚举顶层 `*.json`，按文件名排序后加载。每个文件描述一个完整 Provider，不做
跨文件字段覆盖。同一 `id` 出现多次时，为避免加载顺序改变行为，所有冲突文件都会被
隔离；其他有效文件继续可用。Provider 文件不能包含凭据；API Key 写在
`$AGAS_HOME/image-generation-profiles.json` 的对应 profile 上。

## 新协议 Adapter DLL

仅当 HTTP 协议与两个内置 Adapter 都不兼容时才需要 DLL。插件项目使用 `net8.0`，
只引用应用发布目录 `ai-provider-sdk` 中独立、无 Godot/宿主依赖的
`APP-AGAS-CS.AiAdapter.Contracts` 包或 DLL，实现公开接口：

```csharp
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AppAgasCs.Features.Ai.Generation;

public sealed class CompanyImageAdapter : IAiImageGenerationAdapter
{
    public string AdapterKind => "company.image-v1";
    public AiGenerationAdapterCapabilities Capabilities =>
        AiGenerationAdapterCapabilities.TextToImage;

    public Task<GeneratedImageData> GenerateAsync(
        AiGenerationProviderProfile profile,
        AiGenerationRequest request,
        HttpClient providerHttpClient,
        HttpClient imageDownloadClient,
        CancellationToken cancellationToken)
    {
        // 使用注入的 HttpClient；不要新建会绕过宿主安全策略的下载客户端。
        // 凭据为 profile.ApiKey；只用于当前请求，禁止记录或持久化。
        throw new NotImplementedException();
    }
}
```

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
  <ItemGroup>
    <Reference Include="APP-AGAS-CS.AiAdapter.Contracts">
      <HintPath>/path/to/agas/ai-provider-sdk/APP-AGAS-CS.AiAdapter.Contracts.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

也可以把 `ai-provider-sdk` 作为本地 NuGet 源，引用同目录发布的包：

```xml
<PackageReference Include="APP-AGAS-CS.AiAdapter.Contracts" Version="1.0.0" />
```

```bash
dotnet restore --source /path/to/agas/ai-provider-sdk
```

把插件 DLL、它自己的 managed/native 依赖和可选 `.deps.json` 一起复制到
`$AGAS_HOME/ai-adapters`，再重启应用。`adapterKind` 必须是 1-80 位小写字母、数字、
点、下划线或连字符，并与 Provider JSON 完全一致。一个 DLL 中任一 Adapter 无效或与
已加载 kind 冲突时，整个入口 DLL 都会被隔离。被 Adapter 引用的依赖 DLL 会按程序集
引用闭包识别为 sidecar，不会被误报成缺少 Adapter；未被任何 Adapter 引用、也不导出
Adapter 的独立 DLL 仍会报告为坏入口。插件具有与应用进程相同的权限，只加载可信 DLL；
更新/删除 DLL 后必须重启，Provider JSON 则按每次 UI 刷新重新加载。

## 默认文件与 Profile 迁移

首次加载会按“仅缺失时复制”把内置 JSON 写入用户目录并留下 schema marker，之后不会
覆盖用户编辑。New API 模板单独使用增量 marker，已有安装也能看到新入口；删除已完成
初始化的模板/连接不会被下次刷新自动恢复。Profile v1/v2 的旧类型会迁移为稳定 `providerId`；
v1-v3 配置统一升级为 v4，将旧 `credentials.json` 按已存在 Profile 的精确 ID 一次性导入。
已有内嵌 Key 优先，New API 连接不导入旧 Key；旧凭据文件原样保留便于回滚。
旧文件损坏时保留连接、显示本次运行的提示，并要求为受影响连接重新填写 Key；
此后修复旧文件也不会自动重新导入。
v4 不再回读旧文件，避免清除 Key 或更换地址后恢复旧凭据。API Key 保存在同一份
`image-generation-profiles.json` 里，使用私有文件权限。
