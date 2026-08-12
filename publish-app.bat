@echo off
echo Publishing Hardware Template Builder App...
echo.

REM Clean previous publish
if exist "HardwareTemplateBuilder.App\bin\Release\net8.0\win-x86\publish" (
    echo Cleaning previous publish...
    rmdir /s /q "HardwareTemplateBuilder.App\bin\Release\net8.0\win-x86\publish"
)

REM Publish the application
echo Publishing application...
dotnet publish HardwareTemplateBuilder.App\HardwareTemplateBuilder.App.csproj -c Release -r win-x86 --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=false -p:PublishTrimmed=false

if %ERRORLEVEL% EQU 0 (
    echo.
    echo ========================================
    echo Publish SUCCESSFUL!
    echo ========================================
    echo.
    echo Output location:
    echo %cd%\HardwareTemplateBuilder.App\bin\Release\net8.0\win-x86\publish\
    echo.
    echo You can now copy the contents to your deployment location.
    echo.
    pause
) else (
    echo.
    echo ========================================
    echo Publish FAILED!
    echo ========================================
    echo.
    pause
)
