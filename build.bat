@echo off
REM ============================================================
REM  GloriaCloud (PCL2 修改版) 一键构建脚本  —  在 Windows 上运行
REM  前置环境：Visual Studio 2022（社区版/专业版/企业版）
REM            勾选工作负载 ".NET 桌面开发"
REM            （会自动安装 MSBuild、.NET Framework 4.8 开发包、WPF 构建目标）
REM  说明：本脚本自动定位 MSBuild 并还原 NuGet 包、编译 Release。
REM ============================================================
SETLOCAL ENABLEDELAYEDEXPANSION
SET "SLN=Plain Craft Launcher 2.sln"
SET "MSBUILD="

IF EXIST "%ProgramFiles%\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"   SET "MSBUILD=%ProgramFiles%\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
IF EXIST "%ProgramFiles(x86)%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe" SET "MSBUILD=%ProgramFiles(x86)%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
IF EXIST "%ProgramFiles%\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe" SET "MSBUILD=%ProgramFiles%\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe"
IF EXIST "%ProgramFiles%\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe"   SET "MSBUILD=%ProgramFiles%\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
IF NOT DEFINED MSBUILD SET "MSBUILD=msbuild"

IF NOT EXIST %SLN% (
  ECHO 未找到 %SLN%，请在本文件所在目录运行本脚本。
  PAUSE
  EXIT /B 1
)

ECHO [1/2] 还原 NuGet 包 ...
"%MSBUILD%" %SLN% /t:Restore /p:Configuration=Release
IF NOT "!ERRORLEVEL!"=="0" ( ECHO NuGet 还原失败，尝试继续编译... )

ECHO [2/2] 编译 Release (Any CPU) ...
"%MSBUILD%" %SLN% /p:Configuration=Release /p:Platform="Any CPU" /p:Prefer32Bit=false
IF "!ERRORLEVEL!"=="0" (
  ECHO.
  ECHO ===== 构建成功 =====
  ECHO EXE 位于：Plain Craft Launcher 2\bin\Plain Craft Launcher 2.exe
) ELSE (
  ECHO ===== 构建失败，请检查上方错误 =====
)
PAUSE
