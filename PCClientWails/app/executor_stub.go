//go:build !windows

package app

import "fmt"

func executeCommand(command string) (string, error) {
	return "", fmt.Errorf("unsupported OS for command execution: %s", command)
}
