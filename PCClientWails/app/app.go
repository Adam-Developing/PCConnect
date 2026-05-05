package app

import (
	"context"
	"encoding/json"
	"fmt"
	"log"
	"os"
	"strings"

	"git.sr.ht/~jackmordaunt/go-toast/v2"

	"github.com/Adam-Developing/PCConnect/PCClientWails/internal/auth"
	"github.com/Adam-Developing/PCConnect/PCClientWails/internal/cache"
	"github.com/Adam-Developing/PCConnect/PCClientWails/internal/commands"
	"github.com/Adam-Developing/PCConnect/PCClientWails/internal/reminders"
	"github.com/Adam-Developing/PCConnect/PCClientWails/internal/websocket"
	"github.com/wailsapp/wails/v3/pkg/application"
	"golang.org/x/sys/windows/registry"
)

type App struct {
	ctx            context.Context
	wailsApp       *application.App
	store          *SessionStore
	wsManager      *websocket.ClientManager
	scheduler      *reminders.Scheduler
	mainWindow     *application.WebviewWindow
	reminderWindow *application.WebviewWindow
}

func NewApp() *App {
	store, err := NewSessionStore()
	if err != nil {
		panic(err)
	}

	return &App{store: store}
}

func (a *App) Startup(app *application.App) {
	a.wailsApp = app

	// Initialize Toast
	_ = toast.SetAppData(toast.AppData{
		AppID: "PCConnect.App",
	})

	// Load session and start WS if available
	session, err := a.store.Load()
	if err == nil && session.APIKey != "" && session.PCName != "" {
		// Try to get API key from Credential Manager as fallback/override
		secureKey, err := auth.GetAPIKey()
		if err == nil && secureKey != "" {
			session.APIKey = secureKey
		}
		// WebSocket will be started when the frontend signals it's ready via FrontendReady()
	}
}

func (a *App) FrontendReady() {
	session, err := a.store.Load()
	if err == nil && session.APIKey != "" && session.PCName != "" {
		secureKey, err := auth.GetAPIKey()
		if err == nil && secureKey != "" {
			session.APIKey = secureKey
		}
		a.startWebSocket(session)
	}
}

func (a *App) SetWindows(mainWindow *application.WebviewWindow, reminderWindow *application.WebviewWindow) {
	a.mainWindow = mainWindow
	a.reminderWindow = reminderWindow
}

func (a *App) emitToMain(name string, data ...any) {
	if a.wailsApp != nil {
		a.wailsApp.Event.Emit(name, data...)
	}
}

func (a *App) ShowFullscreenReminder(reminder reminders.Reminder) {
	if a.wailsApp == nil {
		return
	}

	s, err := a.GetSession()
	if err != nil {
		log.Printf("Failed to get session for reminder: %v", err)
	}
	_ = s

	options := application.WebviewWindowOptions{
		Title:            "Reminder",
		Width:            1024,
		Height:           768,
		Frameless:        true,
		AlwaysOnTop:      true,
		BackgroundColour: application.NewRGBA(255, 0, 0, 178), // Red with 70% opacity by default
		URL:              "#/reminder", // Special route for reminder
	}

	window := a.wailsApp.Window.NewWithOptions(options)
	if window != nil {
		window.Fullscreen()
		// Wait slightly to ensure window loads React before emitting the payload
		go func() {
			a.wailsApp.Event.Emit("fullscreen_reminder_data", reminder)
		}()
	}
}

func (a *App) startWebSocket(s Session) {
	if a.wsManager != nil {
		a.wsManager.Stop()
	}

	wsURL := s.BaseURL
	wsURL = strings.Replace(wsURL, "/api_node", "", 1)
	wsURL = strings.TrimSuffix(wsURL, "/")

	a.wsManager = websocket.NewClientManager(wsURL, s.APIKey, s.PCName)
	a.wsManager.SetHandlers(
		func(cmd string) {
			a.ExecuteSystemCommand(cmd)
		},
		func(rawReminders interface{}) {
			var items []reminders.Reminder
			dataJSON, _ := json.Marshal(rawReminders)
			if err := json.Unmarshal(dataJSON, &items); err == nil {
				a.SyncReminders(items)
				cache.Save(cache.CacheData{Reminders: items})
				a.emitToMain("reminders_updated", items)
			} else {
				var wrapped struct {
					Reminders []reminders.Reminder `json:"reminders"`
				}
				if err := json.Unmarshal(dataJSON, &wrapped); err == nil {
					a.SyncReminders(wrapped.Reminders)
					cache.Save(cache.CacheData{Reminders: wrapped.Reminders})
					a.emitToMain("reminders_updated", wrapped.Reminders)
				}
			}
		},
		func(payload interface{}) {
			if data, ok := payload.(map[string]interface{}); ok {
				title := "Reminder"
				if t, ok := data["Reminder"].(string); ok {
					title = t
				}
				body := "You have a new reminder"
				if d, ok := data["Date"].(string); ok {
					if t, ok := data["Time"].(string); ok {
						body = fmt.Sprintf("Due: %s %s", d, t)
					}
				}
				a.Notify(title, body)
			}
			a.emitToMain("reminder_notify", payload)
		},
		func(connected bool, mode string) {
			a.emitToMain("connection_status", map[string]interface{}{
				"connected": connected,
				"mode":      mode,
			})
		},
	)

	if a.scheduler == nil {
		a.scheduler = reminders.NewScheduler(s.APIKey, func(r reminders.Reminder) {
			session, _ := a.store.Load()
			// Always show fullscreen if configured
			if session.NotificationStyle == "fullscreen" {
				log.Printf("[App] Showing fullscreen reminder: %d", r.ID)
				a.ShowFullscreenReminder(r)
			} else {
				a.Notify(r.Reminder, fmt.Sprintf("Scheduled for: %s %s", r.Date, r.Time))
				a.emitToMain("reminder_notify", r)
			}
		})
		a.scheduler.Start()
	}

	err := a.wsManager.Start()
	if err != nil {
		fmt.Printf("Failed to start WebSocket: %v\n", err)
	}
}

func (a *App) Notify(title, body string) {
	notification := toast.Notification{
		Title: title,
		Body:  body,
	}
	_ = notification.Push()
}

func (a *App) GetCachedReminders() (interface{}, error) {
	data, err := cache.Load()
	if err != nil {
		return nil, err
	}
	return data.Reminders, nil
}

func (a *App) SyncReminders(items []reminders.Reminder) {
	if a.scheduler != nil {
		a.scheduler.UpdateReminders(items)
	}
}

func (a *App) GetSession() (Session, error) {
	session, err := a.store.Load()
	if err != nil {
		return Session{}, err
	}

	secureKey, err := auth.GetAPIKey()
	if err == nil && secureKey != "" {
		session.APIKey = secureKey
	}

	return session, nil
}

func (a *App) SaveSession(session Session) (Session, error) {
	session.BaseURL = strings.TrimSpace(session.BaseURL)
	session.APIKey = strings.TrimSpace(session.APIKey)
	session.PCName = strings.TrimSpace(session.PCName)

	if session.BaseURL == "" {
		return Session{}, fmt.Errorf("base URL is required")
	}
	if session.APIKey == "" {
		return Session{}, fmt.Errorf("api key is required")
	}
	if session.PCName == "" {
		return Session{}, fmt.Errorf("pc name is required")
	}

	if err := auth.SaveAPIKey(session.APIKey); err != nil {
		fmt.Printf("Failed to save to Credential Manager: %v\n", err)
	}

	if err := a.store.Save(session); err != nil {
		return Session{}, err
	}

	a.startWebSocket(session)

	return session, nil
}

func (a *App) SaveNotificationStyle(style string, bgColor string, textColor string) error {
	session, err := a.store.Load()
	if err != nil {
		return err
	}
	session.NotificationStyle = style
	session.FullscreenBgColor = bgColor
	session.FullscreenTextColor = textColor
	return a.store.Save(session)
}

func (a *App) ClearSession() error {
	if a.wsManager != nil {
		a.wsManager.Stop()
		a.wsManager = nil
	}
	auth.DeleteAPIKey()
	return a.store.Clear()
}

func (a *App) ExecuteSystemCommand(command string) (string, error) {
	return commands.Execute(strings.TrimSpace(command))
}

func (a *App) GetConnectionStatus() ConnectivityState {
	if a.wsManager == nil {
		return ConnectivityState{SocketHealthy: false, Mode: "offline"}
	}
	connected := a.wsManager.IsConnected()
	mode := "degraded"
	if connected {
		mode = "realtime"
	}
	return ConnectivityState{SocketHealthy: connected, Mode: mode}
}

func (a *App) SetAutoStart(enabled bool) error {
	const runKey = `Software\Microsoft\Windows\CurrentVersion\Run`
	const appName = "PCConnect"

	executable, err := os.Executable()
	if err != nil {
		return err
	}

	k, err := registry.OpenKey(registry.CURRENT_USER, runKey, registry.QUERY_VALUE|registry.SET_VALUE)
	if err != nil {
		return err
	}
	defer k.Close()

	if enabled {
		return k.SetStringValue(appName, executable)
	} else {
		return k.DeleteValue(appName)
	}
}

func (a *App) IsAutoStartEnabled() bool {
	const runKey = `Software\Microsoft\Windows\CurrentVersion\Run`
	const appName = "PCConnect"

	k, err := registry.OpenKey(registry.CURRENT_USER, runKey, registry.QUERY_VALUE)
	if err != nil {
		return false
	}
	defer k.Close()

	_, _, err = k.GetStringValue(appName)
	return err == nil
}
