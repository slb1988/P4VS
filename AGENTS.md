# AGENTS.md

> **每次开始工作前必须先读取 [SKILL.md](SKILL.md)**——它包含本仓库的构建安装流程、版本号铁律、架构速查和全部 references 索引，不读必踩坑。

## 核心规则（详细版都在 SKILL.md，此处只是红线提醒）

1. **先读 `SKILL.md`**，再按其指向读 `references/` 下的专题文档（build-and-install / architecture / p4-blame-feature / startup-performance / debug-tooling）
2. **每次修改源码必须递增一次小版本号**（4 个 vsixmanifest + 5 个 AssemblyInfo.cs，清单见 `references/build-and-install.md`）。严禁同版本号覆盖安装——会 exit 0 假象成功但 VS 加载旧 DLL
3. **`AssemblyVersion` 永远钉死在 2011.1.0.0 / 1.0.0.0**——822 处 .resx 强名称引用指向它，只能改展示层版本号（vsixmanifest Identity / AssemblyFileVersion / AssemblyInformationalVersion）
4. **安装前必须关闭所有 devenv.exe**，安装走 `cmd.exe //c "D:\Github\P4VS\install_p4vs.bat"`（脚本已入库，Git Bash 直接调 VSIXInstaller 会被 MSYS 参数转换坑，exit 209）
