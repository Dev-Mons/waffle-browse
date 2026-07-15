@echo off
setlocal EnableExtensions

pushd "%~dp0" || exit /b 1

set "APP_PROJECT=src\Waffle.Browse.App\Waffle.Browse.App.csproj"
set "INDEXER_PROJECT=src\Waffle.Browse.Indexer\Waffle.Browse.Indexer.csproj"
set "CONFIGURATION=Release"
set "RUNTIME=win-x64"
set "OUTPUT=publish\%RUNTIME%"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] dotnet CLI was not found. Install the .NET SDK and try again.
    goto :fail
)

echo [1/3] Cleaning %OUTPUT% ...
if exist "%OUTPUT%" rmdir /s /q "%OUTPUT%"
if exist "%OUTPUT%" (
    echo [ERROR] Could not clean the existing publish directory.
    set "EXITCODE=1"
    goto :fail_with_code
)

echo [2/3] Publishing %APP_PROJECT% (framework-dependent, %RUNTIME%, %CONFIGURATION%) ...
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

echo [3/3] Publishing %INDEXER_PROJECT% beside the app (self-contained NativeAOT) ...
dotnet publish "%INDEXER_PROJECT%" ^
    -c %CONFIGURATION% ^
    -r %RUNTIME% ^
    -warnaserror ^
    --self-contained true ^
    -p:PublishAot=true ^
    -p:StripSymbols=true ^
    -p:DebugType=none ^
    -p:DebugSymbols=false ^
    -p:CopyOutputSymbolsToPublishDirectory=false ^
    -o "%OUTPUT%"
if errorlevel 1 goto :fail

if not exist "%OUTPUT%\Waffle.Browse.App.exe" (
    echo [ERROR] App executable was not produced.
    set "EXITCODE=1"
    goto :fail_with_code
)
if not exist "%OUTPUT%\Waffle.Browse.Indexer.exe" (
    echo [ERROR] Indexer helper executable was not produced.
    set "EXITCODE=1"
    goto :fail_with_code
)
if exist "%OUTPUT%\Waffle.Browse.Indexer.dll" (
    echo [ERROR] Managed Indexer DLL was produced; the elevated helper must be NativeAOT.
    set "EXITCODE=1"
    goto :fail_with_code
)
if exist "%OUTPUT%\Waffle.Browse.Indexer.deps.json" (
    echo [ERROR] Managed Indexer dependency metadata was produced; the elevated helper must be NativeAOT.
    set "EXITCODE=1"
    goto :fail_with_code
)
if exist "%OUTPUT%\Waffle.Browse.Indexer.runtimeconfig.json" (
    echo [ERROR] Managed Indexer runtime metadata was produced; the elevated helper must be NativeAOT.
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
echo   App:     Waffle.Browse.App.exe
echo   Helper:  Waffle.Browse.Indexer.exe (self-contained NativeAOT)
echo Note: The app requires .NET 9 Desktop Runtime (%RUNTIME%); the helper is self-contained.
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
