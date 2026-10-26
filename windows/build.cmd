@echo off
rem FreeFlow Windows: build, test, publish single-file exe to windows\dist\FreeFlow.exe
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo .NET SDK not found. Install it with:  winget install Microsoft.DotNet.SDK.8
  exit /b 1
)

echo === Building ===
dotnet build FreeFlow.sln -v q || exit /b 1

echo === Running tests ===
dotnet test tests\FreeFlow.Tests\FreeFlow.Tests.csproj --no-build -v q || exit /b 1

echo === Publishing single-file exe ===
if exist dist rmdir /s /q dist
dotnet publish src\FreeFlow\FreeFlow.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o dist || exit /b 1

echo.
echo Done: %~dp0dist\FreeFlow.exe
endlocal
