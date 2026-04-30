package commands

import (
	"fmt"
	"os/exec"
	"strings"
)

// Execute handles system commands like Shutdown, Restart, Lock, etc.
func Execute(command string) (string, error) {
	allowed := map[string][]string{
		"Shutdown":  {"shutdown", "/s", "/f", "/t", "10"},
		"Restart":   {"shutdown", "/r", "/f", "/t", "10"},
		"Signout":   {"shutdown", "/l"},
		"Lock":      {"rundll32.exe", "user32.dll,LockWorkStation"},
		"Sleep":     {"rundll32.exe", "powrprof.dll,SetSuspendState", "0,1,0"},
		"Hibernate": {"rundll32.exe", "powrprof.dll,SetSuspendState", "Hibernate"},
	}

	// Case insensitive match
	var invocation []string
	var found bool
	for name, args := range allowed {
		if strings.EqualFold(name, command) {
			invocation = args
			found = true
			break
		}
	}

	if !found {
		return "", fmt.Errorf("command not allowed: %s", command)
	}

	process := exec.Command(invocation[0], invocation[1:]...)
	if err := process.Start(); err != nil {
		return "", err
	}

	return "accepted", nil
}
