# VS 扩展调试工具链（本仓库实战沉淀）

排查"装了扩展但 UI 里找不到/行为不对"时的全套手段，按代价从低到高排。

## 1. 静态验证（不启动 VS）

| 检查 | 方法 |
|---|---|
| 包注册 | 解开 vsix → `*.pkgdef`（UTF-16）：应有 Packages 注册、`AutoLoadPackages\{CodeWindow-guid}` 条目、`Menus` 条目（`"{包guid}"=", Menus.ctmenu, N"`） |
| cto 是否嵌入 dll | dll 二进制搜 `CFCT` magic（cto 以原生资源嵌入，不在 GetManifestResourceNames 里，别被误导） |
| 代码是否进 dll | `strings dll | grep 方法名`（.NET 元数据是 UTF-8） |
| 已装版本 vs 待装版本 | 已装扩展目录 `Extensions\<随机名>\extension.vsixmanifest` 的 Identity Version；同号/更低会被拒（exit 1001） |

## 2. 运行时探针（DTE via ROT，不抢前台焦点）

VS 实例在 ROT 注册 `!VisualStudio.DTE.17.0:<pid>`。枚举 ROT 拿 DTE 后可以：

- `dte.Commands.Item("{cmdset-guid}", cmdid)` → 命令是否在命令表；`.IsAvailable` 触发真实 QueryStatus（含自定义 BeforeQueryStatus 逻辑）——**能区分"命令没注册"和"注册了但菜单没合并"**
- `dte.ExecuteCommand("EditorContextMenus.CodeWindow.Helix.ToggleBlame")` — 直接执行命令（2022 里菜单贡献的命令带放置路径前缀），不抢焦点
- `dte.Documents.Open(path)` / `dte.ActiveDocument` — 远程激活文档
- `dte.ActiveDocument.Language` — 判文件实际编辑器（C/C++、Plain Text、JSON），比猜扩展名准
- **CommandBars facade**（`dte.CommandBars`，VS2022 仍可用）：枚举 `bars["JSON Context"]` 等的 Controls 看菜单**静态合并结构**（不跑 QueryStatus，拿工具窗口激活做对照可区分「没合并」和「被隐藏」）；bar 名不本地化；`cmd.AddControl(bar, pos)` 可动态注入菜单项——VSCT 合并不生效时的兜底手段
- **DTE COM 互操作两坑**（2026-10-15 实锤）：① 老 EnvDTE 引用与 VS2022 运行时签名不符（`get_CommandBars` 返回值类型变了）→ **JIT 期 MissingMethodException，方法体内 try/catch 接不住**，能炸穿包加载（SetSite failed）；② C# dynamic 调不了 COM 参数化默认属性（`bars["name"]`）→ RuntimeBinderException 静默失败。探针脚本没踩到是因为直接引了 VS2022 的 PublicAssemblies dll；扩展代码里安全做法 = 全 dynamic + 枚举代替索引器

探针模板在本机 `%TEMP%\vsprobe*.cs/.exe`（csc 编译需引用 `EnvDTE.dll` + `Microsoft.VisualStudio.Interop.dll`，且两个 dll 要拷到 exe 旁——17.x 里 DTE 类型转发到后者，少了会 JIT 期崩）。

## 3. 屏幕取证

- **PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT=2)**：可截取**后台/被遮挡**窗口的 WPF 内容，不需要 SetForegroundWindow（用户在前台干活时唯一安全手段）
- UIA（UIAutomationClient）：枚举菜单项/对话框文本可用，但 VS 的 WPF 编辑区暴露的元素树很不全（实测 Document/TabItem 经常拿不到），别强求
- 全局 SendKeys/SetCursorPos/mouse_event 会抢用户前台——用户在场时禁用

## 4. MSYS/Git Bash 参数转换坑（本轮反复踩）

| 写法 | 结果 |
|---|---|
| bash 里直接 `vsixinstaller /q x.vsix` | `/q` 被转成 `C:/Program Files/Git/q` → exit 209 |
| bash 里 `cmd.exe /c "..."` | `/c` 也被转换 → 进了交互式 cmd 什么都没跑 |
| **正确** | 命令全写进 .bat，`cmd.exe //c "C:\path\x.bat"`（双斜杠抗转换）；或 VSIXInstaller 参数用冒号形式 `/rootSuffix:Exp`（空格形式 exit 2001） |

## 5. devenv 参数陷阱

- **`devenv /log <文件路径>` 会把后面的文件参数当日志输出路径**——ActivityLog XML 会覆盖写进那个文件（本轮 BlameMargin.cs 被这样毁掉过）。要开日志：`devenv /log` 单独用；要开文件：另起一行或别加 /log
- `devenv /updateconfiguration`：强制重建 PkgDef/MEF 缓存（扩展改动不生效时先 Exp 沙箱验证再考虑它）
- ActivityLog.xml 是 UTF-16，先 `iconv -f UTF-16LE` 再 grep

## 6. Exp 沙箱

```
VSIXInstaller.exe /q /rootSuffix:Exp P4VS.vsix     # 装进实验实例（同版本要先 /u:<Id> 卸）
devenv.exe /RootSuffix Exp                        # 启动沙箱（参数放 .bat 里防 MSYS 转换）
```

 hive 在 `%LOCALAPPDATA%\Microsoft\VisualStudio\17.0_*Exp\`，整个删掉/改名 = 完全重置。沙箱验证通过而主实例不生效 → 主实例缓存问题（删 ComponentModelCache + updateconfiguration）。

## 7. 菜单合并的最终事实

**命令进命令表 ≠ 菜单里出现。** Button 直挂 `IDM_VS_CTXT_CODEWIN` 不合并；必须 Group 中转（参照 `Engine/Source/Programs/UnrealVS/UnrealVS.2022/UnrealVS.vsct` 的 `UBTSubMenuGroupContainer`）。改 vsct 后 ProvideMenuResource 版本号 +1 是好习惯但救不了结构性不合并。
