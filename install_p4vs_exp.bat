@echo off
REM P4VS 安装到 Exp 实验沙箱（不污染主实例，无需关主 VS）。用法：cmd.exe /c "D:\Github\P4VS\install_p4vs_exp.bat"
REM 注意 /rootSuffix:Exp 是冒号不是空格（空格形式 exit 2001）
REM 启动沙箱：devenv.exe /RootSuffix Exp；hive 在 %%LOCALAPPDATA%%\Microsoft\VisualStudio\17.0_*Exp\，整个删掉=完全重置
"C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\VSIXInstaller.exe" /q /rootSuffix:Exp "D:\Github\P4VS\bin\Release\P4VS.vsix"
echo EXP_INSTALL_EXIT=%ERRORLEVEL%
