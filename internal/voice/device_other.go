//go:build !windows

package voice

import "errors"

// OpenDevice is only implemented on Windows, where the game runs.
func OpenDevice() (Device, error) { return nil, errors.New("voice: no audio backend on this OS") }
