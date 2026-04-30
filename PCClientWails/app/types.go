package app

type Session struct {
	BaseURL             string `json:"baseUrl"`
	APIKey              string `json:"apiKey"`
	PCName              string `json:"pcName"`
	NotificationStyle   string `json:"notificationStyle"`
	FullscreenBgColor   string `json:"fullscreenBgColor"`
	FullscreenTextColor string `json:"fullscreenTextColor"`
}

type ConnectivityState struct {
	SocketHealthy bool   `json:"socketHealthy"`
	Mode          string `json:"mode"`
}
