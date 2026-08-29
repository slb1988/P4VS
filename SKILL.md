---
name: p4vs
title: P4VS 插件开发工作流
description: P4VS（P4 for Visual Studio）插件源码仓库的开发参考 - 构建打包、安装升级、四项目架构、版本号铁律、自研编辑器 Blame 功能。用户提到 p4vs、VS 扩展编译、vsix 安装失败、Toggle P4 Blame、修改 P4VS 源码时使用。
tags: [P4VS, Visual-Studio-Extension, VSIX, Perforce, Blame]
---

# P4VS 插件开发工作流

> 本目录 = Perforce 官方 P4VS（Helix Plugin for Visual Studio）源码（github.com/perforce/P4VS，source-available EULA，非开源协议）+ 自研改动。
> 个人本地 git 仓库，不参与 MainDev 的 P4 / skill 索引体系（改了不用跑 skill_index_gen.bat）。

## 本 fork 与上游的差异

1. **P4 Blame 编辑器边距**（自研）：`CodeLensVisx/Blame/`，右键 Toggle P4 Blame。细节见 references/p4-blame-feature.md
2. **`LazyLoadStatus` 默认 true + `PreloadScmCache` 默认 false**：官方源码默认值不统一（`P4VsProviderService` 三处默认 true、其余 false），本 fork 全部 `GetBool("LazyLoadStatus", ...)` 读取点统一为 true——Solution Explorer 的 Scc 图标状态改为懒加载。`PreloadScmCache` 六处读取点统一翻 false——**这是「点确定连接后卡死 73~95s」的真凶**（SolutionPath setter 对整个解决方案目录树做递归 fstat），翻 false 后连接全程 ~100ms。注意 P4VS 偏好落盘在 `%APPDATA%\Perforce\P4VS\VS2022\LocalSettings.ini`，**已存储值压过源码默认值**，两处 ini 也已同步改。见 references/startup-performance.md
3. `Microsoft.Deployment.WindowsInstaller` 引用补 HintPath 到 `libs\Release\`（官方依赖构建机 GAC）
4. 版本号体系统一 2026.1.0.0（高于官方 2025.1.282.7872 才能覆盖安装）

## 仓库结构

| 项目 | 产物 | 作用 |
|---|---|---|
| `HelixVS/` | `bin\Release\P4VS.vsix` | 总 vsix（安装入口），manifest 把其余 4 个组件打成一个扩展 |
| `P4VS/` | P4VS.dll | 主包 - Scc 源代码管理 provider、连接管理（`P4VsProvider.CurrentScm`）、工具窗口 |
| `P4VS_StartPage/` | | 起始页 |
| `CodeLensVisx/` | HelixCodeLensOopProviderVsix.dll | 编辑器侧包 - CodeLens 命令 + **P4 Blame（自研，在 `Blame/`）** |
| `CodeLensOopProvider/` | | CodeLens 数据 provider（ServiceHub 独立进程组件） |

构建顺序由 ProjectReference 自动推导；只改编辑器侧代码时可只编 `HelixVS/HelixVS.csproj`（会连带编依赖链）。

## 30 秒构建安装

```bash
cd /d/Github/P4VS
/d/Github/_tools/nuget.exe restore P4VS.sln     # 一次性前置见 references
"/c/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/MSBuild.exe" P4VS.sln //p:Configuration=Release
# 关闭所有 devenv 后：
cmd.exe //c "C:\Windows\Temp\install_p4vs.bat"  # 输出 VSIX_EXIT=0 即成功
```

> 前置条件、install bat 内容、exit code 排障表（209/2004/1001）：[build-and-install](references/build-and-install.md)

## 铁律

<memory category="core-rules">
- **AssemblyVersion 永远钉死在 2011.1.0.0 / 1.0.0.0**：822 处 .resx 强名称引用（`P4VS10, Version=2011.1.0.0` 形式）指向它，改了全部 WinForms 对话框资源解析炸。要展示版本只改 AssemblyFileVersion / AssemblyInformationalVersion / 四个 vsixmanifest。
- **安装是升级判定**：vsix Identity Version 必须高于已装版本（官方发布版是 2025.1.282.7872），本仓库统一用 **2026.1.0.0**，否则 VSIXInstaller exit 1001 AlreadyInstalled。
- 逻辑代码里的 `2011.1` 不是插件版本号（`Versions.cs` 的 P4 服务器版本判断、`CommandStatus.cs` 的 P4V 版本比较），全局替换前先确认语境。
</memory>

## 自研功能 - P4 Blame

代码编辑器右键 **Toggle P4 Blame** → 左边距逐行显示 `CL号 用户 MM-dd`（git-blame 风格，按新旧渐变配色），hover 出完整 CL 描述；再点一次关闭，重开即刷新。编辑文件后整栏变灰标记 stale。

- 实现在 `CodeLensVisx/Blame/`（3 个文件，约 500 行）
- 数据直接复用主插件活动连接 `P4VsProvider.CurrentScm.Connection.Repository`，不建新连接、无 charset 坑
- 官方源码无此功能（它的 "Time-lapse View" 只是拉起外部 P4V）

> 数据流、渲染细节、p4api.net annotate 三个实测坑：[p4-blame-feature](references/p4-blame-feature.md)

## 架构速查

- **命令路由两条路**：CodeLens 面板命令走 package 的 `IOleCommandTarget` 链（CommandWellOnly）；编辑器右键菜单走 `OleMenuCommandService` 注册（Blame 命令，cmdid 0x1015）
- **编辑器右键放置**：Button 直挂 `IDM_VS_CTXT_CODEWIN` **不合并**；必须 Group 中转（Group→`IDM_VS_CTXT_CODEWIN`，Button→该 Group）。命令进命令表但菜单不出现时的标准修法
- **包必须自动加载**否则右键项不出现：`[ProvideAutoLoad("8FE2DF1D-E0DA-4EBE-9D5C-415D40AD4874", BackgroundLoad)]`（CodeWindow UI context）
- **同进程扩展拿 P4 连接**用 `P4VsProvider.CurrentScm`；跨进程（CodeLens 组件）才走 memory-mapped file `connection-info-{devenvPid}`（`HelixUtility`）
- **MEF 编辑器扩展**（margin 等）必须在 `HelixVS/source.extension.vsixmanifest` 给宿主项目补 `MefComponent` asset，否则 VS 不扫描该 dll

> 项目依赖图、asset 打包、命令扩展示例：[architecture](references/architecture.md)

## References

| 主题 | Reference |
|---|---|
| 构建前置、编译命令、安装 bat、VSIXInstaller exit code 排障、版本号统一清单 | [build-and-install](references/build-and-install.md) |
| 解决方案架构、vsix asset 打包、命令路由、P4 连接共享两种模式 | [architecture](references/architecture.md) |
| P4 Blame 实现细节、p4api.net annotate API 坑、ZoomLevel/折行渲染坑、调试环境变量 | [p4-blame-feature](references/p4-blame-feature.md) |
| 启动性能结构与测量方法（什么贵、PkgDef 重建、LazyLoadStatus、CodeLens 成本模型） | [startup-performance](references/startup-performance.md) |
| 扩展调试工具链（DTE ROT 探针、PrintWindow 后台截图、MSYS 参数坑、Exp 沙箱、菜单合并事实） | [debug-tooling](references/debug-tooling.md) |
