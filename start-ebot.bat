@echo off
setlocal

cd /d "%~dp0"

echo [EBot] Building...
dotnet build src/EBot.WebHost/EBot.WebHost.csproj -c Release -v quiet
if errorlevel 1 (
    echo [EBot] Build FAILED.
    pause
    exit /b 1
)

echo [EBot] Starting on http://localhost:5000
echo [EBot] Open browser: http://localhost:5000
echo [EBot] Press Ctrl+C to stop.
echo.
dotnet run --project src/EBot.WebHost/EBot.WebHost.csproj -c Release --no-build --debug
