package reminders

import (
	"crypto/aes"
	"crypto/cipher"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"log"
	"strings"
	"sync"
	"time"

	"github.com/robfig/cron/v3"
)

// Reminder represents a reminder record
type Reminder struct {
	ID        int    `json:"ID"`
	Username  int    `json:"Username"` // UserID in DB
	Date      string `json:"Date"`     // DD/MM/YY
	Time      string `json:"Time"`     // HH:MM:SS
	Reminder  string `json:"Reminder"`
	Completed int    `json:"Completed"`
}

type Scheduler struct {
	cron        *cron.Cron
	apiKey      string
	onNotify    func(r Reminder)
	mu          sync.RWMutex
	reminders   []Reminder
	scheduled   map[int]cron.EntryID
	pastAlerted map[int]bool
}

func NewScheduler(apiKey string, onNotify func(Reminder)) *Scheduler {
	return &Scheduler{
		cron:        cron.New(cron.WithSeconds()),
		apiKey:      apiKey,
		onNotify:    onNotify,
		scheduled:   make(map[int]cron.EntryID),
		pastAlerted: make(map[int]bool),
	}
}

func (s *Scheduler) Start() {
	s.cron.Start()
}

func (s *Scheduler) Stop() {
	s.cron.Stop()
}

func (s *Scheduler) UpdateReminders(raw interface{}) []Reminder {
	s.mu.Lock()
	defer s.mu.Unlock()

	var newReminders []Reminder
	bytes, _ := json.Marshal(raw)

	// Try unmarshaling as a slice first
	if err := json.Unmarshal(bytes, &newReminders); err != nil {
		// If that fails, try unmarshaling as an object with a "reminders" field
		var wrapped struct {
			Reminders []Reminder `json:"reminders"`
		}
		if err2 := json.Unmarshal(bytes, &wrapped); err2 != nil {
			log.Printf("Error unmarshaling reminders: %v (as slice) and %v (as object)", err, err2)
			return s.reminders
		}
		newReminders = wrapped.Reminders
	}

	// Decrypt reminders
	for i := range newReminders {
		dec, err := DecryptReminder(newReminders[i].Reminder, s.apiKey)
		if err == nil {
			newReminders[i].Reminder = dec
		} else {
			log.Printf("Failed to decrypt reminder %d: %v", newReminders[i].ID, err)
		}
	}

	// Clear old schedules
	for id, entryID := range s.scheduled {
		s.cron.Remove(entryID)
		delete(s.scheduled, id)
	}

	s.reminders = newReminders

	for _, r := range s.reminders {
		if r.Completed != 0 {

			delete(s.pastAlerted, r.ID)
			continue
		}

		// Parse date/time
		// Date: DD/MM/YY
		// Time: HH:MM:SS
		reminderTime, err := time.ParseInLocation("02/01/06 15:04:05", r.Date+" "+r.Time, time.Local)
		if err != nil {
			log.Printf("Error parsing reminder time %d: %v", r.ID, err)

			delete(s.pastAlerted, r.ID)
			continue
		}

		now := time.Now()
		log.Printf("[Scheduler] Checking reminder %d: %s %s (Now: %v, Target: %v)", r.ID, r.Date, r.Time, now.Format("15:04:05"), reminderTime.Format("15:04:05"))

		if reminderTime.After(now) {
			rCopy := r // Capture for closure
			entryID, err := s.cron.AddFunc(fmt.Sprintf("%d %d %d %d %d *",
				reminderTime.Second(),
				reminderTime.Minute(),
				reminderTime.Hour(),
				reminderTime.Day(),
				reminderTime.Month()), func() {
				if s.onNotify != nil {
					s.onNotify(rCopy)
				}
			})
			if err == nil {
				s.scheduled[r.ID] = entryID
			}
		} else {
			// Reminder is past due and not completed
			if !s.pastAlerted[r.ID] {
				log.Printf("[Scheduler] Reminder %d is PAST DUE. Triggering immediate notification.", r.ID)
				s.pastAlerted[r.ID] = true
				rCopy := r
				if s.onNotify != nil {
					// Execute in background to avoid blocking
					go s.onNotify(rCopy)
				}
			} else {
				log.Printf("[Scheduler] Reminder %d is past due but already alerted.", r.ID)
			}
		}
	}

	return s.reminders
}

// DecryptReminder decrypts the reminder text using the API key
func DecryptReminder(dataB64 string, apiKey string) (string, error) {
	if len(apiKey) != 32 {
		return "", fmt.Errorf("invalid API key length: expected 32, got %d", len(apiKey))
	}

	rawData, err := base64.StdEncoding.DecodeString(dataB64)
	if err != nil {
		return "", err
	}

	if len(rawData) < 16 {
		return "", errors.New("ciphertext too short")
	}

	iv := rawData[:16]
	ciphertext := rawData[16:]

	block, err := aes.NewCipher([]byte(apiKey))
	if err != nil {
		return "", err
	}

	mode := cipher.NewCBCDecrypter(block, iv)
	decrypted := make([]byte, len(ciphertext))
	mode.CryptBlocks(decrypted, ciphertext)

	// Remove PKCS7 padding
	padding := int(decrypted[len(decrypted)-1])
	if padding < 1 || padding > aes.BlockSize {
		// Try to handle no padding or different format gracefully
		return strings.TrimSpace(string(decrypted)), nil
	}
	// Verify padding
	if padding > len(decrypted) {
		return strings.TrimSpace(string(decrypted)), nil
	}

	return string(decrypted[:len(decrypted)-padding]), nil
}

// EncryptReminder encrypts the reminder text using the API key
func EncryptReminder(plainText string, apiKey string, iv []byte) (string, error) {
	if len(apiKey) != 32 {
		return "", fmt.Errorf("invalid API key length: expected 32, got %d", len(apiKey))
	}

	block, err := aes.NewCipher([]byte(apiKey))
	if err != nil {
		return "", err
	}

	// PKCS7 padding
	padding := aes.BlockSize - (len(plainText) % aes.BlockSize)
	padText := append([]byte(plainText), make([]byte, padding)...)
	for i := len(plainText); i < len(padText); i++ {
		padText[i] = byte(padding)
	}

	ciphertext := make([]byte, len(padText))
	mode := cipher.NewCBCEncrypter(block, iv)
	mode.CryptBlocks(ciphertext, padText)

	final := append(iv, ciphertext...)
	return base64.StdEncoding.EncodeToString(final), nil
}
