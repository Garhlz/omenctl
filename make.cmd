@echo off
rem
rem  omenctl build helper
rem
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
set DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
setlocal EnableDelayedExpansion
set DOTNET_CLI_HOME=%~dps0.dotnet
set dotnet=%USERPROFILE%\scoop\apps\dotnet-sdk\current\dotnet.exe
if not exist "%dotnet%" set dotnet=dotnet
set DOTNET_ROOT=%USERPROFILE%\scoop\apps\dotnet-sdk\current
set op_scope=build clean clean-driver agent-build agent-run diag-build diag-run gui-build gui-run gui-publish-agent release usage
set op=%~1
set sc=%SystemRoot%\System32\sc.exe
set driver_service=R0omenctl
set agent_project=src\omenctl.Agent\omenctl.Agent.csproj
set agent_bin=src\omenctl.Agent\bin\x64\Release\net10.0-windows\omenctl.dll
set diag_project=diagnostics\omenctl.Diagnostics\omenctl.Diagnostics.csproj
set diag_bin=diagnostics\omenctl.Diagnostics\bin\x64\Release\net10.0-windows\omenctl.Diagnostics.dll
pushd %~dps0
for %%p in (%op_scope%) do if "%op%"=="%%p" echo BEGIN %~n0 (%op%) & goto %op%
echo BEGIN %~n0 & goto usage

:build
"%dotnet%" build omenctl.sln -c Release -p:Platform=x64
if errorlevel 1 goto fail
goto end

:clean
call :DriverClean
"%dotnet%" clean omenctl.sln -c Release -p:Platform=x64
if errorlevel 1 goto fail
goto end

:clean-driver
call :DriverClean
goto end

:agent-build
"%dotnet%" build omenctl.sln -c Release -p:Platform=x64
if errorlevel 1 goto fail
goto end

:agent-run
if exist %agent_bin% ( "%dotnet%" %agent_bin% ) ^
else "%dotnet%" run --project %agent_project% -c Release -p:Platform=x64
if errorlevel 1 goto fail
goto end

:diag-build
"%dotnet%" build omenctl.sln -c Release -p:Platform=x64
if errorlevel 1 goto fail
goto end

:diag-run
set "diag_args=%*"
set "diag_args=!diag_args:*%op%=!"
if exist "%diag_bin%" goto diag-run-bin
"%dotnet%" run --project %diag_project% -c Release -p:Platform=x64 --!diag_args!
if errorlevel 1 goto fail
goto end

:diag-run-bin
"%dotnet%" "%diag_bin%"!diag_args!
if errorlevel 1 goto fail
goto end

:gui-build
cd src\omenctl.Gui
cargo tauri build
if errorlevel 1 goto fail
goto end

:gui-run
"%dotnet%" build %agent_project% -c Release -p:Platform=x64
if errorlevel 1 goto fail
cd src\omenctl.Gui
cargo tauri dev
if errorlevel 1 goto fail
goto end

:gui-publish-agent
"%dotnet%" publish src\omenctl.Agent\omenctl.Agent.csproj -c Release -r win-x64 --self-contained true -o src\omenctl.Gui\bin
if errorlevel 1 goto fail
goto end

:release
echo Step 1/4: Building .NET solution...
"%dotnet%" build omenctl.sln -c Release -p:Platform=x64
if errorlevel 1 goto fail

echo Step 2/4: Publishing self-contained agent...
set release_dir=%~dps0release\omenctl
if exist "%release_dir%" rmdir /s /q "%release_dir%"
mkdir "%release_dir%"
"%dotnet%" publish src\omenctl.Agent\omenctl.Agent.csproj -c Release -r win-x64 --self-contained true -o "%release_dir%"
if errorlevel 1 goto fail

echo Step 3/4: Building Tauri GUI...
cd src\omenctl.Gui
cargo tauri build
if errorlevel 1 goto fail
cd %~dps0

echo Step 4/4: Packaging release...
copy /y src\omenctl.Gui\src-tauri\target\release\omenctl-gui.exe "%release_dir%\" >nul
copy /y Resources\Driver.sys.gz "%release_dir%\" >nul
copy /y Resources\README.md "%release_dir%\Driver-README.md" >nul
copy /y LICENSE.md "%release_dir%\" >nul
powershell -Command "Compress-Archive -Path '%release_dir%\*' -DestinationPath '%~dps0release\omenctl-v0.1.0.zip' -Force"
echo Release package created: release\omenctl-v0.1.0.zip
goto end

:usage
echo Usage: %~n0 ^<build^|clean^|clean-driver^|agent-build^|agent-run^|diag-build^|diag-run^|gui-build^|gui-run^|gui-publish-agent^|release^|usage^>
goto end

:DriverClean
echo Releasing driver service %driver_service% if present...
%sc% stop "%driver_service%" >nul 2>nul
%sc% delete "%driver_service%" >nul 2>nul
exit /b 0

:fail
echo END %~n0
popd
exit /b 1

:end
echo END %~n0
popd
