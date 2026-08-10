@echo off
setlocal
set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"
set "COORDINATOR=%ROOT%\Coordinator\DevBridge.Coordinator.exe"

if exist "%COORDINATOR%" (
  "%COORDINATOR%" --root "%ROOT%" %*
  exit /b %ERRORLEVEL%
)

where dotnet >nul 2>nul
if errorlevel 1 (
  echo DevBridge coordinator is not built.
  echo Build it with: dotnet publish Source\Coordinator\DevBridge.Coordinator.csproj -c Release -r win-x64 --self-contained false -o Coordinator
  exit /b 2
)

if not exist "%ROOT%\Source\Coordinator\DevBridge.Coordinator.csproj" (
  echo DevBridge coordinator project is missing.
  exit /b 2
)

dotnet run --project "%ROOT%\Source\Coordinator\DevBridge.Coordinator.csproj" -- --root "%ROOT%" %*
exit /b %ERRORLEVEL%
