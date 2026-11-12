package main

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"
)

// DeleteTask defines a scheduled or manual folder cleanup rule.
type DeleteTask struct {
	ID          string `json:"id"`
	Name        string `json:"name"`
	Description string `json:"description"`
	FolderPath  string `json:"folder_path"`
	Enabled     bool   `json:"enabled"`

	// Schedule configuration
	ScheduleType string `json:"schedule_type"` // "monthly", "weekly", "daily", "once", "manual"
	DayOfMonth   int    `json:"day_of_month"`  // 1..31 (or 0 for last day of month)
	DayOfWeek    int    `json:"day_of_week"`   // 0=Sun .. 6=Sat
	RunTime      string `json:"run_time"`      // "HH:MM" (24h, e.g., "02:00")
	OnceDate     string `json:"once_date"`     // "YYYY-MM-DD" if ScheduleType == "once"
	SyncWinTask  bool   `json:"sync_win_task"` // Register in Windows Task Scheduler (schtasks)

	// Date selection rule (Which files fall in the deletion target window?)
	DateField        string `json:"date_field"`          // "modified" (default) or "created"
	DateRuleType     string `json:"date_rule_type"`      // "prev_month", "date_range", "older_than_days", "current_month_days", "all_dates"
	DateFrom         string `json:"date_from"`           // "YYYY-MM-DD" for "date_range"
	DateTo           string `json:"date_to"`             // "YYYY-MM-DD" for "date_range"
	PrevMonthDayFrom int    `json:"prev_month_day_from"` // e.g., 1 (start day of previous month)
	PrevMonthDayTo   int    `json:"prev_month_day_to"`   // e.g., 31 (end day of previous month)
	OlderThanDays    int    `json:"older_than_days"`     // e.g., 30 (delete files older than X days)

	// Target file inclusion filters (Which files to target?)
	IncludeSubfolders  bool   `json:"include_subfolders"`
	RemoveEmptyFolders bool   `json:"remove_empty_folders"`
	TargetExtensions   string `json:"target_extensions"`    // CSV e.g. ".log, .bak, .tmp" (empty = all)
	TargetNamePatterns string `json:"target_name_patterns"` // CSV e.g. "backup_*, temp_*" (empty = all)

	// Retention / Exclusion rules ("Y solo deja tal...")
	KeepNamePatterns   string `json:"keep_name_patterns"`   // CSV e.g. "*cierre*, importante.txt, *_mensual.*"
	KeepExtensions     string `json:"keep_extensions"`      // CSV e.g. ".xlsx, .pdf"
	KeepDateFrom       string `json:"keep_date_from"`       // "YYYY-MM-DD" keep files from this date...
	KeepDateTo         string `json:"keep_date_to"`         // "YYYY-MM-DD" ...to this date
	KeepDaysOfMonth    string `json:"keep_days_of_month"`   // CSV e.g. "1, 15, 30" (keep files dated on these days of any month)
	KeepFirstOfMonth   bool   `json:"keep_first_of_month"`  // Keep files from the 1st of the month
	KeepLastOfMonth    bool   `json:"keep_last_of_month"`   // Keep files from the last day of the month
	KeepNewestCount    int    `json:"keep_newest_count"`    // Always keep the N newest files in the folder
	KeepRecentDays     int    `json:"keep_recent_days"`     // Never delete files newer than X days

	// Metadata
	CreatedAt     string `json:"created_at"`
	LastRunAt     string `json:"last_run_at"`
	LastRunStatus string `json:"last_run_status"`
}

// FileDecision describes what happened (or will happen in Dry Run) to a single file.
type FileDecision struct {
	Path         string `json:"path"`
	RelPath      string `json:"rel_path"`
	Name         string `json:"name"`
	Size         int64  `json:"size"`
	FileDate     string `json:"file_date"`
	FileDateTime string `json:"file_datetime"`
	Action       string `json:"action"` // "DELETE", "KEEP", "ERROR", "DELETED"
	Reason       string `json:"reason"`
}

// ExecutionReport stores the result of a dry-run simulation or real task execution.
type ExecutionReport struct {
	ID               string         `json:"id"`
	TaskID           string         `json:"task_id"`
	TaskName         string         `json:"task_name"`
	FolderPath       string         `json:"folder_path"`
	DryRun           bool           `json:"dry_run"`
	ExecutedAt       string         `json:"executed_at"`
	EffectiveWindow  string         `json:"effective_window"`
	TotalFilesScanned int           `json:"total_files_scanned"`
	FilesToDelete    int            `json:"files_to_delete"`
	FilesKept        int            `json:"files_kept"`
	ErrorsCount      int            `json:"errors_count"`
	BytesFreed       int64          `json:"bytes_freed"`
	Status           string         `json:"status"` // "SUCCESS", "PARTIAL", "ERROR", "DRY_RUN"
	Message          string         `json:"message"`
	Decisions        []FileDecision `json:"decisions"`
}

// AppStore manages tasks and execution history on disk.
type AppStore struct {
	mu          sync.Mutex
	DataDir     string
	TasksFile   string
	HistoryFile string
	LogsDir     string
	Tasks       []DeleteTask
	History     []ExecutionReport
}

func NewAppStore(baseDir string) (*AppStore, error) {
	dataDir := filepath.Join(baseDir, "data")
	logsDir := filepath.Join(baseDir, "logs")
	if err := os.MkdirAll(dataDir, 0755); err != nil {
		return nil, err
	}
	if err := os.MkdirAll(logsDir, 0755); err != nil {
		return nil, err
	}

	s := &AppStore{
		DataDir:     dataDir,
		TasksFile:   filepath.Join(dataDir, "tasks.json"),
		HistoryFile: filepath.Join(dataDir, "history.json"),
		LogsDir:     logsDir,
		Tasks:       []DeleteTask{},
		History:     []ExecutionReport{},
	}
	s.Load()
	return s, nil
}

func (s *AppStore) Load() {
	s.mu.Lock()
	defer s.mu.Unlock()

	if raw, err := os.ReadFile(s.TasksFile); err == nil {
		_ = json.Unmarshal(raw, &s.Tasks)
	}
	if raw, err := os.ReadFile(s.HistoryFile); err == nil {
		_ = json.Unmarshal(raw, &s.History)
	}
}

func (s *AppStore) SaveTasks() error {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.saveTasksLocked()
}

func (s *AppStore) saveTasksLocked() error {
	raw, err := json.MarshalIndent(s.Tasks, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(s.TasksFile, raw, 0644)
}

func (s *AppStore) AddHistory(rep ExecutionReport) {
	s.mu.Lock()
	defer s.mu.Unlock()

	// Write detailed log file in logs/
	modeTag := "EXEC"
	if rep.DryRun {
		modeTag = "SIMULACION"
	}
	logFileName := fmt.Sprintf("%s_%s_%s.log", time.Now().Format("2006-01-02_150405"), modeTag, sanitizeName(rep.TaskName))
	logFilePath := filepath.Join(s.LogsDir, logFileName)

	var sb strings.Builder
	sb.WriteString(fmt.Sprintf("====================================================================\n"))
	sb.WriteString(fmt.Sprintf("AutoCleaner Server - Reporte de %s\n", modeTag))
	sb.WriteString(fmt.Sprintf("Tarea:          %s (%s)\n", rep.TaskName, rep.TaskID))
	sb.WriteString(fmt.Sprintf("Carpeta:        %s\n", rep.FolderPath))
	sb.WriteString(fmt.Sprintf("Fecha Ejec.:    %s\n", rep.ExecutedAt))
	sb.WriteString(fmt.Sprintf("Ventana Fechas: %s\n", rep.EffectiveWindow))
	sb.WriteString(fmt.Sprintf("Escaneados:     %d | A Borrar/Borrados: %d | Conservados: %d | Errores: %d\n",
		rep.TotalFilesScanned, rep.FilesToDelete, rep.FilesKept, rep.ErrorsCount))
	sb.WriteString(fmt.Sprintf("Espacio:        %s (%d bytes)\n", formatBytes(rep.BytesFreed), rep.BytesFreed))
	sb.WriteString(fmt.Sprintf("====================================================================\n\n"))

	for _, d := range rep.Decisions {
		sb.WriteString(fmt.Sprintf("[%-7s] %-19s | %10s | %-35s | %s\n",
			d.Action, d.FileDateTime, formatBytes(d.Size), d.RelPath, d.Reason))
	}
	_ = os.WriteFile(logFilePath, []byte(sb.String()), 0644)

	// Keep up to 100 history records in history.json (cap Decisions to 250 items per stored JSON entry)
	storedRep := rep
	if len(storedRep.Decisions) > 250 {
		storedRep.Decisions = storedRep.Decisions[:250]
	}
	s.History = append([]ExecutionReport{storedRep}, s.History...)
	if len(s.History) > 100 {
		s.History = s.History[:100]
	}
	if raw, err := json.MarshalIndent(s.History, "", "  "); err == nil {
		_ = os.WriteFile(s.HistoryFile, raw, 0644)
	}
}

// EvaluateOrExecuteTask scans the folder, applies all date/inclusion/retention rules,
// and either returns a DryRun preview or performs the actual file deletion.
func EvaluateOrExecuteTask(task DeleteTask, dryRun bool, now time.Time) ExecutionReport {
	rep := ExecutionReport{
		ID:         fmt.Sprintf("run_%d", now.UnixNano()),
		TaskID:     task.ID,
		TaskName:   task.Name,
		FolderPath: task.FolderPath,
		DryRun:     dryRun,
		ExecutedAt: now.Format("2006-01-02 15:04:05"),
		Decisions:  []FileDecision{},
	}

	cleanRoot := filepath.Clean(task.FolderPath)
	info, err := os.Stat(cleanRoot)
	if err != nil || !info.IsDir() {
		rep.Status = "ERROR"
		rep.Message = fmt.Sprintf("La carpeta no existe o no es accesible: %s", task.FolderPath)
		return rep
	}

	// Safety guard against accidental root wipe (e.g. C:\ or /)
	if isDangerousRootPath(cleanRoot) {
		rep.Status = "ERROR"
		rep.Message = fmt.Sprintf("Por seguridad, no se permite ejecutar borrado directamente sobre la raíz del disco (%s). Selecciona una subcarpeta.", cleanRoot)
		return rep
	}

	// Calculate target date window [winStart, winEnd]
	winStart, winEnd, winDesc := computeTargetDateWindow(task, now)
	rep.EffectiveWindow = winDesc

	// Parse filter lists
	targetExts := parseCSVLower(task.TargetExtensions)
	targetPatterns := parseCSVLower(task.TargetNamePatterns)
	keepPatterns := parseCSVLower(task.KeepNamePatterns)
	keepExts := parseCSVLower(task.KeepExtensions)
	keepDaysMap := parseIntSet(task.KeepDaysOfMonth)

	var keepFromTime, keepToTime time.Time
	hasKeepDateRange := false
	if strings.TrimSpace(task.KeepDateFrom) != "" && strings.TrimSpace(task.KeepDateTo) != "" {
		t1, e1 := time.ParseInLocation("2006-01-02", strings.TrimSpace(task.KeepDateFrom), now.Location())
		t2, e2 := time.ParseInLocation("2006-01-02", strings.TrimSpace(task.KeepDateTo), now.Location())
		if e1 == nil && e2 == nil {
			keepFromTime = time.Date(t1.Year(), t1.Month(), t1.Day(), 0, 0, 0, 0, now.Location())
			keepToTime = time.Date(t2.Year(), t2.Month(), t2.Day(), 23, 59, 59, 999999999, now.Location())
			hasKeepDateRange = true
		}
	}

	type scannedFile struct {
		path     string
		relPath  string
		name     string
		size     int64
		fileTime time.Time
	}

	var files []scannedFile

	walkErr := filepath.Walk(cleanRoot, func(path string, fi os.FileInfo, err error) error {
		if err != nil {
			return nil // skip unreadable item
		}
		if path == cleanRoot {
			return nil
		}
		if fi.IsDir() {
			if !task.IncludeSubfolders {
				return filepath.SkipDir
			}
			return nil
		}
		rel, err := filepath.Rel(cleanRoot, path)
		if err != nil {
			rel = fi.Name()
		}
		fTime := fi.ModTime()
		if task.DateField == "created" {
			fTime = getFileCreationTime(fi)
		}
		files = append(files, scannedFile{
			path:     path,
			relPath:  rel,
			name:     fi.Name(),
			size:     fi.Size(),
			fileTime: fTime,
		})
		return nil
	})
	if walkErr != nil {
		rep.Status = "ERROR"
		rep.Message = fmt.Sprintf("Error al explorar la carpeta: %v", walkErr)
		return rep
	}

	// Sort newest first so we can easily identify the top N newest files
	sort.Slice(files, func(i, j int) bool {
		return files[i].fileTime.After(files[j].fileTime)
	})

	rep.TotalFilesScanned = len(files)

	// Track top N newest files if KeepNewestCount > 0
	newestProtected := make(map[string]bool)
	if task.KeepNewestCount > 0 {
		for i := 0; i < len(files) && i < task.KeepNewestCount; i++ {
			newestProtected[files[i].path] = true
		}
	}

	for _, f := range files {
		dec := FileDecision{
			Path:         f.path,
			RelPath:      f.relPath,
			Name:         f.name,
			Size:         f.size,
			FileDate:     f.fileTime.Format("2006-01-02"),
			FileDateTime: f.fileTime.Format("2006-01-02 15:04"),
		}

		// 1. Check inclusion filters (TargetExtensions & TargetNamePatterns)
		if len(targetExts) > 0 {
			ext := strings.ToLower(filepath.Ext(f.name))
			if !containsNormalizedExt(targetExts, ext) {
				dec.Action = "KEEP"
				dec.Reason = fmt.Sprintf("No coincide con las extensiones a borrar (%s)", task.TargetExtensions)
				rep.FilesKept++
				rep.Decisions = append(rep.Decisions, dec)
				continue
			}
		}
		if len(targetPatterns) > 0 {
			if !matchesAnyPattern(f.name, targetPatterns) {
				dec.Action = "KEEP"
				dec.Reason = fmt.Sprintf("El nombre no coincide con el filtro a borrar (%s)", task.TargetNamePatterns)
				rep.FilesKept++
				rep.Decisions = append(rep.Decisions, dec)
				continue
			}
		}

		// 2. Check if file date falls within target deletion window [winStart, winEnd]
		if f.fileTime.Before(winStart) || f.fileTime.After(winEnd) {
			dec.Action = "KEEP"
			dec.Reason = fmt.Sprintf("Fuera del rango de fechas a borrar (%s)", winDesc)
			rep.FilesKept++
			rep.Decisions = append(rep.Decisions, dec)
			continue
		}

		// 3. Check Retention / Exclusion rules ("Solo deja tal...")
		if len(keepExts) > 0 {
			ext := strings.ToLower(filepath.Ext(f.name))
			if containsNormalizedExt(keepExts, ext) {
				dec.Action = "KEEP"
				dec.Reason = fmt.Sprintf("Protegido por extensión excluida (%s)", ext)
				rep.FilesKept++
				rep.Decisions = append(rep.Decisions, dec)
				continue
			}
		}

		if len(keepPatterns) > 0 {
			if matched, pat := matchesAnyPatternDetail(f.name, keepPatterns); matched {
				dec.Action = "KEEP"
				dec.Reason = fmt.Sprintf("Protegido por regla 'Solo deja nombre/patrón' (%s)", pat)
				rep.FilesKept++
				rep.Decisions = append(rep.Decisions, dec)
				continue
			}
		}

		if hasKeepDateRange && !f.fileTime.Before(keepFromTime) && !f.fileTime.After(keepToTime) {
			dec.Action = "KEEP"
			dec.Reason = fmt.Sprintf("Protegido por rango de fechas a conservar (%s a %s)", task.KeepDateFrom, task.KeepDateTo)
			rep.FilesKept++
			rep.Decisions = append(rep.Decisions, dec)
			continue
		}

		if task.KeepFirstOfMonth && f.fileTime.Day() == 1 {
			dec.Action = "KEEP"
			dec.Reason = "Protegido por regla: Conservar archivos del día 1 de cada mes"
			rep.FilesKept++
			rep.Decisions = append(rep.Decisions, dec)
			continue
		}

		if task.KeepLastOfMonth && isLastDayOfMonth(f.fileTime) {
			dec.Action = "KEEP"
			dec.Reason = "Protegido por regla: Conservar archivos del último día del mes"
			rep.FilesKept++
			rep.Decisions = append(rep.Decisions, dec)
			continue
		}

		if keepDaysMap[f.fileTime.Day()] {
			dec.Action = "KEEP"
			dec.Reason = fmt.Sprintf("Protegido por regla: Conservar archivos del día %d del mes", f.fileTime.Day())
			rep.FilesKept++
			rep.Decisions = append(rep.Decisions, dec)
			continue
		}

		if task.KeepRecentDays > 0 {
			cutoff := now.AddDate(0, 0, -task.KeepRecentDays)
			if f.fileTime.After(cutoff) {
				dec.Action = "KEEP"
				dec.Reason = fmt.Sprintf("Protegido por tener menos de %d días de antigüedad", task.KeepRecentDays)
				rep.FilesKept++
				rep.Decisions = append(rep.Decisions, dec)
				continue
			}
		}

		if newestProtected[f.path] {
			dec.Action = "KEEP"
			dec.Reason = fmt.Sprintf("Protegido por estar entre los %d archivos más recientes", task.KeepNewestCount)
			rep.FilesKept++
			rep.Decisions = append(rep.Decisions, dec)
			continue
		}

		// File qualifies for deletion!
		if dryRun {
			dec.Action = "DELETE"
			dec.Reason = fmt.Sprintf("Cumple condición de borrado (%s)", winDesc)
			rep.FilesToDelete++
			rep.BytesFreed += f.size
		} else {
			if err := os.Remove(f.path); err != nil {
				dec.Action = "ERROR"
				dec.Reason = fmt.Sprintf("No se pudo borrar: %v", err)
				rep.ErrorsCount++
			} else {
				dec.Action = "DELETED"
				dec.Reason = fmt.Sprintf("Borrado exitosamente (%s)", winDesc)
				rep.FilesToDelete++
				rep.BytesFreed += f.size
			}
		}
		rep.Decisions = append(rep.Decisions, dec)
	}

	// Clean empty subfolders if enabled and not dryRun
	if !dryRun && task.IncludeSubfolders && task.RemoveEmptyFolders {
		removeEmptySubdirs(cleanRoot)
	}

	if dryRun {
		rep.Status = "DRY_RUN"
		rep.Message = fmt.Sprintf("Simulación completada: %d archivos se borrarán (%s) y %d archivos se conservarán.",
			rep.FilesToDelete, formatBytes(rep.BytesFreed), rep.FilesKept)
	} else if rep.ErrorsCount > 0 {
		rep.Status = "PARTIAL"
		rep.Message = fmt.Sprintf("Ejecución con advertencias: %d borrados (%s), %d conservados, %d errores.",
			rep.FilesToDelete, formatBytes(rep.BytesFreed), rep.FilesKept, rep.ErrorsCount)
	} else {
		rep.Status = "SUCCESS"
		rep.Message = fmt.Sprintf("Ejecución exitosa: %d archivos borrados (%s liberados) y %d conservados.",
			rep.FilesToDelete, formatBytes(rep.BytesFreed), rep.FilesKept)
	}

	return rep
}

func computeTargetDateWindow(task DeleteTask, now time.Time) (time.Time, time.Time, string) {
	loc := now.Location()
	switch task.DateRuleType {
	case "date_range":
		t1, e1 := time.ParseInLocation("2006-01-02", strings.TrimSpace(task.DateFrom), loc)
		t2, e2 := time.ParseInLocation("2006-01-02", strings.TrimSpace(task.DateTo), loc)
		if e1 == nil && e2 == nil {
			start := time.Date(t1.Year(), t1.Month(), t1.Day(), 0, 0, 0, 0, loc)
			end := time.Date(t2.Year(), t2.Month(), t2.Day(), 23, 59, 59, 999999999, loc)
			return start, end, fmt.Sprintf("Desde %s hasta %s", start.Format("2006-01-02"), end.Format("2006-01-02"))
		}

	case "prev_month":
		firstOfCurrent := time.Date(now.Year(), now.Month(), 1, 0, 0, 0, 0, loc)
		lastOfPrev := firstOfCurrent.AddDate(0, 0, -1)
		prevYear, prevMonth, maxDay := lastOfPrev.Year(), lastOfPrev.Month(), lastOfPrev.Day()

		dayFrom := task.PrevMonthDayFrom
		if dayFrom < 1 {
			dayFrom = 1
		}
		if dayFrom > maxDay {
			dayFrom = maxDay
		}
		dayTo := task.PrevMonthDayTo
		if dayTo < 1 || dayTo > maxDay {
			dayTo = maxDay
		}
		if dayTo < dayFrom {
			dayTo = maxDay
		}

		start := time.Date(prevYear, prevMonth, dayFrom, 0, 0, 0, 0, loc)
		end := time.Date(prevYear, prevMonth, dayTo, 23, 59, 59, 999999999, loc)
		return start, end, fmt.Sprintf("Mes anterior (%s al %s)", start.Format("2006-01-02"), end.Format("2006-01-02"))

	case "current_month_days":
		firstOfNext := time.Date(now.Year(), now.Month()+1, 1, 0, 0, 0, 0, loc)
		maxDay := firstOfNext.AddDate(0, 0, -1).Day()
		dayFrom := task.PrevMonthDayFrom
		if dayFrom < 1 {
			dayFrom = 1
		}
		if dayFrom > maxDay {
			dayFrom = maxDay
		}
		dayTo := task.PrevMonthDayTo
		if dayTo < 1 || dayTo > maxDay {
			dayTo = maxDay
		}
		start := time.Date(now.Year(), now.Month(), dayFrom, 0, 0, 0, 0, loc)
		end := time.Date(now.Year(), now.Month(), dayTo, 23, 59, 59, 999999999, loc)
		return start, end, fmt.Sprintf("Mes actual (%s al %s)", start.Format("2006-01-02"), end.Format("2006-01-02"))

	case "older_than_days":
		days := task.OlderThanDays
		if days < 0 {
			days = 0
		}
		start := time.Date(1970, 1, 1, 0, 0, 0, 0, loc)
		end := now.AddDate(0, 0, -days)
		return start, end, fmt.Sprintf("Anteriores a %s (> %d días)", end.Format("2006-01-02 15:04"), days)
	}

	// Default "all_dates"
	start := time.Date(1970, 1, 1, 0, 0, 0, 0, loc)
	end := time.Date(2100, 12, 31, 23, 59, 59, 0, loc)
	return start, end, "Todas las fechas"
}

func isLastDayOfMonth(t time.Time) bool {
	nextDay := t.AddDate(0, 0, 1)
	return nextDay.Month() != t.Month()
}

func isDangerousRootPath(p string) bool {
	cleaned := filepath.Clean(p)
	if cleaned == "/" || cleaned == `\` || cleaned == "." {
		return true
	}
	// Windows drive root like "C:\" or "C:"
	if len(cleaned) <= 3 && strings.Contains(cleaned, ":") {
		return true
	}
	lower := strings.ToLower(cleaned)
	if lower == `c:\windows` || lower == `c:\windows\system32` || lower == `c:\program files` || lower == `c:\program files (x86)` {
		return true
	}
	return false
}

func parseCSVLower(s string) []string {
	var out []string
	for _, part := range strings.Split(s, ",") {
		trimmed := strings.ToLower(strings.TrimSpace(part))
		if trimmed != "" {
			out = append(out, trimmed)
		}
	}
	return out
}

func parseIntSet(s string) map[int]bool {
	res := make(map[int]bool)
	for _, part := range strings.Split(s, ",") {
		trimmed := strings.TrimSpace(part)
		if n, err := strconv.Atoi(trimmed); err == nil && n >= 1 && n <= 31 {
			res[n] = true
		}
	}
	return res
}

func containsNormalizedExt(list []string, ext string) bool {
	extWithDot := ext
	if !strings.HasPrefix(extWithDot, ".") && extWithDot != "" {
		extWithDot = "." + extWithDot
	}
	for _, item := range list {
		norm := item
		if !strings.HasPrefix(norm, ".") {
			norm = "." + norm
		}
		if norm == extWithDot {
			return true
		}
	}
	return false
}

func matchesAnyPattern(name string, patterns []string) bool {
	matched, _ := matchesAnyPatternDetail(name, patterns)
	return matched
}

func matchesAnyPatternDetail(name string, patterns []string) (bool, string) {
	lowerName := strings.ToLower(name)
	for _, pat := range patterns {
		if ok, _ := filepath.Match(pat, lowerName); ok {
			return true, pat
		}
		// Also support simple substring if pattern has no wildcard characters
		if !strings.ContainsAny(pat, "*?") && strings.Contains(lowerName, pat) {
			return true, pat
		}
	}
	return false, ""
}

func removeEmptySubdirs(root string) {
	var dirs []string
	_ = filepath.Walk(root, func(path string, info os.FileInfo, err error) error {
		if err == nil && info.IsDir() && path != root {
			dirs = append(dirs, path)
		}
		return nil
	})
	// Sort deepest first
	sort.Slice(dirs, func(i, j int) bool {
		return len(dirs[i]) > len(dirs[j])
	})
	for _, d := range dirs {
		entries, err := os.ReadDir(d)
		if err == nil && len(entries) == 0 {
			_ = os.Remove(d)
		}
	}
}

func sanitizeName(s string) string {
	var out strings.Builder
	for _, r := range s {
		if (r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z') || (r >= '0' && r <= '9') || r == '-' || r == '_' {
			out.WriteRune(r)
		} else if r == ' ' {
			out.WriteRune('_')
		}
	}
	res := out.String()
	if res == "" {
		return "tarea"
	}
	return res
}

func formatBytes(b int64) string {
	const unit = 1024
	if b < unit {
		return fmt.Sprintf("%d B", b)
	}
	div, exp := int64(unit), 0
	for n := b / unit; n >= unit; n /= unit {
		div *= unit
		exp++
	}
	return fmt.Sprintf("%.2f %cB", float64(b)/float64(div), "KMGTPE"[exp])
}
