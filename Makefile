.PHONY: help backend windows mobile

help:
	@echo PCConnect development launchers
	@echo.
	@echo   make backend  - build and run PostgreSQL, Valkey, API, and worker
	@echo   make windows  - build and run the Windows agent and companion
	@echo   make mobile   - build, install, and run the Android app

backend:
	powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\make-and-run-backend.ps1"

windows:
	powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\make-and-run-windows.ps1"

mobile:
	powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\make-and-run-mobile.ps1"
