@echo off
REM atg.cmd - Convenience wrapper so `atg ...` works from cmd.exe.
REM Delegates to atg.ps1, which resolves the interpreter and vendor path.
pwsh -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0atg.ps1" %*
exit /b %ERRORLEVEL%
