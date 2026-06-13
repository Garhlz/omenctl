@echo off
rem
rem  Omen Agent build helper
rem
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
set DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
setlocal
set DOTNET_CLI_HOME=%~dps0.dotnet
set dotnet=%USERPROFILE%\scoop\apps\dotnet-sdk\current\dotnet.exe
if not exist "%dotnet%" set dotnet=dotnet
set DOTNET_ROOT=%USERPROFILE%\scoop\apps\dotnet-sdk\current
set op_scope=build clean clean-driver agent-build agent-run usage
set op=%~1
set sc=%SystemRoot%\System32\sc.exe
set driver_service=R0OmenMon_Agent
set agent_project=src\OmenMon.Agent\OmenMon.Agent.csproj
set agent_bin=src\OmenMon.Agent\bin\x64\Release\net10.0-windows\OmenMon.Agent.dll
pushd %~dps0
for %%p in (%op_scope%) do if "%op%"=="%%p" echo BEGIN %~n0 (%op%) & goto %op%
echo BEGIN %~n0 & goto usage

:build
"%dotnet%" build OmenMon.Modern.sln -c Release -p:Platform=x64
if errorlevel 1 goto fail
goto end

:clean
call :DriverClean
"%dotnet%" clean OmenMon.Modern.sln -c Release -p:Platform=x64
if errorlevel 1 goto fail
goto end

:clean-driver
call :DriverClean
goto end

:agent-build
"%dotnet%" build OmenMon.Modern.sln -c Release -p:Platform=x64
if errorlevel 1 goto fail
goto end

:agent-run
if exist %agent_bin% ( "%dotnet%" %agent_bin% ) ^
else "%dotnet%" run --project %agent_project% -c Release -p:Platform=x64
if errorlevel 1 goto fail
goto end

:usage
echo Usage: %~n0 ^<build^|clean^|clean-driver^|agent-build^|agent-run^|usage^>
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
