@echo off
cd /d "%~dp0"
if exist "%~dp0app\TerminalCustom.exe" (
    start "" "%~dp0app\TerminalCustom.exe"
) else (
    echo Preparando o Terminal Custom pela primeira vez...
    dotnet publish "%~dp0src\CustomTerminal.csproj" -c Release -o "%~dp0app"
    if errorlevel 1 (
        echo.
        echo Nao foi possivel compilar o terminal.
        pause
        exit /b 1
    )
    start "" "%~dp0app\TerminalCustom.exe"
)
