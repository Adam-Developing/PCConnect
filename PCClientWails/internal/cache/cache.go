package cache

import (
	"encoding/json"
	"os"
	"path/filepath"
)

type CacheData struct {
	Reminders interface{} `json:"reminders"`
}

func GetCachePath() (string, error) {
	configDir, err := os.UserConfigDir()
	if err != nil {
		return "", err
	}
	basePath := filepath.Join(configDir, "PCConnect")
	if err := os.MkdirAll(basePath, 0700); err != nil {
		return "", err
	}
	return filepath.Join(basePath, "cache.json"), nil
}

func Save(data CacheData) error {
	path, err := GetCachePath()
	if err != nil {
		return err
	}
	bytes, err := json.MarshalIndent(data, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(path, bytes, 0600)
}

func Load() (CacheData, error) {
	path, err := GetCachePath()
	if err != nil {
		return CacheData{}, err
	}
	bytes, err := os.ReadFile(path)
	if err != nil {
		return CacheData{}, err
	}
	var data CacheData
	if err := json.Unmarshal(bytes, &data); err != nil {
		return CacheData{}, err
	}
	return data, nil
}
