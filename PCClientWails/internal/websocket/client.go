package websocket

import (
	"bytes"
	"context"
	"encoding/json"
	"log"
	"net/http"
	"strings"
	"sync"
	"time"

	"github.com/gorilla/websocket"
)

type ClientManager struct {
	url         string
	apiKey      string
	pcName      string
	connected   bool
	mu          sync.RWMutex
	conn        *websocket.Conn
	cancel      context.CancelFunc
	onCommand   func(command string)
	onReminders func(reminders interface{})
	onNotify    func(payload interface{})
	onStatus    func(connected bool, mode string)
}

func NewClientManager(url, apiKey, pcName string) *ClientManager {
	return &ClientManager{
		url:    url,
		apiKey: apiKey,
		pcName: pcName,
	}
}

func (m *ClientManager) SetHandlers(onCommand func(string), onReminders func(interface{}), onNotify func(interface{}), onStatus func(bool, string)) {
	m.onCommand = onCommand
	m.onReminders = onReminders
	m.onNotify = onNotify
	m.onStatus = onStatus
}

func (m *ClientManager) Start() error {
	ctx, cancel := context.WithCancel(context.Background())
	m.cancel = cancel

	go m.connectLoop(ctx)
	return nil
}

func (m *ClientManager) connectLoop(ctx context.Context) {
	wsURL := strings.Replace(m.url, "http://", "ws://", 1)
	wsURL = strings.Replace(wsURL, "https://", "wss://", 1)
	wsURL = strings.TrimSuffix(wsURL, "/") + "/socket.io/?EIO=4&transport=websocket"

	for {
		select {
		case <-ctx.Done():
			return
		default:
		}

		// SECURE COOKIE ROLE-BASED AUTH
		authHeader := http.Header{}
		authRespUrl := strings.TrimSuffix(m.url, "/") + "/v1/auth/session"
		reqBody, _ := json.Marshal(map[string]string{"apiKey": m.apiKey, "pcName": m.pcName, "clientType": "desktop"})
		if res, err := http.Post(authRespUrl, "application/json", bytes.NewBuffer(reqBody)); err == nil {
			for _, cookie := range res.Cookies() {
				if cookie.Name == "auth_token" {
					authHeader.Add("Cookie", cookie.String())
				}
			}
			res.Body.Close()
		}

		log.Printf("Connecting to Socket.IO at %s...", wsURL)
		conn, _, err := websocket.DefaultDialer.DialContext(ctx, wsURL, authHeader)
		if err != nil {
			log.Printf("Socket.IO connection error: %v", err)
			if m.onStatus != nil {
				m.onStatus(false, "degraded")
			}

			// Wait before reconnecting, cancelable
			select {
			case <-time.After(5 * time.Second):
			case <-ctx.Done():
				return
			}
			continue
		}
		m.readLoop(ctx, conn)

		m.mu.Lock()
		m.connected = false
		m.conn = nil
		m.mu.Unlock()

		if m.onStatus != nil {
			m.onStatus(false, "degraded")
		}

		// Wait before reconnecting, cancelable
		select {
		case <-time.After(2 * time.Second):
		case <-ctx.Done():
			return
		}
	}
}

func (m *ClientManager) readLoop(ctx context.Context, conn *websocket.Conn) {
	defer conn.Close()

	for {
		select {
		case <-ctx.Done():
			return
		default:
		}

		_, message, err := conn.ReadMessage()
		if err != nil {
			log.Printf("Socket.IO read error: %v", err)
			return
		}

		msg := string(message)
		if len(msg) == 0 {
			continue
		}

		// Engine.IO Ping (2) -> Pong (3)
		if msg == "2" || msg == "2probe" {
			conn.WriteMessage(websocket.TextMessage, []byte(strings.Replace(msg, "2", "3", 1)))
			continue
		}

		// Engine.IO Connect (0)
		if strings.HasPrefix(msg, "0") {
			// Send Socket.IO Connect (40)
			conn.WriteMessage(websocket.TextMessage, []byte("40"))
			continue
		}

		// Socket.IO Connect (40)
		if strings.HasPrefix(msg, "40") {
			log.Printf("Socket.IO connected. Sending authentication for PC: %s", m.pcName)
			// Authenticate
			authPayload := map[string]string{
				"apiKey": m.apiKey,
				"pcName": m.pcName,
			}
			authJSON, _ := json.Marshal(authPayload)
			// Send Event: 42["authenticate", {...}]
			conn.WriteMessage(websocket.TextMessage, []byte(`42["authenticate",`+string(authJSON)+`]`))
			continue
		}

		// Socket.IO Event (42)
		if strings.HasPrefix(msg, "42") {
			// Parse the array
			data := msg[2:]
			var event []json.RawMessage
			if err := json.Unmarshal([]byte(data), &event); err == nil && len(event) >= 2 {
				var eventName string
				json.Unmarshal(event[0], &eventName)

				switch eventName {
				case "authenticated":
					log.Printf("Socket.IO authenticated successfully.")
					m.mu.Lock()
					m.connected = true
					m.mu.Unlock()
					if m.onStatus != nil {
						m.onStatus(true, "realtime")
					}
				case "auth_error":
					var errData map[string]interface{}
					json.Unmarshal(event[1], &errData)
					log.Printf("Socket.IO authentication error: %v", errData)
					m.mu.Lock()
					m.connected = false
					m.mu.Unlock()
					if m.onStatus != nil {
						m.onStatus(false, "degraded")
					}
				case "execute_command":
					var payload struct {
						Command string `json:"command"`
					}
					if err := json.Unmarshal(event[1], &payload); err == nil && m.onCommand != nil {
						m.onCommand(payload.Command)
					}
				case "reminders_initial", "reminder_update":
					var payload interface{}
					if err := json.Unmarshal(event[1], &payload); err == nil && m.onReminders != nil {
						m.onReminders(payload)
					}
				case "reminder_notify":
					var payload interface{}
					if err := json.Unmarshal(event[1], &payload); err == nil && m.onNotify != nil {
						m.onNotify(payload)
					}
				}
			}
		}
	}
}

func (m *ClientManager) Stop() {
	if m.cancel != nil {
		m.cancel()
	}
	m.mu.Lock()
	if m.conn != nil {
		m.conn.Close()
	}
	m.mu.Unlock()
}

func (m *ClientManager) IsConnected() bool {
	m.mu.RLock()
	defer m.mu.RUnlock()
	return m.connected
}
