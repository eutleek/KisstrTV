@echo off
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" ( echo [ERR] csc not found & exit /b 1 )

"%CSC%" /nologo /target:exe /r:System.Web.Extensions.dll /out:vaulttest.exe vaulttest.cs
if errorlevel 1 ( echo [ERR] compile failed & exit /b 1 )

echo --- run ---
vaulttest.exe "%TEMP%\vault-26000.json"
