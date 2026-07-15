@echo off
setlocal EnableExtensions

pushd "%~dp0" || exit /b 1

set "SOLUTION=waffle-browse.slnx"
set "APP_PROJECT=src\Waffle.Browse.App\Waffle.Browse.App.csproj"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] dotnet CLI was not found. Install the .NET SDK and try again.
    goto :fail
)

echo [1/2] Building %SOLUTION%...
dotnet build "%SOLUTION%"
if errorlevel 1 goto :fail

if /i "%~1"=="build" (
    echo.
    echo Build completed.
    goto :success
)

echo.
echo [2/2] Running Waffle Browse...
dotnet run --no-build --project "%APP_PROJECT%"
set "EXITCODE=%ERRORLEVEL%"
goto :exit

:success
set "EXITCODE=0"
goto :exit

:fail
set "EXITCODE=%ERRORLEVEL%"
echo.
echo Build or run failed with exit code %EXITCODE%.
pause

:exit
popd
endlocal & exit /b %EXITCODE%
