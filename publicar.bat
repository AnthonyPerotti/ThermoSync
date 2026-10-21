@echo off
rem Generates ThermoSync ready to deploy on another PC (no .NET install required).
rem Output: ..\ThermoSync-dist\ThermoSync.exe
rem Always recompiles from scratch using the current source code.
cd /d "%~dp0"
echo Closing ThermoSync if running ...
taskkill /f /im ThermoSync.exe >nul 2>&1
echo Building ThermoSync.exe from current source ...
rmdir /s /q obj\Release >nul 2>&1
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o "..\ThermoSync-dist" -nologo -v q
if errorlevel 1 (
    echo.
    echo BUILD FAILED. Check the errors above. The old .exe was NOT updated.
    pause
    exit /b 1
)
echo.
echo Done: %~dp0..\ThermoSync-dist
for %%F in ("..\ThermoSync-dist\ThermoSync.exe") do echo Generated at %%~tF (check the timestamp^)
pause
