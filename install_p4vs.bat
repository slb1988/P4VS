@echo off
REM P4VS 安装脚本（主实例）。用法：cmd.exe /c "D:\Github\P4VS\install_p4vs.bat"
REM 必须在 Git Bash 里以 cmd.exe //c 方式调用，直接 bash 调 VSIXInstaller 会被 MSYS 参数转换坑（exit 209）
REM 安装前必须关闭所有 devenv.exe，否则 exit 2004（日志尾部列阻塞 PID）
REM 目标版本号必须高于已装版本，否则 exit 1001 AlreadyInstalled
REM 排障表见 references/build-and-install.md
"C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\VSIXInstaller.exe" /q /logFile:p4vs_install.log "D:\Github\P4VS\bin\Release\P4VS.vsix"
echo VSIX_EXIT=%ERRORLEVEL%
