@echo off
setlocal
where pwsh.exe >nul 2>nul
if %ERRORLEVEL% equ 0 (
    pwsh.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0make-and-run-windows.ps1" %*
) else (
    powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0make-and-run-windows.ps1" %*
)
set "EXIT_CODE=%ERRORLEVEL%"
if %EXIT_CODE% equ 0 exit /b 0

echo.
echo Windows launcher exited with code %EXIT_CODE%.
setlocal EnableDelayedExpansion
if /i not "!CMDCMDLINE:%~nx0=!"=="!CMDCMDLINE!" pause
endlocal
exit /b %EXIT_CODE%
