@echo off
rem Generates ThermoSync ready to deploy on another PC (no .NET install required).
rem Output: dist\ThermoSync.exe
rem Always recompiles from scratch using the current source code.
cd /d "%~dp0"

net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Solicitando privilegios de administrador para encerrar o processo e publicar...
    powershell -Command "Start-Process cmd -ArgumentList '/c \"\"%~f0\"\"' -Verb RunAs"
    exit /b
)

where dotnet >nul 2>&1
if errorlevel 1 (
    if exist "%ProgramFiles%\dotnet\dotnet.exe" (
        set "PATH=%ProgramFiles%\dotnet;%PATH%"
    )
)
echo Closing ThermoSync if running ...
taskkill /f /im ThermoSync.exe >nul 2>&1
timeout /t 1 /nobreak >nul 2>&1
echo Building ThermoSync.exe from current source ...
rmdir /s /q obj\Release >nul 2>&1
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o "dist" -nologo -v q
if errorlevel 1 (
    echo.
    echo BUILD FAILED. Check the errors above. The old .exe was NOT updated.
    pause
    exit /b 1
)
echo.
echo Done: %~dp0dist
for %%F in ("dist\ThermoSync.exe") do echo Generated at %%~tF (check the timestamp^)
pause
