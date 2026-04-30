package auth

import (
	"github.com/danieljoos/wincred"
)

const targetName = "PCConnect_APIKey"

// SaveAPIKey stores the API key in Windows Credential Manager
func SaveAPIKey(apiKey string) error {
	cred := wincred.NewGenericCredential(targetName)
	cred.CredentialBlob = []byte(apiKey)
	cred.Persist = wincred.PersistLocalMachine
	return cred.Write()
}

// GetAPIKey retrieves the API key from Windows Credential Manager
func GetAPIKey() (string, error) {
	cred, err := wincred.GetGenericCredential(targetName)
	if err != nil {
		return "", err
	}
	return string(cred.CredentialBlob), nil
}

// DeleteAPIKey removes the API key from Windows Credential Manager
func DeleteAPIKey() error {
	cred, err := wincred.GetGenericCredential(targetName)
	if err != nil {
		return err
	}
	return cred.Delete()
}
