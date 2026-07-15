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

echo [2/2] Publishing %APP_PROJECT% (framework-dependent, %RUNTIME%, %CONFIGURATION%) ...
dotnet publish "%APP_PROJECT%" ^
    -c %CONFIGURATION% ^
    -r %RUNTIME% ^
    --self-contained false ^
    -p:PublishSingleFile=false ^
    -o "%OUTPUT%"
if errorlevel 1 goto :fail

echo.
echo Build completed.
echo Output: %CD%\%OUTPUT%\Waffle.Browse.App.exe
echo Note: Requires .NET 9 Desktop Runtime (%RUNTIME%) on the target machine.
set "EXITCODE=0"
goto :exit

:fail
set "EXITCODE=%ERRORLEVEL%"
echo.
echo Build failed with exit code %EXITCODE%.
pause

:exit
popd
endlocal & exit /b %EXITCODE%
