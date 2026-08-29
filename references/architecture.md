# 解决方案架构

## 项目与打包

```
P4VS.sln
├── HelixVS/              # 总 vsix 项目，AssemblyName=P4VS → bin\Release\P4VS.vsix
│   └── source.extension.vsixmanifest   # Identity: P4VsProviderx64.Perforce.6C2C3283-...
├── P4VS/                 # 主包 P4VS.dll：Scc provider、连接管理、所有工具窗口/对话框
├── P4VS_StartPage/       # 起始页包
├── CodeLensVisx/         # HelixCodeLensOopProviderVsix.dll：编辑器侧包（命令宿主）
└── CodeLensOopProvider/  # CodeLens 数据 provider（ServiceHub 独立进程）
```

HelixVS manifest 的 Assets 把这些打成一个扩展（安装后 VS 里只显示一个 "P4VS - P4 for Visual Studio"）：

```xml
<Asset Type="Microsoft.VisualStudio.VsPackage" d:ProjectName="P4VS_StartPage" .../>
<Asset Type="Microsoft.VisualStudio.VsPackage" d:ProjectName="CodeLensOopProviderVsix" .../>
<Asset Type="Microsoft.VisualStudio.CodeLensComponent" d:ProjectName="CodeLensOopProvider" .../>
<Asset Type="Microsoft.VisualStudio.VsPackage" d:ProjectName="%CurrentProject%" .../>
<Asset Type="Microsoft.VisualStudio.VsPackage" d:ProjectName="P4VS" .../>
<!-- 自研新增：让 MEF 扫描 CodeLensOopProviderVsix.dll（Blame margin provider 在里面） -->
<Asset Type="Microsoft.VisualStudio.MefComponent" d:ProjectName="CodeLensOopProviderVsix" Path="|CodeLensOopProviderVsix|" />
```

注意：同一个 dll 可以同时声明 VsPackage 和 MefComponent 两种 asset。

## 命令路由两条路

**1. CodeLens 详情面板命令**（NavigateToChangelist / ShowHistory / TimeLapseView）

- vsct 里带 `CommandWellOnly`（不出现在任何菜单，只能被 CodeLens 组件以编程方式触发）
- package 类显式实现 `IOleCommandTarget`：`QueryStatus`/`Exec` 里解析 `Marshal.GetObjectForNativeVariant(pvaIn)` 传参，未识别的命令链给 `pkgCommandTarget`（InitializeAsync 里 `GetServiceAsync(typeof(IOleCommandTarget))` 拿到）

**2. 普通菜单命令**（自研 Toggle P4 Blame 用这条）

- `InitializeAsync` 里 `OleMenuCommandService.AddCommand(new OleMenuCommand(exec, null, beforeQueryStatus, commandID))`
- `BeforeQueryStatus` 控制 `Visible/Enabled/Checked`（Checked = 菜单项勾选态）
- 编辑器右键菜单放置：vsct Button 的 `<Parent guid="guidSHLMainMenu" id="IDM_VS_CTXT_CODEWIN" />`

**包自动加载**：不加 `[ProvideAutoLoad]` 时包未加载、右键菜单项根本不出现。当前用 CodeWindow UI context 后台加载：

```csharp
[ProvideAutoLoad("8FE2DF1D-E0DA-4EBE-9D5C-415D40AD4874", PackageAutoLoadFlags.BackgroundLoad)]
```

## P4 连接共享（两种模式，别搞混）

| 场景 | 方式 |
|---|---|
| 同进程（VsPackage 内） | `P4VsProvider.CurrentScm`（static，`P4ScmProvider`）→ `.Connection.Repository` 直接跑 p4api.net；`CheckConnection()` 判活。**新功能一律用这个** |
| 跨进程（CodeLens ServiceHub 组件） | `CodeLensOopProvider/HelixUtility.GetRepository()`：主插件把 `port,user,client` 写进 memory-mapped file `connection-info-{devenvPid}`，组件读后自建 `Repository` |

用 CurrentScm 复用主插件已建立的连接，天然避开 P4CHARSET / unicode 服务器配置问题。

## 编辑器扩展点（MEF）

- margin provider 标准五连：`[Export(typeof(IWpfTextViewMarginProvider))]` `[Name(...)]` `[Order(After = PredefinedMarginNames.LineNumber)]` `[MarginContainer(PredefinedMarginNames.Left)]` `[ContentType("text")]` `[TextViewRole(PredefinedTextViewRoles.Document)]`
- package 拿当前编辑器 view：

```csharp
IVsTextManager tm = GetService(typeof(SVsTextManager)) as IVsTextManager;
tm.GetActiveView(1, null, out IVsTextView vsView);
var adapters = ((IComponentModel)GetService(typeof(SComponentModel))).GetService<IVsEditorAdaptersFactoryService>();
IWpfTextView view = adapters.GetWpfTextView(vsView);
```

- 当前文件路径：`view.TextBuffer.Properties.TryGetProperty(typeof(ITextDocument), out doc)` → `doc.FilePath`

## 引用版本坑

- `Microsoft.VisualStudio.Editor` NuGet 包**没有** `17.0.31902.203` 这类版本号，用 `17.0.487`（与 Text.Data/Logic/UI 对齐）；TextManager.Interop 才有 `17.0.31902.203`
- `IVsEditorAdaptersFactoryService` 在 `Microsoft.VisualStudio.Editor.dll`（命名空间 `Microsoft.VisualStudio.Editor`），不在 ComponentModelHost 里
