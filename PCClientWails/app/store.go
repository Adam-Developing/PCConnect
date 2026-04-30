package app

import (
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
)

type SessionStore struct {
	path string
}

func NewSessionStore() (*SessionStore, error) {
	configDir, err := os.UserConfigDir()
	if err != nil {
		return nil, err
	}

	basePath := filepath.Join(configDir, "PCClient", "wails")
	if err := os.MkdirAll(basePath, 0o700); err != nil {
		return nil, err
	}

	return &SessionStore{path: filepath.Join(basePath, "session.json")}, nil
}

func (store *SessionStore) Save(session Session) error {
	bytes, err := json.MarshalIndent(session, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(store.path, bytes, 0o600)
}

func (store *SessionStore) Load() (Session, error) {
	bytes, err := os.ReadFile(store.path)
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return Session{}, nil
		}
		return Session{}, err
	}

	var session Session
	if err := json.Unmarshal(bytes, &session); err != nil {
		return Session{}, err
	}

	return session, nil
}

func (store *SessionStore) Clear() error {
	if err := os.Remove(store.path); err != nil && !errors.Is(err, os.ErrNotExist) {
		return err
	}
	return nil
}
