package tray

import (
	"github.com/getlantern/systray"
)

type TrayManager struct {
	onShow   func()
	onQuit   func()
	onLogout func()
}

func NewTrayManager(onShow, onQuit, onLogout func()) *TrayManager {
	return &TrayManager{
		onShow:   onShow,
		onQuit:   onQuit,
		onLogout: onLogout,
	}
}

func (m *TrayManager) Run() {
	systray.Run(m.onReady, m.onExit)
}

func (m *TrayManager) onReady() {
	systray.SetTitle("PCConnect")
	systray.SetTooltip("PCConnect - Remote Command & Reminders")

	mShow := systray.AddMenuItem("Open Control Panel", "Show the main window")
	mLogout := systray.AddMenuItem("Logout", "Logout from the application")
	systray.AddSeparator()
	mQuit := systray.AddMenuItem("Exit", "Quit the application")

	go func() {
		for {
			select {
			case <-mShow.ClickedCh:
				if m.onShow != nil {
					m.onShow()
				}
			case <-mLogout.ClickedCh:
				if m.onLogout != nil {
					m.onLogout()
				}
			case <-mQuit.ClickedCh:
				if m.onQuit != nil {
					m.onQuit()
				}
				systray.Quit()
			}
		}
	}()
}

func (m *TrayManager) onExit() {
	// Cleanup if needed
}
