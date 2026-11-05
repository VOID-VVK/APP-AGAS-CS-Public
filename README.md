# APP-AGAS-CS

基于 Godot 4.6 和 C# 的图像资源与画布编辑工作区，支持场景、图层和 AI 图像生成适配器。

## 从源码运行

安装 **Godot 4.6.1 .NET 版**与 **.NET 8 SDK**。克隆本仓库后，在仓库根目录执行 `dotnet build APP-AGAS-CS.csproj`，再用 Godot .NET 编辑器导入 `project.godot` 并运行。macOS 上请使用 .NET 版应用打开项目；标准 Godot 版无法运行此 C# 项目。

源码更新与应用安装包发布独立；可用安装包以 [Releases](https://github.com/VOID-VVK/APP-AGAS-CS-Public/releases) 为准。AI 生成需要自行配置服务地址、API Key 和支持图片参考的模型，项目不附带凭据。

## 使用示例

- [Case 1：用 AI 制作 TileSet 47](docs/examples/tileset47/README.md)：包含参考底图、草地/泥土与海水/沙滩两套完整提示词、反推工具源码和效果示例，按步骤组装场景与工具链。
- [Tile47 双图风格参考场景包](https://github.com/VOID-VVK/AGAS-SCENES/tree/main/scenes/tile47-sand-water)：按已有图片的画风生成固定 Tile47 布局。当前源码支持独立画风参考层、双图输入与场景包往返保存。风格图作为第一张参考，Q 框选模板作为第二张参考；使用所有图层范围，不设置 W。导入后按说明创建只有一步「Tile47 标准图反推」的工具链，从画布右键工具箱执行。

## License

本项目采用 [MIT License](LICENSE)。第三方组件遵循各自的许可协议，详见 [第三方许可说明](THIRD_PARTY_LICENSES.md)。
