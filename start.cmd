@echo off
rem Windows launcher: runs run.ps1 (restore, build, install resources, launch the app).
rem Arguments (e.g. -SkipBuild) are passed through.
cd /d "%~dp0"
where pwsh >nul 2>nul
if %errorlevel%==0 (
    pwsh -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" %*
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" %*
)
set "EXITCODE=%ERRORLEVEL%"
if not "%EXITCODE%"=="0" pause
exit /b %EXITCODE%
