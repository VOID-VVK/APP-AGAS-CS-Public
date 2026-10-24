# AGAS 主工作区 MVP 技术设计

> 范围：第一个最小 Demo。基础功能完整，未提功能不扩展。  
> 技术固定：Godot 4.6.1 Mono / .NET 8 / 全 C#。  
> 产品数据：用户项目根目录隐藏 `.agas/` 下的 Godot custom `.tres` Resource。  
> 图片资产：放在用户项目普通目录中，不复制进 `.agas/`，运行时用 `Image.LoadFromFile` 读取。  
> 本文只描述 MVP，不包含生产级扩展设计。

---

## 1. 已验证事实与待验证项

### 已验证事实

- 已验证 Godot `ResourceSaver` / `ResourceLoader` 可用绝对路径保存/加载外部 custom `.tres`，且嵌套 `Godot.Collections.Array<Resource>` 可 roundtrip；外部图片读取以 `Image.LoadFromFile` 为准。

### 待验证项

- 导出包中外部 `.agas/*.tres` custom Resource 的加载稳定性仍待验证；MVP 不以导出发布作为完成前置条件。

---

## 2. MVP 目标

1. 启动后选择一个本地项目目录。
2. 新建/初始化项目目录：创建 `.agas/`、写入项目 Resource、创建 3 个示例 scene。
3. 打开已初始化项目目录并恢复保存内容。
4. 左侧提供 `Files` / `Scenes` 两个页签。
5. `Files` 显示项目目录内图片文件。
6. 左下显示当前选中图片预览。
7. `Scenes` 显示 3 个示例 scene，并可切换当前 scene。
8. 中央为无限网格画布。
9. 网格默认 `48px`，可配置为 `24px` 或其他正整数。
10. 画布支持平移、缩放。
11. 顶部显示当前 scene 的图层 tabs。
12. 图层支持 `visible`、`locked`、`current` 状态。
13. 只有当前、可见、未锁定图层可编辑。
14. 图片可从 `Files` 拖入画布成为图片实例。
15. 图片实例支持选中、移动、删除、复制、吸附网格。
16. 支持 undo / redo。
17. 每层支持 `PromptFragments` 增、删、改、排序、启用/禁用。
18. 支持当前层 prompt 合并预览。
19. `Properties` 仅显示/编辑当前选中图片实例最小属性；无图片选中显示空态，不绑定 layer 字段。
20. splitter 支持拖动调整尺寸与 collapse 折叠。
21. 所有项目数据真实保存为 `.agas` 下 `.tres`。
22. 关闭重开后恢复 scene、layer、image instance、prompt fragments、grid、视图和布局。

---

## 3. 明确非目标

第一版不做：

1. AI、模型调用、图片生成。
2. 登录、支付、订阅、账号。
3. Steamworks、创意工坊、成就、云存档。
4. 最近项目、自定义快捷键配置。
5. 复杂多文件事务、13 类故障注入、事务 manifest wrapper。
6. 数据迁移系统、云端同步、协作、多窗口。
7. 签名、公证、发布流水线、Windows 实机验收。
8. 大型 evidence 系统。
9. 图片 base64 持久化、网络图片、URL 图片。
10. GDScript、JS/TS、Node、Electron、WebView。

---

## 4. 工作区 ASCII 布局

```text
┌─────────────────────────────────────────────────────────────────────────────┐
│ AGAS - <project-name>                                                保存状态 │
├───────────────┬───────────────────────────────────────────────┬─────────────┤
│ Files | Scenes│ Layer Tabs: [BG 👁 🔓] [Props 👁 🔒] [FX 👁 🔓*]│             │
│               ├───────────────────────────────────────────────┤             │
│ Files tree    │                                               │             │
│ image.png     │              Infinite Grid Canvas             │ Properties  │
│ folder/a.webp │              平移 / 缩放 / 吸附               │             │
│               │              image instances                  │             │
│               │                                               │             │
│───────────────│                                               │             │
│ Preview       │                                               │             │
│ selected img  │                                               │             │
├───────────────┴───────────────────────────────────────────────┴─────────────┤
│ Bottom Dock: [Layer] [Prompt] [Properties]                                   │
│ PromptFragments list + enabled toggle + order + merged preview               │
└─────────────────────────────────────────────────────────────────────────────┘
```

布局要求：

1. 左栏、左下预览、右侧 `Properties`、底部 dock 均支持 splitter/collapse。
2. collapse 与尺寸保存到 `.agas/workspace-layout.tres`。

---

## 5. `.agas` 目录结构

```text
<project-root>/
├── .agas/
│   ├── project.tres
│   ├── workspace-layout.tres
│   └── scenes/
│       ├── scene-001.tres
│       ├── scene-002.tres
│       └── scene-003.tres
├── images/
│   └── *.png / *.jpg / *.jpeg / *.webp
└── any-user-folder/
    └── *.png / *.jpg / *.jpeg / *.webp
```

规则：

1. `.agas/` 只保存 AGAS 产品数据。
2. 图片留在项目普通目录，Resource 只保存项目相对 `ImagePath`。
3. 扫描图片时跳过 `.agas/`、`.godot/`、`bin/`、`obj/`、`.git/`。
4. 首版只支持 `.png`、`.jpg`、`.jpeg`、`.webp`。
5. 路径统一保存为 `/` 分隔的项目相对路径。
6. 禁止保存绝对图片路径、URL、`data:image`、base64。

---

## 6. Resource 类最小字段

说明：以下为 MVP 最小持久化字段。所有类为 C# `[GlobalClass] partial class ... : Resource`，字段使用 `[Export]`。
SceneIndexResource ResourceVersion,Id,Name,RelativePath(.agas/scenes/<scene-id>.tres),Order
AgasLayerResource ResourceVersion,Id,Name,Order,Visible,Locked,Images,PromptFragments
PromptFragmentResource ResourceVersion,Id,Text,Enabled,Order
SceneViewStateResource ResourceVersion,CameraX,CameraY,Zoom

### 6.1 `AgasProjectResource`
文件：`.agas/project.tres`

```csharp
public partial class AgasProjectResource : Resource
{
    [Export] public int ResourceVersion { get; set; } = 1;
    [Export] public string ProjectId { get; set; } = "";
    [Export] public string Name { get; set; } = "";
    [Export] public string ActiveSceneId { get; set; } = "";
    [Export] public GridSettingsResource Grid { get; set; } = new();
    [Export] public Array<SceneIndexResource> Scenes { get; set; } = new();
}
```

### 6.2 `SceneIndexResource`
嵌入：`AgasProjectResource.Scenes`

```csharp
public partial class SceneIndexResource : Resource
{
    [Export] public int ResourceVersion { get; set; } = 1;
    [Export] public string Id { get; set; } = "";
    [Export] public string Name { get; set; } = "";
    [Export] public string RelativePath { get; set; } = "";
    [Export] public int Order { get; set; } = 0;
}
```

### 6.3 `GridSettingsResource`

嵌入：`AgasProjectResource.Grid`

```csharp
public partial class GridSettingsResource : Resource
{
    [Export] public int ResourceVersion { get; set; } = 1;
    [Export] public int SizePx { get; set; } = 48;
    [Export] public bool SnapEnabled { get; set; } = true;
    [Export] public bool Visible { get; set; } = true;
}
```

### 6.4 `AgasSceneResource`

文件：`.agas/scenes/<scene-id>.tres`

```csharp
public partial class AgasSceneResource : Resource
{
    [Export] public int ResourceVersion { get; set; } = 1;
    [Export] public string Id { get; set; } = "";
    [Export] public string Name { get; set; } = "";
    [Export] public string ActiveLayerId { get; set; } = "";
    [Export] public SceneViewStateResource ViewState { get; set; } = new();
    [Export] public Array<AgasLayerResource> Layers { get; set; } = new();
}
```

### 6.5 `SceneViewStateResource`

嵌入：`AgasSceneResource.ViewState`

```csharp
public partial class SceneViewStateResource : Resource
{
    [Export] public int ResourceVersion { get; set; } = 1;
    [Export] public double CameraX { get; set; } = 0.0;
    [Export] public double CameraY { get; set; } = 0.0;
    [Export] public double Zoom { get; set; } = 1.0;
}
```

### 6.6 `AgasLayerResource`

嵌入：`AgasSceneResource.Layers`

```csharp
public partial class AgasLayerResource : Resource
{
    [Export] public int ResourceVersion { get; set; } = 1;
    [Export] public string Id { get; set; } = "";
    [Export] public string Name { get; set; } = "";
    [Export] public int Order { get; set; } = 0;
    [Export] public bool Visible { get; set; } = true;
    [Export] public bool Locked { get; set; } = false;
    [Export] public Array<ImageInstanceResource> Images { get; set; } = new();
    [Export] public Array<PromptFragmentResource> PromptFragments { get; set; } = new();
}
```

### 6.7 `ImageInstanceResource`

嵌入：`AgasLayerResource.Images`

```csharp
public partial class ImageInstanceResource : Resource
{
    [Export] public int ResourceVersion { get; set; } = 1;
    [Export] public string Id { get; set; } = "";
    [Export] public string ImagePath { get; set; } = "";
    [Export] public int Order { get; set; } = 0;
    [Export] public double X { get; set; } = 0.0;
    [Export] public double Y { get; set; } = 0.0;
    [Export] public double ScaleX { get; set; } = 1.0;
    [Export] public double ScaleY { get; set; } = 1.0;
    [Export] public double RotationDegrees { get; set; } = 0.0;
    [Export] public double Opacity { get; set; } = 1.0;
}
```

### 6.8 `PromptFragmentResource`

嵌入：`AgasLayerResource.PromptFragments`

```csharp
public partial class PromptFragmentResource : Resource
{
    [Export] public int ResourceVersion { get; set; } = 1;
    [Export] public string Id { get; set; } = "";
    [Export] public string Text { get; set; } = "";
    [Export] public bool Enabled { get; set; } = true;
    [Export] public int Order { get; set; } = 0;
}
```

### 6.9 `WorkspaceLayoutResource`

文件：`.agas/workspace-layout.tres`

```csharp
public partial class WorkspaceLayoutResource : Resource
{
    [Export] public int ResourceVersion { get; set; } = 1;
    [Export] public double LeftPanelWidth { get; set; } = 280.0;
    [Export] public bool LeftPanelCollapsed { get; set; } = false;
    [Export] public double PreviewHeight { get; set; } = 180.0;
    [Export] public bool PreviewCollapsed { get; set; } = false;
    [Export] public double RightPanelWidth { get; set; } = 280.0;
    [Export] public bool RightPanelCollapsed { get; set; } = false;
    [Export] public double BottomDockHeight { get; set; } = 240.0;
    [Export] public bool BottomDockCollapsed { get; set; } = false;
    [Export] public string ActiveLeftTab { get; set; } = "Files";
    [Export] public string ActiveBottomTab { get; set; } = "Prompt";
}
```

---

## 7. 初始化流程

1. 用户选择一个本地目录作为 `<project-root>`。
2. 若目录不存在则报错，不在 MVP 内创建父级路径。
3. 若 `.agas/project.tres` 不存在，执行初始化。
4. 创建 `.agas/` 与 `.agas/scenes/`。
5. 写入 `.agas/project.tres`。
6. 写入 `.agas/workspace-layout.tres`。
7. 写入 3 个示例 scene：`scene-001`、`scene-002`、`scene-003`。
8. 3 个示例 scene 每个至少包含 1 个默认 layer。
9. `project.tres` 的 `Scenes` 索引指向 3 个 scene 文件。
10. `ActiveSceneId` 指向第一个 scene。
11. 打开已初始化项目时加载 `project.tres`、当前 scene、workspace layout。
12. 打开失败时显示中文错误，不覆盖旧文件。

---

## 8. 核心交互

### 8.1 Files / Preview

1. `Files` 扫描项目目录图片。
2. 选中图片后，左下预览区用 `Image.LoadFromFile` 加载。
3. 图片损坏或不存在时显示占位和错误文案。
4. 图片列表不返回、不保存、不缓存 base64。

### 8.2 Scenes

1. `Scenes` 显示 `project.tres` 中的 scene 索引。
2. 点击 scene 切换当前 scene。
3. 切换前保存当前 scene 的内存状态到待保存模型。
4. MVP 必须带 3 个示例 scene。

### 8.3 Layer tabs

1. 顶部 tabs 显示当前 scene 的 layers。
2. tab 展示名称、visible、locked、current 状态。
3. 点击 tab 设置当前 layer。
4. visible=false：不渲染、不命中、不编辑。
5. locked=true：渲染，但不可命中、不可编辑。
6. current=true 且 visible=true 且 locked=false：允许图片拖入和编辑。

### 8.4 画布

1. 画布坐标为世界坐标，不受窗口大小限制。
2. 拖动空白区域平移视图。
3. 滚轮或按钮缩放视图。
4. 网格随视图平移缩放绘制。
5. 网格大小来自 `GridSettingsResource.SizePx`。
6. `SnapEnabled=true` 时移动落点吸附到网格。

### 8.5 图片实例

1. 从 `Files` 拖入画布时，在当前可编辑 layer 新增 `ImageInstanceResource`。
2. 新实例 `ImagePath` 为项目相对路径。
3. 点击图片实例选中。
4. 拖动选中实例移动。
5. Delete 删除选中实例。
6. Copy/Paste 复制选中实例到当前 layer。
7. 移动、删除、复制都进入 undo/redo。

### 8.6 PromptFragments

1. Prompt dock 显示当前 layer 的 fragments。
2. 支持新增、删除、编辑 `Text`。
3. 支持调整 `Order`。
4. 支持切换 `Enabled`。
5. 合并预览只取 `Enabled=true`，按 `Order` 升序，用换行连接非空文本。
6. Prompt 只做本地文本，不调用 AI。

### 8.7 Properties

1. Properties 选中图片时只读 `ImagePath,Order`；可编辑 `X,Y,ScaleX,ScaleY,RotationDegrees,Opacity`；无图片选中时显示空态提示，不绑定 layer 字段。
2. 未选中图片时不绑定 layer 字段。
3. 修改 Properties 进入 undo/redo。

### 8.8 Undo / Redo

1. undo/redo 只保存在进程内。
2. undo/redo 只覆盖图片增删改复制移动、Properties、Prompt、layer visible/locked；current layer 切换与 grid 设置不进 undo，但会持久化。
3. 关闭重开不恢复 undo/redo 历史。

---

## 9. 最小保存方案

1. 保存目标只包含：`.agas/project.tres`、`.agas/workspace-layout.tres`、`.agas/scenes/*.tres`。
2. 保存单个 Resource 时，先写入同目录临时文件：`<target>.tmp.tres`。
3. `ResourceSaver.Save(resource, tempPath)` 成功后，用文件替换将 temp 替换为 target。
4. 替换成功后删除残留 temp。
5. 保存失败时保留旧 target 文件，删除可删除的 temp，并显示中文错误。
6. 不实现多文件事务、commit marker、backup 目录、故障注入矩阵。
7. 关闭项目前触发保存；用户主动保存也触发保存。
8. 打开项目只加载 `.agas` 下 Resource，不从图片目录推导产品数据。

---

## 10. 最小测试

### 10.1 构建

1. Godot 4.6.1 Mono 项目可打开。
2. .NET 8 C# 编译通过。
3. 无 GDScript 业务代码。

### 10.2 Resource 保存恢复

1. 初始化项目后存在 `.agas/project.tres`、`.agas/workspace-layout.tres`、3 个 scene `.tres`。
2. `ResourceLoader` 可加载上述 `.tres`。
3. 修改 scene、layer、图片实例、prompt 后保存。
4. 关闭重开后修改内容恢复。
5. 保存失败模拟下旧文件仍存在且可加载。

### 10.3 图片与路径

1. Files 能发现项目内 `.png`、`.jpg`、`.jpeg`、`.webp`。
2. `.agas/`、`.godot/`、`bin/`、`obj/`、`.git/` 被跳过。
3. 预览使用 `Image.LoadFromFile`。
4. `ImagePath` 保存为项目相对路径。
5. `.tres` 中不包含绝对图片路径、URL、`data:image`、base64。

### 10.4 画布与编辑

1. 无限网格可平移缩放。
2. 默认网格 48px。
3. 网格可改为 24px。
4. 图片可拖入、选中、移动、删除、复制。
5. 吸附开关生效。
6. visible=false 图层不可见不可编辑。
7. locked=true 图层可见不可编辑。
8. undo/redo 对核心编辑生效。

### 10.5 Prompt 与布局

1. PromptFragments 可增删改排序启停。
2. 合并预览按 enabled + order + 换行生成。
3. Properties 修改可保存恢复。
4. splitter/collapse 状态可保存恢复。

### 10.6 截图证据

保留 3 张关键截图：项目选择/初始化、主工作区、关闭重开恢复后状态。

---

## 11. 验收清单

1. 使用 Godot 4.6.1 Mono / .NET 8 / C#。
2. 用户能选择项目目录，新项目会创建隐藏 `.agas/`。
3. `.agas/project.tres` 存在且为 custom Resource。
4. `.agas/workspace-layout.tres` 存在且可恢复布局。
5. `.agas/scenes/` 下有 3 个示例 scene `.tres`。
6. Files/Scenes 左侧页签可用，Files 显示项目普通目录内图片。
7. 左下预览能显示选中图片，图片读取使用 `Image.LoadFromFile`。
8. 顶部 layer tabs 显示 visible/locked/current。
9. current + visible + unlocked layer 可编辑；hidden 不渲染不编辑；locked 渲染但不编辑。
10. 无限网格可平移缩放，默认 48px，可设 24px。
11. 图片可拖入画布，图片实例可选中、移动、删除、复制。
12. 吸附网格生效。
13. undo/redo 生效，重启后历史为空。
14. 每层 PromptFragments 可增删改排序启停，Prompt 合并预览正确。
15. Properties 可编辑最小字段。
16. splitter/collapse 可用且可恢复。
17. 保存使用临时 `.tres` 后替换目标文件。
18. 保存失败保留旧文件并报错。
19. 关闭重开后 scene、layer、image instance、prompt、layout 恢复。
20. 产品 `.tres` 不包含图片 base64、URL、绝对图片路径。
21. 第一版无 AI、登录支付、Steamworks、最近项目、自定义快捷键配置。
22. 第一版无复杂事务、迁移、云端、多窗口、签名公证、Windows 实机验收、大型 evidence manifest wrapper。
23. 验收证据只需构建结果、关键测试结果、关键截图。
