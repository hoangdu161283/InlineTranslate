@echo off
rem Build portable single-file exe -> dist\InlineTranslate.exe (no .NET needed on target machine)
taskkill /im InlineTranslate.exe /f >nul 2>&1
dotnet publish "%~dp0InlineTranslate\InlineTranslate.csproj" -c Release -o "%~dp0dist" -nologo
if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)
echo.
echo Done: %~dp0dist\InlineTranslate.exe
