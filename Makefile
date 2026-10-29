.PHONY: help backend windows mobile

help:
	@echo PCConnect development launchers
	@echo.
	@echo   make backend  - build, migrate, and run PostgreSQL, Valkey, API, and worker
	@echo   make windows  - build and run the Windows agent and companion
	@echo   make mobile   - build, install, and run the Android app (defaults to physical device)

backend:
	.\make-and-run-backend.cmd $(ARGS)

windows:
	.\make-and-run-windows.cmd $(ARGS)

mobile:
	.\make-and-run-mobile.cmd $(ARGS)
