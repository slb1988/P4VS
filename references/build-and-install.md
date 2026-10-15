# Build & Install

## 一次性前置

1. **VS 2022**（本机验证：Community 17.14）。**不需要**装「Visual Studio extension development」workload——NuGet 包 `Microsoft.VSSDK.BuildTools 17.0.5232`（packages.config 已声明）自带全部 targets。
2. **nuget.exe** 在 `D:\Github\_tools\nuget.exe`（https://dist.nuget.org/win-x86-commandline/latest/nuget.exe）。packages.config 项目只能用 `nuget.exe restore`，`dotnet restore` 不认。
3. **三个 dll 拷到 `P4VS\libs\Release\`**：
   - `p4api.net.dll`、`p4bridge.dll`（csproj HintPath 指向这里）
   - `Microsoft.Deployment.WindowsInstaller.dll`（官方构建机靠 GAC，干净机器没有；本仓库已在 `P4VS.csproj` 补 HintPath）
   - 来源：已装扩展目录 `C:\Users\admin\AppData\Local\Microsoft\VisualStudio\17.0_*\Extensions\<随机名>\`，或 p4api.net 官方 release

## 构建

```bash
cd /d/Github/P4VS
/d/Github/_tools/nuget.exe restore P4VS.sln
"/c/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/MSBuild.exe" P4VS.sln //p:Configuration=Release
```

- 输出总安装包：`bin\Release\P4VS.vsix`（HelixVS 项目产出，AssemblyName=P4VS）
- 只改了编辑器侧代码可只编 `HelixVS/HelixVS.csproj`，增量很快
- Git Bash 里给 Windows 程序传 `/flag` 必须双斜杠（`//p:...`），否则 MSYS 把它转成路径

## 安装

脚本已入库（仓库根目录，勿再用 %TEMP%——会被系统清理）：

| 脚本 | 用途 |
|---|---|
| `install_p4vs.bat` | 装主实例：`VSIXInstaller /q /logFile:p4vs_install.log D:\Github\P4VS\bin\Release\P4VS.vsix`，输出 `VSIX_EXIT=0` 即成功 |
| `install_p4vs_exp.bat` | 装 Exp 沙箱（`/rootSuffix:Exp`，不污染主实例，无需关主 VS） |
| `uninstall_p4vs.bat` | 按 Identity Id 卸载，默认总包 Id，可传参卸残留组件（如独立 CodeLens Id） |

调用必须走 `cmd.exe //c "D:\Github\P4VS\install_p4vs.bat"`——直接在 bash 里调 VSIXInstaller 会被 MSYS 参数转换坑（见 exit 209）。

**安装前必须关闭所有 devenv.exe**：先 `taskkill //PID <pid>`（温和，让它弹保存提示），6-10 秒不退再 `taskkill //F //PID <pid>`。

## VSIXInstaller exit code 排障

| Exit | 含义 | 处理 |
|---|---|---|
| 0 | 成功 | 日志在 `%TEMP%\p4vs_install.log`（/logFile 指定）或 `dd_VSIXInstaller_*.log` |
| 209 | 参数无效 | 几乎都是 Git Bash 把 `/q` 转成了路径。走上面的 bat |
| 2004 | 阻塞进程未关（devenv / cl.exe / link.exe） | 日志尾部列出阻塞 PID，关掉重试。编译残留的 cl/link 也会挡 |
| 1001 | 异常，常见 `AlreadyInstalledException: 此扩展已安装到所有适用的产品` | **目标 Version ≤ 已装版本**。抬 manifest 版本号重装 |

## 改动不生效排障（菜单/命令装了却没出现）

症状：vsix 安装成功但 VS 里右键菜单/命令找不到。按顺序排查：

1. **先在 Exp 沙箱验证功能本身**（不污染主实例，无需关主 VS）：
   ```bat
   VSIXInstaller.exe /q /rootSuffix:Exp "D:\Github\P4VS\bin\Release\P4VS.vsix"
   devenv.exe /RootSuffix Exp "某个.cs 文件"
   ```
   注意 `/rootSuffix:Exp` 是冒号不是空格（写成空格 exit 2001 HandleInvalidArg）。Exp 实例里命令表可用 DTE 探针验证：`dte.Commands.Item("{f3cb9f10-281b-444f-a14e-de5de36177cd}", 4117)` 返回非空且 `IsAvailable=True` 即正常。
2. **Exp 正常而主实例没有** = 主实例缓存陈旧：关主 VS → 删 `%LOCALAPPDATA%\Microsoft\VisualStudio\17.0_*\ComponentModelCache\` → 跑 `devenv.exe /updateconfiguration` → 重开主 VS。
3. **改了 vsct 加了按钮、命令表里有（DTE `IsAvailable=True`）但菜单里就是不显示** = **Button 直挂 `IDM_VS_CTXT_CODEWIN` 不会合并进编辑器右键菜单，必须经 Group 中转**（本机可用参照：UnrealVS 的 `UBTSubMenuGroupContainer` 模式——Group parent 到 `IDM_VS_CTXT_CODEWIN`，Button parent 到该 Group）。同一 Button 可挂多个 Parent 做多处放置（如同时进 Tools 菜单组）。注意 `ProvideMenuResource` 版本号 +1 这类缓存手段对此无效（2026-08-29 实测排查全流程：命令已注册可用、纯放置不合并）。
4. **`devenv /log <文件路径>` 的 `/log` 会吃掉后面的文件参数当日志输出路径**——会把 ActivityLog XML 写进并覆盖那个源文件！启动 VS 调试时永远不要把 /log 和文档路径放同一命令行；要指定日志用 `/log <独立日志路径>`，要打开文件就分两行/省略 /log。（2026-08-28 踩过，BlameMargin.cs 被 339KB ActivityLog 覆盖）
5. **ActivityLog 注意**：`devenv /log` 写的 `%APPDATA%\Microsoft\VisualStudio\17.0_*\ActivityLog.xml` 在本机实测不更新（一直是旧文件），别依赖它排查；用 DTE ROT 探针（`!VisualStudio.DTE.17.0:<pid>`）代替。
5. **VS 命令注册验证不靠猜**：包未加载时该包的菜单项整体隐藏；`[ProvideAutoLoad(CodeWindow, BackgroundLoad)]` 的包在打开第一个代码窗口后几秒才加载，立刻右键可能还没有。
6. **Extensions 目录下同一组件两个 Identity Id 并存 = 隐形覆盖**：2026-10-15 发现 `Extensions\` 里残留 2026-08-31 调试期装的**独立** CodeLensOopProvider vsix（Id `CodeLensOopProviderVsixx64.a6d0c985-...`，与总包 `P4VsProviderx64.Perforce.6C2C3283-...` 不同 Id，升级不会被清掉）——同一包 guid 两个提供者，VS 可能加载旧 dll 导致新功能静默不生效。排查：`grep -o 'Id="[^"]*"' Extensions/*/extension.vsixmanifest` 查重；清法：`VSIXInstaller /q /u:<IdentityId>`（VS 需关闭）。

## 升级机制

- 同 Identity Id（`P4VsProviderx64.Perforce.6C2C3283-...`）且版本更高 = 升级：VS 删旧随机目录 `Extensions\<random1>\`、装到新随机目录 `<random2>\`，日志里大量 "Deleting file" 是正常升级过程
- 已装的官方发布版是 `2025.1.282.7872`，所以本仓库必须 > 它
- **每次修改源码必须递增一次小版本号**（当前 **2026.1.4.0**）。**同版本号覆盖安装是陷阱**（2026-08-31 实测）：VSIXInstaller 返回 exit 0 看似成功，但 `Extensions\` 下会残留新旧两份随机目录，VS 可能加载旧 DLL——功能不生效且无报错。修复流程：`VSIXInstaller /u:<IdentityId>` 卸载 → 手动删掉残留的含目标 dll 的旧目录 → 抬版本号重装。验证安装结果：检查 `Extensions\<新目录>\extension.vsixmanifest` 的 Version 和 dll 时间戳/内容字节（.NET dll 里字符串是 UTF-16LE，搜字节模式别用 grep 文本）。

## 版本号统一清单（2026-10-15 升到 2026.1.4.0；每次改源码递增）

**要改（展示层）：**
- 4 个 `source.extension.vsixmanifest` 的 Identity Version：`HelixVS/`（总包，扩展管理器显示的就是它）、`P4VS/`、`P4VS_StartPage/`、`CodeLensVisx/`
- `P4VS/P4VsProvider/P4VsProvider.cs`：`InstalledProductRegistration` 第 3 参 + `ProvideLoadKey` 第 2 参（Help→About 显示）
- 5 个 `Properties/AssemblyInfo.cs` 的 `AssemblyFileVersion`（+ 有的话 `AssemblyInformationalVersion`）

**不要改：**
- `AssemblyVersion`——822 处 `.resx` 强名称引用（`P4VS10, Version=2011.1.0.0` 形式）指向它。官方发布版同样钉死（2025.1 的 dll AssemblyVersion 仍是 2011.1.0.0）
- `Versions.cs` `V11_1 = new Version(2011, 1)`、`CommandStatus.cs` `P4V.version() < 2011.1`——P4 服务器 / P4V 的版本比较逻辑
- Copyright 头注释

**安全性备注**：`P4VsProvider/Action.cs` 的 `P4VsP4VSHelp` 用自身 FileVersion 前两位拼官方文档 URL（`rYY.M`），404 时回退 `doc.current`，所以 FileVersion 改到 2026.1.0.0 无副作用。
