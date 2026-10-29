@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0make-and-run-backend.ps1" %*
