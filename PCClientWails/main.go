package main

import (
	"embed"
	"log"

	"github.com/Adam-Developing/PCConnect/PCClientWails/app"
	"github.com/Adam-Developing/PCConnect/PCClientWails/internal/tray"
	"github.com/wailsapp/wails/v3/pkg/application"
	"github.com/wailsapp/wails/v3/pkg/events"
)

//go:embed all:frontend/dist
var assets embed.FS

func main() {
	applicationInstance := app.NewApp()

	wailsApp := application.New(application.Options{
		Name:        "PCConnect",
		Description: "PCConnect",
		Assets: application.AssetOptions{
			Handler: application.BundledAssetFileServer(assets),
		},
		Services: []application.Service{
			application.NewService(applicationInstance),
		},
		OnShutdown: func() {
			applicationInstance.ClearSession()
		},
	})

	mainWindow := wailsApp.Window.NewWithOptions(application.WebviewWindowOptions{
		Name:      "main",
		Title:     "PCConnect",
		Width:     1320,
		Height:    860,
		MinWidth:  1024,
		MinHeight: 720,
		URL:       "/",
	})

	reminderWindow := wailsApp.Window.NewWithOptions(application.WebviewWindowOptions{
		Name:            "fullscreen-reminder",
		Title:           "PCConnect Reminder",
		URL:             "/",
		AlwaysOnTop:     true,
		Frameless:       true,
		DisableResize:   true,
		StartState:      application.WindowStateFullscreen,
		BackgroundType:  application.BackgroundTypeTransparent,
		BackgroundColour: application.NewRGBA(0, 0, 0, 0),
		Hidden:          true,
		Windows: application.WindowsWindow{
			HiddenOnTaskbar: true,
		},
	})

	applicationInstance.SetRuntime(wailsApp, reminderWindow)

	wailsApp.Event.OnApplicationEvent(events.Common.ApplicationStarted, func(event *application.ApplicationEvent) {
		applicationInstance.Startup(wailsApp.Context())

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
	})

	if err := wailsApp.Run(); err != nil {
		log.Fatal(err)
	}
}
