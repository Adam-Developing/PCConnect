package main

import (
	"embed"

	"github.com/Adam-Developing/PCConnect/PCClientWails/app"
	"github.com/Adam-Developing/PCConnect/PCClientWails/internal/tray"
	"github.com/wailsapp/wails/v3/pkg/application"
)

//go:embed all:frontend/dist
var assets embed.FS

func main() {
	var applicationInstance = app.NewApp()

	app := application.New(application.Options{
		Name:   "PCConnect",
		Assets: application.AssetOptions{Handler: application.AssetFileServerFS(assets)},
		OnShutdown: func() {
			applicationInstance.ClearSession()
		},
	})

	mainWindow := app.NewWebviewWindowWithOptions(application.WebviewWindowOptions{
		Title:     "PCConnect",
		Width:     1320,
		Height:    860,
		MinWidth:  1024,
		MinHeight: 720,
	})

	// Initialize Tray in a goroutine
	trayManager := tray.NewTrayManager(
		func() {
			mainWindow.Show()
		},
		func() {
			application.Get().Quit()
		},
		func() {
			applicationInstance.ClearSession()
			application.Get().Event.Emit("logout", nil)
		},
	)
	go trayManager.Run()
	applicationInstance.Startup(nil)

	err2 := application.Get().Run()
	if err2 != nil {
		println("Error:", err2.Error())
	}
}
