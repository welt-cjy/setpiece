@echo off
rem 开发用启动器：用 dotnet 宿主运行 UI，避免未签名 exe 被安全软件降权。
rem 首次运行会自动构建；构建产物在 src\Setpiece.App\bin\Debug。
cd /d "%~dp0"

set APP=src\Setpiece.App\bin\Debug\net8.0-windows\Setpiece.App.dll
if not exist "%APP%" (
    echo Building Setpiece...
    dotnet build src\Setpiece.App\Setpiece.App.csproj
    if errorlevel 1 (
        pause
        exit /b 1
    )
)

dotnet "%APP%"
