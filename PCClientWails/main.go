package main

import (
	"embed"

	"github.com/Adam-Developing/PCConnect/PCClientWails/app"
	"github.com/Adam-Developing/PCConnect/PCClientWails/internal/tray"
	"github.com/wailsapp/wails/v3/pkg/application"
)

//go:embed all:frontend/dist
var assets embed.FS

const reminderWindowAlpha70 = 179

func main() {
	var backendApp = app.NewApp()

	wailsApp := application.New(application.Options{
		Name:   "PCConnect",
		Assets: application.AssetOptions{Handler: application.AssetFileServerFS(assets)},
		Services: []application.Service{
			application.NewService(backendApp),
		},
		OnShutdown: func() {
			backendApp.ClearSession()
		},
	})

	mainWindow := wailsApp.Window.NewWithOptions(application.WebviewWindowOptions{
		Title:     "PCConnect",
		Width:     1320,
		Height:    860,
		MinWidth:  1024,
		MinHeight: 720,
	})

	reminderWindow := wailsApp.Window.NewWithOptions(application.WebviewWindowOptions{
		Name:             "reminder",
		Title:            "Reminder",
		AlwaysOnTop:      true,
		DisableResize:    true,
		Frameless:        true,
		StartState:       application.WindowStateFullscreen,
		BackgroundType:   application.BackgroundTypeSolid,
		BackgroundColour: application.NewRGBA(255, 0, 0, reminderWindowAlpha70),
		URL:              "/?window=reminder",
		Hidden:           true,
	})

	backendApp.SetWindows(mainWindow, reminderWindow)

	// Initialize Tray in a goroutine
	trayManager := tray.NewTrayManager(
		func() {
			mainWindow.Show()
		},
		func() {
			application.Get().Quit()
		},
		func() {
			backendApp.ClearSession()
			mainWindow.EmitEvent("logout")
		},
	)
	go trayManager.Run()

	err2 := wailsApp.Run()
	if err2 != nil {
		println("Error:", err2.Error())
	}
}
