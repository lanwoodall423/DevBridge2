@echo off
rem Plain restart is the durable aggregate launch contract: minimal control or the union of active
rem project registrations. Production ModsConfig is available only via explicit --legacy-production;
rem this wrapper never falls back to it. Use project register/status/renew/release and status --json
rem to verify frozen inclusion before test begin. During crash isolation, poll status only.
setlocal
set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"
set "COORDINATOR=%ROOT%\Coordinator\DevBridge.Coordinator.exe"

if not exist "%COORDINATOR%" goto :fallback

"%COORDINATOR%" --root "%ROOT%" %*
exit /b %ERRORLEVEL%

:fallback
where dotnet >nul 2>nul
if errorlevel 1 goto :missing_dotnet

if not exist "%ROOT%\Source\Coordinator\DevBridge.Coordinator.csproj" goto :missing_project

dotnet run --project "%ROOT%\Source\Coordinator\DevBridge.Coordinator.csproj" -- --root "%ROOT%" %*
exit /b %ERRORLEVEL%

:missing_dotnet
echo DevBridge coordinator is not built.
echo Build it with: dotnet publish Source\Coordinator\DevBridge.Coordinator.csproj -c Release -r win-x64 --self-contained false -o Coordinator
exit /b 2

:missing_project
echo DevBridge coordinator project is missing.
exit /b 2
