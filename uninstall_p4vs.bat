@echo off
REM 按 Identity Id 卸载扩展（主实例）。用法：cmd.exe /c "D:\Github\P4VS\uninstall_p4vs.bat" [IdentityId]
REM 默认卸总包；排查 Extensions 目录残留/双 Id 并存时传第二个 Id，例如：
REM   uninstall_p4vs.bat CodeLensOopProviderVsixx64.a6d0c985-cb62-4fb6-ba8c-53d2a80dc6f5
set ID=%~1
if "%ID%"=="" set ID=P4VsProviderx64.Perforce.6C2C3283-424C-44B2-B1A7-B12D635DE7CF
"C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\VSIXInstaller.exe" /q /u:%ID%
echo UNINSTALL_EXIT=%ERRORLEVEL%
