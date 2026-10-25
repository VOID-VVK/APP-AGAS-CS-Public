# 第三方许可说明

APP-AGAS-CS 自有代码采用根目录 [LICENSE](LICENSE) 中的 MIT License。
第三方软件的版权和许可仍属于各自权利人，不因本项目采用 MIT 而改变。

本清单依据项目文件、当前本机还原出的 NuGet `project.assets.json` 和各包的
`.nuspec` 许可、版权及源码仓库元数据核对。源码仓库通过 SDK / NuGet 引用这些
组件，不提交其安装包和构建输出。本文件是源码依赖清单；下文另列二进制发行时
需要随实际发行物保留的许可和声明。

## 运行与构建依赖

| 组件 | 当前版本 | 许可及版权 | 官方来源 |
| --- | --- | --- | --- |
| Godot.NET.Sdk；GodotSharp、GodotSharpEditor、Godot.SourceGenerators | 4.6.1 | MIT；Godot Engine contributors | [Godot 源码与许可证](https://github.com/godotengine/godot/blob/14d19694e0c88a3f9e82d899a0400f27a24c176e/LICENSE.txt)、[Godot 许可说明](https://godotengine.org/license/) |
| ModelContextProtocol.AspNetCore；传递依赖 ModelContextProtocol、ModelContextProtocol.Core | 1.4.1 | Apache-2.0（NuGet 包声明；见下方迁移说明）；© Model Context Protocol a Series of LF Projects, LLC. | [C# SDK 源码与许可证](https://github.com/modelcontextprotocol/csharp-sdk/tree/2b7fd35fbe58dfb9f00eae8b3393e1a7361b5e01) |
| Microsoft.Extensions.Configuration.CommandLine、Configuration.EnvironmentVariables、Configuration.Json、Hosting、Logging.Console、Logging.Debug、Logging.EventSource | 10.0.7 | MIT；© Microsoft Corporation. All rights reserved. | [.NET 源码](https://github.com/dotnet/dotnet/tree/b16286c2284fecf303dbc12a0bb152476d662e44)、[.NET Runtime 许可证](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) |
| Microsoft.Extensions.AI.Abstractions（MCP 的传递依赖） | 10.5.2 | MIT；© Microsoft Corporation. All rights reserved. | [源码](https://github.com/dotnet/extensions/tree/2a86d759c251eee39274c191bd9f8e14c58f875a)、[许可证](https://github.com/dotnet/extensions/blob/main/LICENSE.TXT) |
| Microsoft.Extensions 其他传递依赖；System.Diagnostics.DiagnosticSource、System.Diagnostics.EventLog、System.IO.Pipelines、System.Net.ServerSentEvents、System.Text.Encodings.Web、System.Text.Json | 10.0.7 | MIT；© Microsoft Corporation. All rights reserved. | [.NET Runtime](https://github.com/dotnet/runtime)、[许可证](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT)、[第三方声明](https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT) |
| Microsoft.AspNetCore.App 与 .NET 运行时 | 由构建环境中匹配 net8.0 的 SDK / Runtime 决定 | MIT；.NET Foundation、Microsoft 及贡献者；随附第三方组件有各自许可 | [ASP.NET Core 许可证](https://github.com/dotnet/aspnetcore/blob/main/LICENSE.txt)、[第三方声明](https://github.com/dotnet/aspnetcore/blob/main/THIRD-PARTY-NOTICES.txt)、[.NET Runtime 许可证](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) |

MCP 对应提交的 [LICENSE](https://github.com/modelcontextprotocol/csharp-sdk/blob/2b7fd35fbe58dfb9f00eae8b3393e1a7361b5e01/LICENSE)
还包含从 MIT 迁移到 Apache-2.0 的说明，并另行说明其文档贡献的许可。
发行时应保留上游完整许可文件，不能只截取 Apache-2.0 标签。

上述 Microsoft.Extensions 其他传递依赖具体包括：

- Caching.Abstractions；Configuration、Configuration.Abstractions、Configuration.Binder、Configuration.FileExtensions、Configuration.UserSecrets。
- DependencyInjection、DependencyInjection.Abstractions；Diagnostics、Diagnostics.Abstractions。
- FileProviders.Abstractions、FileProviders.Physical、FileSystemGlobbing。
- Hosting.Abstractions；Logging、Logging.Abstractions、Logging.Configuration、Logging.EventLog。
- Options、Options.ConfigurationExtensions、Primitives。

`APP-AGAS-CS.Common.props` 还会从本机构建环境复制部分 ASP.NET Core 运行时 DLL
至应用输出目录。其实际版本和随附声明需要按发行构建环境核对，不能仅依据 NuGet
引用版本确定。

## 测试依赖

| 组件 | 当前版本 | 许可及版权 | 官方来源 |
| --- | --- | --- | --- |
| Microsoft.NET.Test.Sdk、Microsoft.CodeCoverage、Microsoft.TestPlatform.ObjectModel、Microsoft.TestPlatform.TestHost | 17.12.0 | MIT；© Microsoft Corporation. All rights reserved. | [VSTest 源码](https://github.com/microsoft/vstest/tree/bc9161306b23641b0364b8f93d546da4d48da1eb)、[许可证](https://github.com/microsoft/vstest/blob/main/LICENSE) |
| xunit、xunit.assert、xunit.core、xunit.extensibility.core、xunit.extensibility.execution | 2.9.2 | Apache-2.0；Copyright (C) .NET Foundation | [xUnit 源码](https://github.com/xunit/xunit/tree/82543a6df6f5f13b5b70f8a9f9ccb41cd676084f)、[许可与版权说明](https://xunit.net/license) |
| xunit.analyzers | 1.16.0 | Apache-2.0；.NET Foundation | [源码与许可](https://github.com/xunit/xunit.analyzers) |
| xunit.runner.visualstudio | 2.8.2 | Apache-2.0；Copyright (C) .NET Foundation | [源码](https://github.com/xunit/visualstudio.xunit/tree/699d445a1aab46a014bce946edc4995b6b46accf)、[许可与版权说明](https://xunit.net/license) |
| xunit.abstractions | 2.0.3 | Apache-2.0；James Newkirk、Brad Wilson（包作者元数据） | [源码与许可](https://github.com/xunit/abstractions.xunit)、[包元数据中的许可入口](https://raw.githubusercontent.com/xunit/xunit/master/license.txt) |
| Newtonsoft.Json | 13.0.1 | MIT；James Newton-King | [源码与许可证](https://github.com/JamesNK/Newtonsoft.Json/blob/13.0.1/LICENSE.md) |
| System.Reflection.Metadata | 1.6.0 | MIT；Copyright (c) .NET Foundation and Contributors | [包对应的 .NET CoreFX 许可证](https://github.com/dotnet/corefx/blob/master/LICENSE.TXT)、[第三方声明](https://github.com/dotnet/corefx/blob/master/THIRD-PARTY-NOTICES.TXT) |

`System.Reflection.Metadata` 1.6.0 的旧包没有 SPDX `license` 元素，已根据本机包内
`LICENSE.TXT` 确认为 MIT，且该包包含 `THIRD-PARTY-NOTICES.TXT`。
`xunit.abstractions` 2.0.3 的旧包通过 `licenseUrl` 指向 xUnit 许可证。
测试依赖不代表应用运行时必然包含这些组件。

## 素材与服务

仓库附带 [Case 1：TileSet 47](docs/examples/tileset47/README.md) 的教学图片：
`reference/tilemap47-guide.png` 是项目维护者提供的原布局参考；`samples/` 四张图片是
维护者确认的 AI 生成原图及其标准图反推结果。具体来源、尺寸和 SHA-256 见
[sources.json](docs/examples/tileset47/sources.json)。它们不是从第三方素材站下载的素材包。
这些图片用于展示本教程；代码的 MIT 许可不替代原生成服务对模型和生成内容的条款，
也不构成额外的第三方权利或商业使用保证。提示词及反推工具源码由维护者随本项目提供，
工具源码沿用本项目 MIT 许可。

除上述教学图片外，本清单未发现独立捆绑的第三方字体、音频或素材包。
Godot 编辑器 / 引擎内置资源仍受 Godot 自身的许可和第三方声明约束。
以后引入素材时，应在此记录来源、作者、版本和许可。

AI Provider 配置仅描述服务连接。服务商名称与接口配置不授予对服务、模型、插件
或生成内容的额外许可；使用者自行配置的 API、模型与插件遵循其各自条款。

## 二进制发行时保留的许可和声明

发布包含依赖 DLL、Godot 导出模板或运行时的安装包时，需根据实际打包内容保留：

1. 本项目完整的 MIT `LICENSE`。
2. MIT 组件各自的版权声明和完整许可正文。上方的版权与来源用于定位原始声明，
   不能替代发行物随附的完整正文。
3. Apache-2.0 组件的完整 [Apache License 2.0](https://www.apache.org/licenses/LICENSE-2.0.txt)，
   以及上游若提供的 `NOTICE` 和其他必须保留的版权、专利、商标或归属声明；修改
   上游文件时按其许可标明修改。
4. 所用 Godot 版本的 `LICENSE.txt` 与 `COPYRIGHT.txt`。Godot 的第三方组件归属和
   许可见 [Godot COPYRIGHT.txt](https://github.com/godotengine/godot/blob/14d19694e0c88a3f9e82d899a0400f27a24c176e/COPYRIGHT.txt)。
5. 所用 .NET / ASP.NET Core 运行时及 NuGet 包随附的许可与第三方声明。
   本机 Microsoft.Extensions.Hosting 10.0.7 等包确有 `THIRD-PARTY-NOTICES.TXT`，
   内容涉及其他上游组件的声明，不能因包的主许可为 MIT 就忽略这些文件。

本文件未将上游完整许可和声明全部内嵌，也不声称已生成某个安装包的完整声明集合。
依赖升级或发行内容变化时，应同时更新此清单并核对发行物的许可附件。
