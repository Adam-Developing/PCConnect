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

	appOptions := application.Options{
		Name:        "PCConnect",
		Description: "PCConnect Wails App",
		Services: []application.Service{
			application.NewService(applicationInstance),
		},
		Assets: application.AssetOptions{
			Handler: application.AssetFileServerFS(assets),
		},
	}

	wailsApp := application.New(appOptions)

	wailsApp.Event.OnApplicationEvent(events.Common.ApplicationStarted, func(event *application.ApplicationEvent) {
		applicationInstance.Startup(wailsApp)

		trayManager := tray.NewTrayManager(
			func() {
				// Show all windows when tray icon is clicked
				// Let's just create a new window if it doesn't exist or show existing.
				// wails v3 App.Show() is available
				wailsApp.Show()
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

	wailsApp.OnShutdown(func() {
		applicationInstance.ClearSession()
	})

	application.NewWindow(application.WebviewWindowOptions{
		Title:     "PCConnect",
		Width:     1320,
		Height:    860,
		MinWidth:  1024,
		MinHeight: 720,
		URL:       "/",
	})

	err := wailsApp.Run()
	if err != nil {
		log.Fatal(err)
	}
}
