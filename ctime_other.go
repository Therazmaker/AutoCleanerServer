//go:build !windows

package main

import (
	"os"
	"time"
)

func getFileCreationTime(info os.FileInfo) time.Time {
	return info.ModTime()
}
