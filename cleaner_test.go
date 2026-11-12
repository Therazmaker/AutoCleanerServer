package main

import (
	"os"
	"path/filepath"
	"testing"
	"time"
)

func TestMonthlyPrevMonthRuleAndRetention(t *testing.T) {
	tmpDir := t.TempDir()
	now := time.Date(2026, 11, 1, 2, 0, 0, 0, time.Local) // 1st of November

	// Create files in October 2026
	files := []struct {
		name string
		day  int
	}{
		{"apertura_oct_01.bak", 1},
		{"diario_oct_05.bak", 5},
		{"diario_oct_12.bak", 12},
		{"reporte_IMPORTANTE_oct_15.xlsx", 15},
		{"diario_oct_20.bak", 20},
		{"cierre_oct_31.bak", 31},
	}

	for _, f := range files {
		p := filepath.Join(tmpDir, f.name)
		if err := os.WriteFile(p, []byte("test data"), 0644); err != nil {
			t.Fatal(err)
		}
		ft := time.Date(2026, 10, f.day, 12, 0, 0, 0, time.Local)
		if err := os.Chtimes(p, ft, ft); err != nil {
			t.Fatal(err)
		}
	}

	task := DeleteTask{
		ID:               "test_1",
		Name:             "Test Primero de Mes",
		FolderPath:       tmpDir,
		Enabled:          true,
		ScheduleType:     "monthly",
		DayOfMonth:       1,
		RunTime:          "02:00",
		DateField:        "modified",
		DateRuleType:     "prev_month",
		PrevMonthDayFrom: 1,
		PrevMonthDayTo:   31,
		KeepNamePatterns: "*IMPORTANTE*",
		KeepFirstOfMonth: true,
		KeepLastOfMonth:  true,
	}

	// 1. Dry Run first
	dryRep := EvaluateOrExecuteTask(task, true, now)
	if dryRep.FilesToDelete != 3 {
		t.Fatalf("expected 3 files to delete in dry run, got %d", dryRep.FilesToDelete)
	}
	if dryRep.FilesKept != 3 {
		t.Fatalf("expected 3 files kept in dry run (day 1, day 31, and IMPORTANTE), got %d", dryRep.FilesKept)
	}

	// 2. Real Execution
	execRep := EvaluateOrExecuteTask(task, false, now)
	if execRep.FilesToDelete != 3 {
		t.Fatalf("expected 3 files deleted, got %d", execRep.FilesToDelete)
	}

	// Verify kept files still exist on disk
	keptNames := []string{"apertura_oct_01.bak", "reporte_IMPORTANTE_oct_15.xlsx", "cierre_oct_31.bak"}
	for _, kn := range keptNames {
		if _, err := os.Stat(filepath.Join(tmpDir, kn)); err != nil {
			t.Errorf("expected kept file %s to remain on disk, got err: %v", kn, err)
		}
	}

	// Verify deleted files are gone
	deletedNames := []string{"diario_oct_05.bak", "diario_oct_12.bak", "diario_oct_20.bak"}
	for _, dn := range deletedNames {
		if _, err := os.Stat(filepath.Join(tmpDir, dn)); !os.IsNotExist(err) {
			t.Errorf("expected file %s to be deleted", dn)
		}
	}
}
