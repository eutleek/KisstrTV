@echo off
rem ============================================
rem  KisstrTV (WebView2 / WinForms) build script
rem
rem  Same toolchain as KISSTR Radio: a single csc.exe invocation, no
rem  Visual Studio, no MSBuild, no SDK. Everything (HTML, images, the
rem  three WebView2 DLLs, the icon) is embedded as a resource, so the
rem  output is one standalone exe (~2 MB instead of Electron's 70 MB).
rem
rem  Folder layout (ASCII names on purpose - CJK paths break cmd.exe
rem  when the .bat is not saved in the local ANSI codepage):
rem
rem    ..\dist\      <- output: KisstrTV.exe
rem    ..\src\       <- the real source: index.html + assets
rem    ..\build\       <- this folder (toolchain)
rem
rem  Workflow:
rem    1. edit ..\src\index.html  (never touch copies elsewhere)
rem    2. run this script - it syncs the source, then compiles
rem
rem  Save this file as UTF-8 WITHOUT BOM. A BOM makes cmd.exe fail with
rem  "'锘緻echo' is not recognized ...".
rem ============================================
setlocal
cd /d "%~dp0"

rem -- 0) check the three WebView2 DLLs exist (they are NOT in git) --
set MISSING=
if not exist "Microsoft.Web.WebView2.Core.dll" set MISSING=Microsoft.Web.WebView2.Core.dll
if not exist "Microsoft.Web.WebView2.WinForms.dll" set MISSING=%MISSING% Microsoft.Web.WebView2.WinForms.dll
if not exist "WebView2Loader.dll" set MISSING=%MISSING% WebView2Loader.dll
if not "%MISSING%"=="" (
  echo [ERROR] Missing WebView2 DLL^(s^): %MISSING%
  echo.
  echo These three files are distributed by Microsoft, so they are not in the repo.
  echo Get them from NuGet ^(Microsoft.Web.WebView2^) and drop them in this folder.
  echo.
  pause
  exit /b 1
)

rem -- 1) sync sources from 02-src (fall back to local copies if absent) --
if exist "..\src\index.html" (
  copy /y "..\src\index.html" ".\index.html" >nul
  echo [sync] index.html  ^<-  src
)
if exist "..\src\assets\icon-128.png" (
  copy /y "..\src\assets\icon-128.png" ".\icon-128.png" >nul
  echo [sync] icon-128.png  ^<-  src\assets
)
if exist "..\src\assets\icon.png" (
  copy /y "..\src\assets\icon.png" ".\icon.png" >nul
  echo [sync] icon.png  ^<-  src\assets
)
if exist "..\src\assets\icon.ico" (
  copy /y "..\src\assets\icon.ico" ".\app.ico" >nul
  copy /y "..\src\assets\icon.ico" ".\app-exe.ico" >nul
  echo [sync] icon.ico  ^<-  src\assets
)

rem -- 2) stop any running instance so the exe is not locked --
taskkill /IM "KisstrTV.exe" /F >nul 2>&1
ping -n 2 127.0.0.1 >nul

rem -- 3) vault.impl.cs is private and NOT in git --
rem    present : define KISSTR_VAULT and compile it, the #if in services.cs
rem              then yields to the real implementation
rem    absent  : open-source build, Vault falls back to the stub (runs, no data)
rem    NOTE: keep this file ASCII-only. cmd.exe parses .bat in the local ANSI
rem    codepage and CJK text here gets misread as commands.
set VAULTSRC=
set VAULTDEF=
if exist "vault.impl.cs" set VAULTSRC=vault.impl.cs
if exist "vault.impl.cs" set VAULTDEF=/define:KISSTR_VAULT
if "%VAULTSRC%"=="" (
  echo [vault] stub - open-source build, library empty
) else (
  echo [vault] private implementation - full build
)

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo [ERROR] .NET Framework 4.x compiler csc.exe not found.
  pause
  exit /b 1
)

if not exist "..\dist" mkdir "..\dist"

echo Compiling...
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ %VAULTDEF% ^
  "/out:..\dist\KisstrTV.exe" ^
  /win32icon:app-exe.ico ^
  /r:Microsoft.Web.WebView2.Core.dll ^
  /r:Microsoft.Web.WebView2.WinForms.dll ^
  /r:System.Drawing.dll ^
  /r:System.Windows.Forms.dll ^
  /r:System.Web.Extensions.dll ^
  "/resource:app.ico,KisstrTV.app.ico" ^
  "/resource:index.html,KisstrTV.index.html" ^
  "/resource:icon-128.png,KisstrTV.icon128.png" ^
  "/resource:icon.png,KisstrTV.icon.png" ^
  "/resource:WebView2Loader.dll,KisstrTV.WebView2Loader.dll" ^
  "/resource:Microsoft.Web.WebView2.Core.dll,KisstrTV.WvCore.dll" ^
  "/resource:Microsoft.Web.WebView2.WinForms.dll,KisstrTV.WvWinForms.dll" ^
  main.cs core.cs services.cs %VAULTSRC%

if errorlevel 1 goto buildfail
echo [OK] Built: ..\dist\KisstrTV.exe
goto done

:buildfail
echo [FAILED] Build error, see messages above.

:done
