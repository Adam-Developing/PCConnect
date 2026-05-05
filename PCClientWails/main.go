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

	wailsApp := application.New(application.Options{
		Name:   "PCConnect",
		Assets: application.AssetOptions{Handler: application.AssetFileServerFS(assets)},
		Bindings: []any{
			applicationInstance,
		},
		OnShutdown: func() {
			applicationInstance.ClearSession()
		},
	})

	mainWindow := wailsApp.Window.NewWithOptions(application.WebviewWindowOptions{
		Title:     "PCConnect",
		Width:     1320,
		Height:    860,
		MinWidth:  1024,
		MinHeight: 720,
		BackgroundColour: application.NewRGB(255, 255, 255),
	})

	// Initialize Tray in a goroutine
	trayManager := tray.NewTrayManager(
		func() {
			mainWindow.Show()
		},
		func() {
			wailsApp.Quit()
		},
		func() {
			applicationInstance.ClearSession()
			wailsApp.Event.Emit("logout")
		},
	)
	go trayManager.Run()
	applicationInstance.Startup(wailsApp)

	err2 := wailsApp.Run()
	if err2 != nil {
		println("Error:", err2.Error())
	}
}
