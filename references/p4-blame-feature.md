# P4 Blame 功能（自研）

官方 P4VS **没有**编辑器内 annotate/blame——它的 `icmdTimeLapse` 只是拉起外部 P4V 的 Time-lapse View，CodeLens 只显示最新一次变更。本功能是 2026-08 自研补齐的 git-blame 风格编辑器左边距。

## 文件清单

| 文件 | 职责 |
|---|---|
| `CodeLensVisx/Blame/P4BlameService.cs` | 数据层：`BlameLineInfo`（CL/用户/日期/描述）+ `P4BlameService.GetBlame(filePath)` |
| `CodeLensVisx/Blame/BlameMargin.cs` | 纯代码 WPF margin（`FrameworkElement.OnRender` + `FormattedText`，无 XAML，规避旧式 csproj 的 XAML 编译问题） |
| `CodeLensVisx/Blame/BlameMarginProvider.cs` | MEF `IWpfTextViewMarginProvider`；margin 实例塞进 `view.Properties[typeof(BlameMargin)]` 供命令查找 |
| `CodeLensVisx/CodeLensOopProviderPackage.cs` | 命令注册与处理：`OnToggleBlameExecute` / `OnToggleBlameBeforeQueryStatus` / `GetActiveWpfTextView` |
| `CodeLensVisx/CodeLensOopProviderPackage.vsct` | `cmdidToggleBlame = 0x1015`，经 `P4BlameGroup`(0x1021) 中转挂 `IDM_VS_CTXT_CODEWIN`（直挂不合并！），同时挂 `MyMenuGroup` 进 Tools 菜单。**JSON 编辑器菜单不在此挂**（合并无效，见下节） |
| `CodeLensVisx/CodeLensOopProviderPackage.cs` | 命令注册与处理 + **运行时注入 JSON Context 菜单**（`EnsureJsonContextMenuBlameButton`，见下节）；DTE/CommandBars 类型在 EnvDTE 与 Interop 17.x 重名（CS0433），必须走 `dynamic` IDispatch |
| `HelixVS/source.extension.vsixmanifest` | 给 CodeLensOopProviderVsix 补的 `MefComponent` asset（不加 VS 不扫描 margin provider） |

## 数据流

```
右键 Toggle P4 Blame（OleMenuCommand，UI 线程）
  → view.Properties 找 BlameMargin → Toggle()
  → Task.Run 拉数据（UI SyncContext 自动回 UI 线程应用）
  → OnRender 逐行画  CL号 用户 MM-dd
```

每个文件 2 次 P4 往返：

1. `rep.GetFileAnnotations(specs, opts)`（`opts["-c"]=null`）→ 每行的最后修改 CL
2. `rep.GetChangelists(clOpts, fileSpec)`（`-m 2000`）→ CL → OwnerName/ModifiedDate/Description 映射

数据复用主插件活动连接：`P4VsProvider.CurrentScm.Connection.Repository`（前置：用户在 P4VS 里已 Connect）。

## 编辑器右键菜单覆盖：CODEWIN 走 VSCT，JSON 走运行时注入（2026-10-15 定论）

**症状**：右击 Toggle P4 Blame 只在 .cpp/.h 出现，.uproject/.json 没有。
**根因**：不是扩展名过滤（QueryStatus 只看 active view + 文件路径）。WebTools 的 `JsonEditorFactory` 注册了 `"*"` 通配 fallback（`Microsoft.WebTools.Languages.Json.VS.pkgdef`），**.uproject/.uplugin/.json 全被 JSON 编辑器接管**；JSON 编辑器右击弹自己的菜单（截图特征：有「对属性进行排序」「重新加载架构」），不是 `IDM_VS_CTXT_CODEWIN`。文件归属用 DTE 探针 `dte.ActiveDocument.Language` 实测：.h/.cpp=C/C++、.ini/.as=Plain Text（核心编辑器→CODEWIN→有）、.uproject/.uplugin/.json=JSON（无）。

**JSON 菜单标识（留档备用）**：guid=`{F718CA06-CF4F-4A0C-9106-E79E9EE5E7CD}`，MenuId=1。提取方法：ildasm `Common7/IDE/Extensions/Microsoft/Web Tools/Languages/Microsoft.WebTools.Languages.Json.VS.dll` → `JsonShowContextMenuCommandHandler.get_MenuId()`（ldc.i4.1）与 `get_CmdSetGuid()` → 指向 Json.Editor.dll 的 `JsonEditorCommands.JsonPackageCmdSetGuid`（.cctor 里 ldstr 字面量）。菜单由 `IMenuCommandService.ShowContextMenu(CommandID(guid,1))` 弹出。

**VSCT 挂组到该菜单 = 无效（已废弃的方案）**：vsct 加 Group→`{F718CA06}:1`，编译过、装了、`ProvideMenuResource` 版本也升了、`devenv /updateconfiguration` 也跑了——菜单里就是没有。判伪手段：CommandBars facade 是**静态合并结构**（不跑 QueryStatus，工具窗口激活时 Code Window 里我们的项仍在），JSON Context 里没有 = 合并从未发生。Copilot/Git 项能出现在该菜单（机制未解析，VS2022 的 cto 全是压缩格式，无法静态读 placement；Copilot.Vsix 里无 ProfferCommands/CommandBars 调用），推断它们也是动态注入。

**最终方案（已实现）**：包加载时 `EnsureJsonContextMenuBlameButton()` 用 `Command.AddControl` 把 cmdidToggleBlame 动态挂到 **"JSON Context" 命令条**（bar 名跨语言不变，中文 VS 也是这个名字），放在**菜单最底部**（已存在但位置不对会先 Delete 再加）；同时在 `OnToggleBlameBeforeQueryStatus` 里懒重试（JSON 包未起时 bar 不存在）。可见性/勾选仍走原 OleMenuCommand QueryStatus，注入控件自动继承。

**CommandBars facade 事实**：① 控件枚举反映静态合并结构 + 动态注入项；② `Command.AddControl(bar, pos)` 可用且立即生效（pos=`Controls.Count+1` 放底部）；③ bar 索引器按内部名查（"JSON Context"/"Code Window"，不本地化）；④ 注入项的 Visible/Enabled 动态跟随命令 QueryStatus。

## p4api.net annotate 实测坑（2018.4/2025.1 dll）

1. **`FileAnnotation` 只有 `File`(FileSpec) + `Line`(string)**——tagged 输出字段是 `depotFile/lower/upper/data`，**没有 user/date**。所以必须再跑 GetChangelists 建元数据映射，别指望 annotate 直接给
2. **`ChangelistIdVersion` 属性名是官方 typo：`ChanglistId`**（少一个 e），照抄才能编译
3. p4api.net 内部判断是否传了 `-c` 的代码是 `options.Keys.ToString().Contains("c")`，不可靠——`ExtractChangeNumber` 要同时兼容 `ChangelistIdVersion` 和 `Revision` 两种包装（反正传了 `-c`，`Revision.Rev` 里的数字也是 CL 号）
4. 行号对齐：只要不传 `-a`（AllResults），输出第 N 条 = 文件第 N 行（含空行），直接按下标对齐；传了 `-a` 会插入历史已删行导致错位（仓库 memory 同款踩坑）
5. **`p4 changes` 默认把描述截断到前 31 个字符**（2026-08-31 实测确诊）——`GetChangelists` 必须传 `-l` 拿完整描述，否则 auto-merge 提交人提取（`#auto-merge... by <user>`，名字在 31 字符之后）永远匹配不到，静默回退显示 CI 机器人账号。这也是 "auto-merge 提交人提取装了却不生效" 的主因（另一主因是同版本号覆盖安装加载了旧 DLL，见 build-and-install.md 升级机制）

## 渲染与行为

- 逐行绘制：`y = (line.TextTop - view.ViewportTop) * (view.ZoomLevel / 100)`；订阅 `LayoutChanged` → `InvalidateVisual()`（滚动/折叠/缩放都走这个事件）
- **ZoomLevel 是最大坑（2026-08-29 确诊）**：`ITextViewLine.Top/TextTop/Height` 全是**逻辑单位（未乘缩放）**，但编辑器文本层按 ZoomLevel 缩放渲染而 **margin 不被缩放**——不做换算时 margin 行间距 16px vs 代码行 20.5px（128% 缩放），行整体漂移、底部无 blame、滚动后全乱。tooltip 命中反向换算：`y = mouse.Y / zoom + ViewportTop`
- **诊断手段**：`P4VS_BLAME_DEBUG=1` 环境变量开启后 OnRender 把每条 view line 的 n/top/texttop/height/ViewportTop 转储到 `C:\Windows\Temp\blame_geo.txt`；`P4VS_BLAME_FAKE=1` 用当前 buffer 生成假数据（CL=10000+行号）免 P4 连接调几何。后台截屏用 PrintWindow(PW_RENDERFULLCONTENT=2)，DTE 走 ROT（`!VisualStudio.DTE.17.0:<pid>`）ExecuteCommand 不抢前台
- **自动换行（word wrap）坑**：一条 buffer line 折成多个 view line 时，每个续行段也是独立 `ITextViewLine` 且 `GetLineNumberFromPosition` 返回同一 buffer 行号——不做过滤会在每个续行段重复绘制同一 blame（表现：blame 行数多于编辑器行数、内容错位）。必须 `if (!line.IsFirstTextViewLineForSnapshotLine) continue;`（2026-08-29 用户反馈「行数信息没对上」确诊）。tooltip 命中测试不用跳过（续行段映射到同一 buffer 行是对的）
- **CodeLens 行不干扰锚点**：CodeLens 通过 line transform 占用该行 `Top..TextTop` 区间，绘制锚点用 `TextTop`（文本顶部）自然落在代码文本旁，无需特殊处理
- 颜色：按 CL 日期在 `[最新 #569CD6, 最旧 #7A7A7A]` 区间线性渐变，按 CL 缓存 brush
- 宽度：数据到达后按最长行 FormattedText 测量 + 10px padding；关闭时 `Width=0 + Collapsed`
- tooltip：`OnMouseMove` 用 `y + ViewportTop` 命中 `ITextViewLine` → 显示 `CL - 用户 - 完整时间 + 描述`
- **stale**：`TextBuffer.Changed` 后整栏变灰（行号可能已偏移），tooltip 追加提示；再 Toggle 开关一次 = 重新拉取
- margin 生命周期：每个 text view 创建时都挂一个（初始折叠零成本），`view.Closed` → Dispose

## 已知限制 / 迭代方向

- 编辑后不做 tracking-span 行映射，只标 stale（够用；要做的话用 `ITrackingSpan` 跟踪每行起始位置）
- 本地未提交的新增行没有 blame，显示 `-`
- 可扩展：点击 margin 行 → `SubmittedChangelistDlg` 看 CL 详情（参考 `CodeLensOopProviderPackage.NavigateToCommit`）；hover 高亮同 CL 的所有行
