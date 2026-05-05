package main

import (
	"context"
	"embed"

	"github.com/Adam-Developing/PCConnect/PCClientWails/app"
	"github.com/Adam-Developing/PCConnect/PCClientWails/internal/tray"
	"github.com/wailsapp/wails/v2"
	"github.com/wailsapp/wails/v2/pkg/options"
	"github.com/wailsapp/wails/v2/pkg/options/assetserver"
	"github.com/wailsapp/wails/v2/pkg/runtime"
)

//go:embed all:frontend/dist
var assets embed.FS

func main() {
	applicationInstance := app.NewApp()

	err := wails.Run(&options.App{
		Title:     "PCConnect",
		Width:     1320,
		Height:    860,
		MinWidth:  1024,
		MinHeight: 720,
		AssetServer: &assetserver.Options{
			Assets: assets,
		},
		OnStartup: func(ctx context.Context) {
			applicationInstance.Startup(ctx)

			trayManager := tray.NewTrayManager(
				func() {
					runtime.WindowShow(ctx)
				},
				func() {
					runtime.Quit(ctx)
				},
				func() {
					applicationInstance.ClearSession()
					runtime.EventsEmit(ctx, "logout")
				},
			)
			go trayManager.Run()
		},
		OnShutdown: func(ctx context.Context) {
			applicationInstance.ClearSession()
		},
		Bind: []interface{}{applicationInstance},
	})
	if err != nil {
		println("Error:", err.Error())
	}
}
