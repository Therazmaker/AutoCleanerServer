package main

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"time"
)

// BuildSchtasksCommand returns the Windows Task Scheduler command line for a task.
func BuildSchtasksCommand(task DeleteTask, exePath string) []string {
	taskWinName := fmt.Sprintf(`\AutoCleanerServer\%s_%s`, sanitizeName(task.Name), task.ID)
	trArg := fmt.Sprintf(`"%s" --run-task %s`, exePath, task.ID)
	runTime := task.RunTime
	if len(runTime) != 5 || !strings.Contains(runTime, ":") {
		runTime = "02:00"
	}

	args := []string{"/Create", "/F", "/TN", taskWinName, "/TR", trArg, "/ST", runTime}

	switch task.ScheduleType {
	case "monthly":
		if task.DayOfMonth <= 0 || task.DayOfMonth > 31 {
			args = append(args, "/SC", "MONTHLY", "/M", "*", "/MO", "LASTDAY")
		} else {
			args = append(args, "/SC", "MONTHLY", "/D", fmt.Sprintf("%d", task.DayOfMonth))
		}
	case "weekly":
		days := []string{"SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"}
		dow := task.DayOfWeek
		if dow < 0 || dow > 6 {
			dow = 1
		}
		args = append(args, "/SC", "WEEKLY", "/D", days[dow])
	case "daily":
		args = append(args, "/SC", "DAILY")
	case "once":
		// Convert YYYY-MM-DD to MM/DD/YYYY or system format; schtasks /SC ONCE requires /SD
		args = append(args, "/SC", "DAILY")
	default:
		return nil
	}
	return args
}

// SyncTaskWithWindowsScheduler registers or removes the task in Windows Task Scheduler (schtasks.exe).
// It first attempts to register as SYSTEM (/RU SYSTEM /RL HIGHEST) so the task runs on Windows Server
// even if no user is logged in via Remote Desktop or after a reboot. If the process is not elevated,
// it falls back to registering under the current user account.
func SyncTaskWithWindowsScheduler(task DeleteTask) (string, error) {
	exePath, err := os.Executable()
	if err != nil {
		exePath = `C:\AutoCleanerServer\AutoCleanerServer.exe`
	}
	taskWinName := fmt.Sprintf(`\AutoCleanerServer\%s_%s`, sanitizeName(task.Name), task.ID)

	if !task.Enabled || !task.SyncWinTask || task.ScheduleType == "manual" {
		if runtime.GOOS == "windows" {
			_ = exec.Command("schtasks", "/Delete", "/TN", taskWinName, "/F").Run()
		}
		return "Desvinculado del Programador de Tareas de Windows", nil
	}

	baseArgs := BuildSchtasksCommand(task, exePath)
	if len(baseArgs) == 0 {
		return "Modo manual (sin programación)", nil
	}

	if runtime.GOOS != "windows" {
		cmdStr := "schtasks " + strings.Join(append(baseArgs, "/RU", "SYSTEM", "/RL", "HIGHEST"), " ")
		return fmt.Sprintf("Listo para Windows Task Scheduler (%s)", cmdStr), nil
	}

	// 1. Try registering as SYSTEM so it runs 24/7 even with session closed or after server reboot
	sysArgs := append([]string{}, baseArgs...)
	sysArgs = append(sysArgs, "/RU", "SYSTEM", "/RL", "HIGHEST")
	if err := exec.Command("schtasks", sysArgs...).Run(); err == nil {
		return fmt.Sprintf("Registrado en Windows Server como SYSTEM (corre aún con sesión cerrada): %s", taskWinName), nil
	}

	// 2. Fallback to current user if not running as Administrator
	out, err := exec.Command("schtasks", baseArgs...).CombinedOutput()
	if err != nil {
		return fmt.Sprintf("Advertencia al registrar en schtasks (%s): %v", strings.TrimSpace(string(out)), err), err
	}
	return fmt.Sprintf("Registrado en Programador de Tareas de Windows (usuario actual): %s", taskWinName), nil
}

// EnsureWindowsStartupDaemon registers the background daemon to start automatically with Windows Server.
func EnsureWindowsStartupDaemon() (string, error) {
	exePath, err := os.Executable()
	if err != nil {
		exePath = `C:\AutoCleanerServer\AutoCleanerServer.exe`
	}
	trArg := fmt.Sprintf(`"%s" --daemon`, exePath)
	taskName := `\AutoCleanerServer\ServicioResidente`

	if runtime.GOOS != "windows" {
		return fmt.Sprintf("En Windows Server registrará: schtasks /Create /F /TN %s /TR %s /SC ONSTART /RU SYSTEM /RL HIGHEST", taskName, trArg), nil
	}

	// Try ONSTART as SYSTEM first (runs when Windows Server boots, before anyone logs in)
	if err := exec.Command("schtasks", "/Create", "/F", "/TN", taskName, "/TR", trArg, "/SC", "ONSTART", "/RU", "SYSTEM", "/RL", "HIGHEST").Run(); err == nil {
		return "Servicio residente registrado al arranque de Windows Server (ONSTART como SYSTEM)", nil
	}

	// Fallback: ONLOGON for current user
	out, err := exec.Command("schtasks", "/Create", "/F", "/TN", taskName, "/TR", trArg, "/SC", "ONLOGON").CombinedOutput()
	if err != nil {
		return fmt.Sprintf("No se pudo registrar inicio automático (%s): %v", strings.TrimSpace(string(out)), err), err
	}
	return "Servicio residente registrado al iniciar sesión en Windows (ONLOGON)", nil
}

// RemoveTaskFromWindowsScheduler deletes the task from Windows Task Scheduler.
func RemoveTaskFromWindowsScheduler(task DeleteTask) {
	if runtime.GOOS != "windows" {
		return
	}
	taskWinName := fmt.Sprintf(`\AutoCleanerServer\%s_%s`, sanitizeName(task.Name), task.ID)
	_ = exec.Command("schtasks", "/Delete", "/TN", taskWinName, "/F").Run()
}

// IsTaskDue checks if an enabled task should be triggered by the built-in background scheduler at time `now`.
func IsTaskDue(task DeleteTask, now time.Time) bool {
	if !task.Enabled || task.ScheduleType == "manual" {
		return false
	}
	currentHM := now.Format("15:04")
	targetHM := task.RunTime
	if targetHM == "" {
		targetHM = "02:00"
	}
	if currentHM != targetHM {
		return false
	}

	// Ensure it hasn't already run during this exact minute
	if strings.HasPrefix(task.LastRunAt, now.Format("2006-01-02 15:04")) {
		return false
	}

	switch task.ScheduleType {
	case "monthly":
		if task.DayOfMonth == 0 {
			return isLastDayOfMonth(now)
		}
		return now.Day() == task.DayOfMonth
	case "weekly":
		return int(now.Weekday()) == task.DayOfWeek
	case "daily":
		return true
	case "once":
		return now.Format("2006-01-02") == task.OnceDate
	}
	return false
}

// CreateDemoSampleFolder creates a safe sandbox folder with sample files having different timestamps
// across the previous month and current month so the user can test rules immediately.
func CreateDemoSampleFolder(baseDir string, now time.Time) (string, int, error) {
	demoDir := filepath.Join(baseDir, "Carpeta_Prueba_Demo")
	if err := os.MkdirAll(demoDir, 0755); err != nil {
		return "", 0, err
	}

	loc := now.Location()
	firstOfCurrent := time.Date(now.Year(), now.Month(), 1, 10, 0, 0, 0, loc)
	lastOfPrev := firstOfCurrent.AddDate(0, 0, -1)
	prevYear, prevMonth := lastOfPrev.Year(), lastOfPrev.Month()

	type sampleSpec struct {
		name string
		date time.Time
		size int
	}

	samples := []sampleSpec{
		{
			name: fmt.Sprintf("backup_%04d_%02d_01_apertura.bak", prevYear, prevMonth),
			date: time.Date(prevYear, prevMonth, 1, 8, 30, 0, 0, loc),
			size: 20480,
		},
		{
			name: fmt.Sprintf("log_transacciones_%04d_%02d_05.log", prevYear, prevMonth),
			date: time.Date(prevYear, prevMonth, 5, 14, 15, 0, 0, loc),
			size: 15360,
		},
		{
			name: fmt.Sprintf("log_transacciones_%04d_%02d_12.log", prevYear, prevMonth),
			date: time.Date(prevYear, prevMonth, 12, 11, 0, 0, 0, loc),
			size: 45056,
		},
		{
			name: fmt.Sprintf("reporte_quincenal_%04d_%02d_15_IMPORTANTE.xlsx", prevYear, prevMonth),
			date: time.Date(prevYear, prevMonth, 15, 17, 45, 0, 0, loc),
			size: 32768,
		},
		{
			name: fmt.Sprintf("temporal_sistema_%04d_%02d_20.tmp", prevYear, prevMonth),
			date: time.Date(prevYear, prevMonth, 20, 9, 20, 0, 0, loc),
			size: 61440,
		},
		{
			name: fmt.Sprintf("backup_diario_%04d_%02d_25.bak", prevYear, prevMonth),
			date: time.Date(prevYear, prevMonth, 25, 22, 0, 0, 0, loc),
			size: 51200,
		},
		{
			name: fmt.Sprintf("cierre_mensual_%04d_%02d_%02d.bak", prevYear, prevMonth, lastOfPrev.Day()),
			date: time.Date(prevYear, prevMonth, lastOfPrev.Day(), 23, 30, 0, 0, loc),
			size: 81920,
		},
		{
			name: fmt.Sprintf("operaciones_mes_actual_%s.log", now.Format("2006_01_02")),
			date: now.Add(-2 * time.Hour),
			size: 12288,
		},
	}

	created := 0
	for _, s := range samples {
		p := filepath.Join(demoDir, s.name)
		buf := make([]byte, s.size)
		copy(buf, []byte(fmt.Sprintf("Archivo de prueba generado por AutoCleaner Server - Fecha: %s\n", s.date.Format(time.RFC3339))))
		if err := os.WriteFile(p, buf, 0644); err == nil {
			_ = os.Chtimes(p, s.date, s.date)
			created++
		}
	}

	return demoDir, created, nil
}
