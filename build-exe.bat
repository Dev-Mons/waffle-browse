@echo off
setlocal EnableExtensions

pushd "%~dp0" || exit /b 1

set "APP_PROJECT=src\Waffle.Browse.App\Waffle.Browse.App.csproj"
set "CONFIGURATION=Release"
set "RUNTIME=win-x64"
set "OUTPUT=publish\%RUNTIME%"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] dotnet CLI was not found. Install the .NET SDK and try again.
    goto :fail
)

echo [1/2] Cleaning %OUTPUT% ...
if exist "%OUTPUT%" rmdir /s /q "%OUTPUT%"
if exist "%OUTPUT%" (
    echo [ERROR] Could not clean the existing publish directory.
    set "EXITCODE=1"
    goto :fail_with_code
)

echo [2/2] Publishing %APP_PROJECT% (framework-dependent, %RUNTIME%, %CONFIGURATION%) ...
dotnet publish "%APP_PROJECT%" ^
    -c %CONFIGURATION% ^
    -r %RUNTIME% ^
    -warnaserror ^
    --self-contained false ^
    -p:PublishSingleFile=false ^
    -p:DebugType=none ^
    -p:DebugSymbols=false ^
    -o "%OUTPUT%"
if errorlevel 1 goto :fail

if not exist "%OUTPUT%\Waffle.Browse.App.exe" (
    echo [ERROR] App executable was not produced.
    set "EXITCODE=1"
    goto :fail_with_code
)
if exist "%OUTPUT%\*.pdb" (
    echo [ERROR] Debug symbols were copied into the product publish directory.
    set "EXITCODE=1"
    goto :fail_with_code
)

echo.
echo Build completed.
echo Output directory: %CD%\%OUTPUT%
echo   App: Waffle.Browse.App.exe
echo Note: The app requires .NET 9 Desktop Runtime (%RUNTIME%).
set "EXITCODE=0"
goto :exit

:fail
set "EXITCODE=%ERRORLEVEL%"

:fail_with_code
echo.
echo Build failed with exit code %EXITCODE%.
pause

:exit
popd
endlocal & exit /b %EXITCODE%
