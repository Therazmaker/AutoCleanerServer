//go:build windows

package main

import (
	"os"
	"syscall"
	"time"
)

func getFileCreationTime(info os.FileInfo) time.Time {
	if sys, ok := info.Sys().(*syscall.Win32FileAttributeData); ok && sys != nil {
		return time.Unix(0, sys.CreationTime.Nanoseconds())
	}
	return info.ModTime()
}
