@echo off
cd /d "%~dp0"
echo Preparando a versao atual do Terminal Custom...
dotnet publish "%~dp0src\CustomTerminal.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -o "%~dp0app"
if errorlevel 1 (
    echo.
    echo Nao foi possivel compilar o terminal.
    pause
    exit /b 1
)
start "" "%~dp0app\TerminalCustom.exe"
