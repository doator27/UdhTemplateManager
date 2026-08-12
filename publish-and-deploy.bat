@echo off
echo Publishing Hardware Template Builder App to Network Location...
echo.

set TARGET_DIR=T:\Server\LISTING DEPARTMENT\DTyler\Template Jobs\TempalteApp

echo Target Directory: %TARGET_DIR%
echo.

REM Publish the application
echo Publishing application...
dotnet publish HardwareTemplateBuilder.App\HardwareTemplateBuilder.App.csproj -c Release -r win-x86 --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=false -p:PublishTrimmed=false

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo ========================================
    echo Publish FAILED!
    echo ========================================
    echo.
    pause
    exit /b 1
)

echo.
echo Copying to network location...

REM Create target directory if it doesn't exist
if not exist "%TARGET_DIR%" (
    echo Creating target directory...
    mkdir "%TARGET_DIR%"
)

REM Copy published files
xcopy /E /I /Y "HardwareTemplateBuilder.App\bin\Release\net8.0\win-x86\publish\*" "%TARGET_DIR%\"

if %ERRORLEVEL% EQU 0 (
    echo.
    echo ========================================
    echo Publish and Deploy SUCCESSFUL!
    echo ========================================
    echo.
    echo Files deployed to: %TARGET_DIR%
    echo.
    pause
) else (
    echo.
    echo ========================================
    echo Copy to network location FAILED!
    echo ========================================
    echo.
    pause
)
