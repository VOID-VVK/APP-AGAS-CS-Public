# AGAS 主工作区 MVP 执行计划

> 文件：`docs/exec-plans/active/agas-main-workspace-execution-plan.md`  
> 项目：仓库根目录 `APP-AGAS-CS`
> 技术固定：Godot `4.6.1` Mono / `.NET 8` / 全 C#。  
> 目标：第一个最小 Demo，基础功能完整，未提功能不扩展。  
> 审计：开发前聚焦审计仍须 P0/P1 清零，但不固定轮次。

## 0. 状态与边界

### 已验证事实

- 本计划文件存在，修改前为 `301` 行。
- 用户要求只修改本文件，并压缩为不超过 `350` 行。
- 用户明确第一版只做 MVP，不做扩展功能。

### 待验证项

- Godot Mono `4.6.1`、`.NET 8`、当前工程构建状态：Phase 1 命令验证。
- `.agas/*.tres` custom Resource 保存、关闭重开恢复：Phase 4 验证。

### MVP 必须做

- 项目目录选择与初始化。
- `Files / Scenes`。
- 左下图片预览。
- 顶部图层 tabs：`visible / locked / current`。
- 无限网格，网格 `SizePx` 默认 `48`，允许 `24` 或其他正整数，支持平移缩放。
- 图片拖入、选中、移动、删除、复制、吸附。
- `undo / redo`。
- 每层 `PromptFragments` 增删改排序启停和合并预览。
- `Properties`。
- splitter / collapse。
- 三个示例 scene。
- 真实 `Resource` 保存，关闭重开恢复。

### 第一版明确不做

AI；登录支付；Steamworks；最近项目；自定义快捷键配置；复杂多文件事务/13 故障；迁移；云端；多窗口；签名公证；Windows 实机；大型 evidence 系统；复杂 manifest wrapper；导出门禁。

## 1. 固定合同

### 技术合同

- 只用 Godot `4.6.1 Mono`、`.NET 8`、C#。
- 禁止新增 GDScript、JavaScript、TypeScript、Node、WebView、Electron。
- Godot 命令固定：

```bash
export GODOT=/Applications/Godot_mono.app/Contents/MacOS/Godot
"$GODOT" --version
```

### 数据合同

产品数据只在用户选择的项目根隐藏目录 `.agas`：

```text
<project-root>/.agas/project.tres
<project-root>/.agas/scenes/<scene-id>.tres
<project-root>/.agas/workspace-layout.tres
```

图片在项目普通目录：

```text
<project-root>/assets/image.png
<project-root>/images/角色.png
```

规则：

- `.agas` 内保存 custom `.tres` Resource。
- 图片不复制进 `.agas`，不转 base64，不嵌入 Resource。
- Resource 中图片字段统一为项目相对路径 `ImagePath`。
- 图片加载统一使用 `Image.LoadFromFile(absoluteImagePath)`。
- 保存第一版只需：`ResourceSaver.Save(resource, tempPath)` 写临时 `.tres`，成功后替换目标文件；失败保留旧文件并向 UI 报错。
- 不做多文件事务；多文件保存允许逐个保存、逐个报错。

### 最小 Resource 模型

```text
AgasProjectResource ResourceVersion,ProjectId,Name,ActiveSceneId,Grid,Scenes
GridSettingsResource ResourceVersion,SizePx,SnapEnabled,Visible
SceneIndexResource ResourceVersion,Id,Name,RelativePath(.agas/scenes/<scene-id>.tres),Order
AgasSceneResource ResourceVersion,Id,Name,ActiveLayerId,Layers,ViewState
AgasLayerResource ResourceVersion,Id,Name,Order,Visible,Locked,Images,PromptFragments
ImageInstanceResource ResourceVersion,Id,ImagePath,Order,X,Y,ScaleX,ScaleY,RotationDegrees,Opacity
PromptFragmentResource ResourceVersion,Id,Text,Enabled,Order
SceneViewStateResource ResourceVersion,CameraX,CameraY,Zoom
WorkspaceLayoutResource ResourceVersion,LeftPanelWidth,LeftPanelCollapsed,PreviewHeight,PreviewCollapsed,RightPanelWidth,RightPanelCollapsed,BottomDockHeight,BottomDockCollapsed,ActiveLeftTab,ActiveBottomTab
```

## 2. 最小目录树

不要扩成 118 个生产文件、37 个测试文件；只保留 MVP 所需。

```text
APP-AGAS-CS/
  project.godot
  APP-AGAS-CS.csproj
  APP-AGAS-CS.sln
  src/
    Resources/
      AgasProjectResource.cs
      AgasSceneResource.cs
      WorkspaceLayoutResource.cs
    Persistence/
      AgasProjectInitializer.cs
      AgasResourceRepository.cs
    Commands/
      UndoRedoHistory.cs
      SceneEditCommands.cs
    UI/
      MainWorkspace.cs
      ProjectPicker.cs
      FilesScenesPanel.cs
      LayerTabs.cs
      PromptPanel.cs
      PropertiesPanel.cs
      SplitterState.cs
    Canvas/
      InfiniteCanvas.cs
      ImageInstanceNode.cs
      CanvasInteractionController.cs
  scenes/
    Main.tscn
  tests/
    AgasResourceRoundTripTests.cs
    AgasProjectInitializationTests.cs
    CanvasCommandUndoRedoTests.cs
    PromptFragmentTests.cs
    WorkspaceRestoreTests.cs
  fixtures/
    demo-project/
      assets/
      .agas/
        project.tres
        scenes/scene-1.tres
        scenes/scene-2.tres
        scenes/scene-3.tres
        workspace-layout.tres
```

测试只保留 `3-6` 个关键 C# 测试文件；第一版目标为上述 `5` 个。

## 3. MVP 验收口径

### 项目选择与初始化

- 用户可选择普通目录作为 AGAS 项目根。
- 无 `.agas` 时创建 `.agas/project.tres`、`.agas/scenes/`、`.agas/workspace-layout.tres`。
- 初始化生成三个示例 scene，并写入真实 `.tres`。
- 不显示最近项目。

### UI 骨架

- 左侧上部：`Files / Scenes` tabs。
- 左下：图片预览。
- 顶部：当前 scene 的 layer tabs，显示 current、visible、locked。
- 中间：无限网格画布。
- 底部：`Layer / Prompt / Properties`。
- splitter / collapse 状态保存到 `.agas/workspace-layout.tres` 并可恢复。

### Files / Scenes

- 当前 scene 保存在 `AgasProjectResource.ActiveSceneId`；当前 layer 保存在 `AgasSceneResource.ActiveLayerId`；layout 不保存当前 scene/layer。
- Files 只列项目目录内 `.png / .jpg / .jpeg / .webp`。
- 选择图片后左下预览使用 `Image.LoadFromFile`。
- Scenes 来自 `.agas/project.tres` 索引，再加载 `.agas/scenes/*.tres`。

### 图层、画布、图片

- `GridSettingsResource.SizePx` 默认 `48`，允许 `24` 或其他正整数。
- 网格可平移、缩放。
- 图片拖入当前 layer，可选中、移动、删除、复制。
- `GridSettingsResource.SnapEnabled=true` 时，移动按当前 `GridSettingsResource.SizePx` 吸附。
- hidden layer 不渲染、不命中。
- locked layer 可见但不可选中、移动、删除、复制。

### Undo / Redo

undo/redo 只覆盖图片增删改复制移动、Properties、Prompt、layer visible/locked；current layer 切换与 grid 设置不进 undo，但会持久化。

### Prompt 与 Properties

- 每层独立维护 `PromptFragments`。
- 合并预览只拼接 `Enabled=true` 且按 `Order` 排序的文本。
- Properties 选中图片时只读 `ImagePath,Order`；可编辑 `X,Y,ScaleX,ScaleY,RotationDegrees,Opacity`；无图片选中时显示空态提示，不绑定 layer 字段。

### 保存与恢复

- 保存使用临时 `.tres` 替换目标文件。
- 保存失败保留旧文件并显示错误。
- 关闭 Godot 进程后重开同一项目根，必须恢复 scene 列表、三个 scene、active scene、active layer、layer 状态、图片实例、PromptFragments、grid、view state、splitter/collapse。

## 4. 四阶段执行计划

### Phase 1：工程 + Resource

目标：工程可构建，Resource 与 `.agas` 初始化可真实保存/加载。

任务：

- 验证 Godot Mono `4.6.1` 与 `.NET 8`。
- 建立/修正 C# 工程引用，确保全 C#。
- 实现最小 Resource 模型，字段逐字采用本计划“最小 Resource 模型”。
- 实现 `.agas` 初始化。
- 实现 `AgasResourceRepository`：加载、保存、临时文件替换、失败保留旧文件并返回错误。
- 生成 `fixtures/demo-project` 的三个真实示例 scene。
- 开发前聚焦审计：只审 MVP 边界、技术合同、数据合同；P0/P1 清零后进入下一阶段，不固定轮次。

验收命令：

```bash
export GODOT=/Applications/Godot_mono.app/Contents/MacOS/Godot
"$GODOT" --version
dotnet --version
dotnet restore APP-AGAS-CS.sln
dotnet build APP-AGAS-CS.sln -c Debug
"$GODOT" --headless --path . --quit
dotnet test APP-AGAS-CS.sln -c Debug --filter "FullyQualifiedName~AgasResourceRoundTripTests|FullyQualifiedName~AgasProjectInitializationTests"
```

完成证据：构建通过；`.agas/project.tres`、三个 `.agas/scenes/*.tres`、`.agas/workspace-layout.tres` 可加载；Resource roundtrip 测试通过。

### Phase 2：UI 骨架

目标：主工作区布局完整，画布交互可先占位。

任务：

- 实现项目目录选择、初始化、打开后进入主工作区。
- 实现 `Files / Scenes`、左下预览、顶部 layer tabs、底部 `Layer / Prompt / Properties`。
- 实现 splitter / collapse 与 layout Resource 保存/恢复。
- 禁止加入最近项目、登录、AI、云端、多窗口。

验收命令：

```bash
export GODOT=/Applications/Godot_mono.app/Contents/MacOS/Godot
dotnet build APP-AGAS-CS.sln -c Debug
"$GODOT" --headless --path . -- --agas-smoke-open fixtures/demo-project
dotnet test APP-AGAS-CS.sln -c Debug --filter "FullyQualifiedName~WorkspaceRestoreTests"
```

完成证据：Files 能列图片；Scenes 能列三个 scene；左下预览成功；splitter/collapse 关闭重开恢复；截图 `artifacts/mvp/screenshots/phase2-workspace.png`。

### Phase 3：画布交互

目标：完成 MVP 图片编辑闭环。

任务：

- 实现无限网格、`GridSettingsResource.SizePx` 配置、平移、缩放。
- 实现图片拖入、选中、移动、删除、复制、吸附。
- 实现 visible / locked / current 规则。
- 实现 undo / redo。
- 实现 Properties：只读 `ImagePath,Order`；可编辑 `X,Y,ScaleX,ScaleY,RotationDegrees,Opacity`。
- 实现 PromptFragments 增删改排序启停与合并预览。

验收命令：

```bash
export GODOT=/Applications/Godot_mono.app/Contents/MacOS/Godot
dotnet build APP-AGAS-CS.sln -c Debug
"$GODOT" --headless --path . -- --agas-smoke-canvas fixtures/demo-project
dotnet test APP-AGAS-CS.sln -c Debug --filter "FullyQualifiedName~CanvasCommandUndoRedoTests|FullyQualifiedName~PromptFragmentTests"
```

完成证据：图片可拖入、选中、移动、删除、复制；hidden/locked 行为正确；undo/redo 覆盖关键编辑；Prompt 合并预览正确；截图 `artifacts/mvp/screenshots/phase3-canvas.png`。

### Phase 4：保存、测试、验收

目标：真实 Resource 保存，关闭重开恢复；MVP 验收通过。

任务：

- 完成保存按钮/自动保存触发点中的 Resource 保存，确保 Resource 字段与本计划合同一致。
- 验证保存失败保留旧文件并报错。
- 执行关闭进程、重开同一项目根恢复测试，验证 active scene、active layer、grid、layout、图片实例字段恢复。
- 执行最小审计：P0/P1 清零，不固定轮次。
- 收集最小证据：构建日志、关键测试日志、截图。
- 不加入导出门禁、复杂 manifest、大型 evidence 系统。

验收命令：

```bash
export GODOT=/Applications/Godot_mono.app/Contents/MacOS/Godot
"$GODOT" --version
dotnet --version
dotnet restore APP-AGAS-CS.sln
dotnet build APP-AGAS-CS.sln -c Debug
dotnet test APP-AGAS-CS.sln -c Debug
"$GODOT" --headless --path . -- --agas-smoke-save-reopen fixtures/demo-project
"$GODOT" --headless --path . --quit
```

最终证据：`dotnet build` 通过；`dotnet test` 通过；Godot headless smoke 通过；保存、退出、重开同一项目根后数据一致；至少两张截图；审计 P0/P1 为 0。

## 5. 无扩展检查清单

完成前确认：未实现 AI、登录支付、Steamworks、最近项目、自定义快捷键配置、复杂事务/13 故障、迁移、云端、多窗口、签名公证、Windows 实机、大型 evidence manifest wrapper、导出门禁；未把图片写入 `.agas`；未把图片转 base64 或嵌入 Resource；未扩大到超出本计划的目录树、生产文件和测试文件规模。
