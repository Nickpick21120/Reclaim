@echo off
rem ============================================================
rem  Reclaim — build a portable single-file Reclaim.exe
rem
rem  Requires the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0
rem  Output: publish\Reclaim.exe  (no install, runs on any 64-bit Win10/11,
rem  no .NET runtime needed on the target machine)
rem ============================================================

cd /d "%~dp0"

dotnet publish src\Reclaim.App -c Release -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -o publish

if errorlevel 1 (
  echo.
  echo Build FAILED. Is the .NET 8 SDK installed?  ^(dotnet --version^)
  pause
  exit /b 1
)

echo.
echo Done: %~dp0publish\Reclaim.exe
pause
