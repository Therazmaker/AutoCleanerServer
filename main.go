package main

import (
	"embed"
	"encoding/json"
	"flag"
	"fmt"
	"net"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"sort"
	"strings"
	"time"
)

//go:embed ui/index.html
var embeddedUI embed.FS

func main() {
	runTaskID := flag.String("run-task", "", "Ejecuta inmediatamente una tarea por su ID en modo silencioso y termina")
	runDue := flag.Bool("run-due", false, "Ejecuta todas las tareas programadas pendientes y termina")
	daemonMode := flag.Bool("daemon", false, "Ejecuta en segundo plano sin abrir ventana al iniciar")
	portFlag := flag.Int("port", 8787, "Puerto local para la interfaz gráfica")
	noBrowser := flag.Bool("no-browser", false, "No abrir ventana del navegador automáticamente")
	flag.Parse()

	exePath, err := os.Executable()
	baseDir := "."
	if err == nil {
		baseDir = filepath.Dir(exePath)
	}

	store, err := NewAppStore(baseDir)
	if err != nil {
		// Fallback to current working directory if executable dir isn't writable
		baseDir, _ = os.Getwd()
		store, err = NewAppStore(baseDir)
		if err != nil {
			fmt.Fprintf(os.Stderr, "Error inicializando almacenamiento: %v\n", err)
			os.Exit(1)
		}
	}

	// Headless CLI mode 1: --run-task <id> (invoked by Windows Task Scheduler schtasks.exe)
	if *runTaskID != "" {
		store.mu.Lock()
		var target *DeleteTask
		idx := -1
		for i := range store.Tasks {
			if store.Tasks[i].ID == *runTaskID {
				target = &store.Tasks[i]
				idx = i
				break
			}
		}
		store.mu.Unlock()

		if target == nil {
			fmt.Fprintf(os.Stderr, "Tarea no encontrada: %s\n", *runTaskID)
			os.Exit(1)
		}
		rep := EvaluateOrExecuteTask(*target, false, time.Now())
		store.mu.Lock()
		if idx >= 0 && idx < len(store.Tasks) {
			store.Tasks[idx].LastRunAt = rep.ExecutedAt
			store.Tasks[idx].LastRunStatus = rep.Message
			_ = store.saveTasksLocked()
		}
		store.mu.Unlock()
		store.AddHistory(rep)
		fmt.Println(rep.Message)
		return
	}

	// Headless CLI mode 2: --run-due
	if *runDue {
		checkAndRunDueTasks(store)
		return
	}

	addr := fmt.Sprintf("127.0.0.1:%d", *portFlag)
	url := fmt.Sprintf("http://%s", addr)

	// Check if another instance is already listening on this port
	if conn, err := net.DialTimeout("tcp", addr, 400*time.Millisecond); err == nil {
		_ = conn.Close()
		if !*daemonMode && !*noBrowser {
			openDesktopWindow(url)
		}
		return
	}

	// Background scheduler ticker (checks every 20 seconds for due tasks)
	go func() {
		ticker := time.NewTicker(20 * time.Second)
		defer ticker.Stop()
		for range ticker.C {
			checkAndRunDueTasks(store)
		}
	}()

	mux := http.NewServeMux()

	// Serve embedded UI
	mux.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
		data, err := embeddedUI.ReadFile("ui/index.html")
		if err != nil {
			http.Error(w, "UI no encontrada", http.StatusInternalServerError)
			return
		}
		w.Header().Set("Content-Type", "text/html; charset=utf-8")
		_, _ = w.Write(data)
	})

	// Download compiled Windows .exe if present in directory
	mux.HandleFunc("/download/AutoCleanerServer.exe", func(w http.ResponseWriter, r *http.Request) {
		exeFile := filepath.Join(baseDir, "AutoCleanerServer.exe")
		if _, err := os.Stat(exeFile); err != nil {
			http.Error(w, "AutoCleanerServer.exe no encontrado", http.StatusNotFound)
			return
		}
		w.Header().Set("Content-Disposition", `attachment; filename="AutoCleanerServer.exe"`)
		w.Header().Set("Content-Type", "application/octet-stream")
		http.ServeFile(w, r, exeFile)
	})

	// Download ZIP bundle (.exe + .bat)
	mux.HandleFunc("/download/AutoCleanerServer_Windows.zip", func(w http.ResponseWriter, r *http.Request) {
		zipFile := filepath.Join(baseDir, "AutoCleanerServer_Windows.zip")
		if _, err := os.Stat(zipFile); err != nil {
			http.Error(w, "AutoCleanerServer_Windows.zip no encontrado", http.StatusNotFound)
			return
		}
		w.Header().Set("Content-Disposition", `attachment; filename="AutoCleanerServer_Windows.zip"`)
		w.Header().Set("Content-Type", "application/zip")
		http.ServeFile(w, r, zipFile)
	})

	// State endpoint
	mux.HandleFunc("/api/state", func(w http.ResponseWriter, r *http.Request) {
		store.mu.Lock()
		defer store.mu.Unlock()
		writeJSON(w, http.StatusOK, map[string]any{
			"tasks":       store.Tasks,
			"history":     store.History,
			"server_time": time.Now().Format("2006-01-02 15:04:05"),
			"os":          runtime.GOOS,
			"exe_path":    exePath,
			"base_dir":    baseDir,
		})
	})

	// Save / Update task
	mux.HandleFunc("/api/tasks/save", func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			http.Error(w, "Método no permitido", http.StatusMethodNotAllowed)
			return
		}
		var task DeleteTask
		if err := json.NewDecoder(r.Body).Decode(&task); err != nil {
			writeJSON(w, http.StatusBadRequest, map[string]any{"error": err.Error()})
			return
		}
		task.FolderPath = strings.TrimSpace(task.FolderPath)
		if task.FolderPath == "" {
			writeJSON(w, http.StatusBadRequest, map[string]any{"error": "La ruta de la carpeta es obligatoria"})
			return
		}
		if isDangerousRootPath(task.FolderPath) {
			writeJSON(w, http.StatusBadRequest, map[string]any{"error": "Por seguridad no se permite seleccionar la raíz directa del disco (ej. C:\\). Usa una subcarpeta."})
			return
		}

		store.mu.Lock()
		if task.ID == "" {
			task.ID = fmt.Sprintf("t_%d", time.Now().Unix())
			task.CreatedAt = time.Now().Format("2006-01-02 15:04:05")
			store.Tasks = append([]DeleteTask{task}, store.Tasks...)
		} else {
			updated := false
			for i := range store.Tasks {
				if store.Tasks[i].ID == task.ID {
					task.CreatedAt = store.Tasks[i].CreatedAt
					task.LastRunAt = store.Tasks[i].LastRunAt
					task.LastRunStatus = store.Tasks[i].LastRunStatus
					store.Tasks[i] = task
					updated = true
					break
				}
			}
			if !updated {
				task.CreatedAt = time.Now().Format("2006-01-02 15:04:05")
				store.Tasks = append([]DeleteTask{task}, store.Tasks...)
			}
		}
		_ = store.saveTasksLocked()
		store.mu.Unlock()

		winMsg, _ := SyncTaskWithWindowsScheduler(task)
		_, _ = EnsureWindowsStartupDaemon()
		writeJSON(w, http.StatusOK, map[string]any{
			"ok":            true,
			"task":          task,
			"win_scheduler": winMsg,
		})
	})

	// Delete task
	mux.HandleFunc("/api/tasks/delete", func(w http.ResponseWriter, r *http.Request) {
		var req struct {
			ID string `json:"id"`
		}
		_ = json.NewDecoder(r.Body).Decode(&req)

		store.mu.Lock()
		var filtered []DeleteTask
		for _, t := range store.Tasks {
			if t.ID == req.ID {
				RemoveTaskFromWindowsScheduler(t)
				continue
			}
			filtered = append(filtered, t)
		}
		store.Tasks = filtered
		_ = store.saveTasksLocked()
		store.mu.Unlock()

		writeJSON(w, http.StatusOK, map[string]any{"ok": true})
	})

	// Simulate an unsaved/editor task
	mux.HandleFunc("/api/tasks/simulate", func(w http.ResponseWriter, r *http.Request) {
		var task DeleteTask
		if err := json.NewDecoder(r.Body).Decode(&task); err != nil {
			writeJSON(w, http.StatusBadRequest, map[string]any{"error": err.Error()})
			return
		}
		rep := EvaluateOrExecuteTask(task, true, time.Now())
		store.AddHistory(rep)
		writeJSON(w, http.StatusOK, rep)
	})

	// Run or simulate a saved task by ID
	mux.HandleFunc("/api/tasks/run", func(w http.ResponseWriter, r *http.Request) {
		var req struct {
			ID     string `json:"id"`
			DryRun bool   `json:"dry_run"`
		}
		_ = json.NewDecoder(r.Body).Decode(&req)

		store.mu.Lock()
		var target *DeleteTask
		idx := -1
		for i := range store.Tasks {
			if store.Tasks[i].ID == req.ID {
				c := store.Tasks[i]
				target = &c
				idx = i
				break
			}
		}
		store.mu.Unlock()

		if target == nil {
			writeJSON(w, http.StatusNotFound, map[string]any{"error": "Tarea no encontrada"})
			return
		}

		rep := EvaluateOrExecuteTask(*target, req.DryRun, time.Now())
		if !req.DryRun {
			store.mu.Lock()
			if idx >= 0 && idx < len(store.Tasks) {
				store.Tasks[idx].LastRunAt = rep.ExecutedAt
				store.Tasks[idx].LastRunStatus = rep.Message
				_ = store.saveTasksLocked()
			}
			store.mu.Unlock()
		}
		store.AddHistory(rep)
		writeJSON(w, http.StatusOK, rep)
	})

	// Create Demo Folder + Sample Task ("El día 1 de cada mes borra los archivos del mes anterior y solo deja...")
	mux.HandleFunc("/api/demo/create", func(w http.ResponseWriter, r *http.Request) {
		now := time.Now()
		demoDir, count, err := CreateDemoSampleFolder(baseDir, now)
		if err != nil {
			writeJSON(w, http.StatusInternalServerError, map[string]any{"error": err.Error()})
			return
		}

		demoTask := DeleteTask{
			ID:                 "demo_mensual_dia1",
			Name:               "Ejemplo: Día 1 del mes borra mes anterior y deja Cierre + Importantes",
			FolderPath:         demoDir,
			Enabled:            true,
			ScheduleType:       "monthly",
			DayOfMonth:         1,
			RunTime:            "02:00",
			SyncWinTask:        true,
			DateField:          "modified",
			DateRuleType:       "prev_month",
			PrevMonthDayFrom:   1,
			PrevMonthDayTo:     31,
			IncludeSubfolders:  true,
			RemoveEmptyFolders: false,
			KeepNamePatterns:   "*cierre*, *IMPORTANTE*",
			KeepFirstOfMonth:   true,
			KeepLastOfMonth:    true,
			CreatedAt:          now.Format("2006-01-02 15:04:05"),
		}

		store.mu.Lock()
		replaced := false
		for i := range store.Tasks {
			if store.Tasks[i].ID == demoTask.ID {
				store.Tasks[i] = demoTask
				replaced = true
				break
			}
		}
		if !replaced {
			store.Tasks = append([]DeleteTask{demoTask}, store.Tasks...)
		}
		_ = store.saveTasksLocked()
		store.mu.Unlock()

		preview := EvaluateOrExecuteTask(demoTask, true, now)
		store.AddHistory(preview)

		writeJSON(w, http.StatusOK, map[string]any{
			"ok":            true,
			"demo_dir":      demoDir,
			"files_created": count,
			"task":          demoTask,
			"preview":       preview,
		})
	})

	// Server Folder Browser endpoint
	mux.HandleFunc("/api/fs/browse", func(w http.ResponseWriter, r *http.Request) {
		reqPath := strings.TrimSpace(r.URL.Query().Get("path"))
		if reqPath == "" {
			reqPath = baseDir
		}
		clean := filepath.Clean(reqPath)
		entries, err := os.ReadDir(clean)
		if err != nil {
			clean = baseDir
			entries, _ = os.ReadDir(clean)
		}

		type dirEntry struct {
			Name string `json:"name"`
			Path string `json:"path"`
		}
		var dirs []dirEntry
		for _, e := range entries {
			if e.IsDir() && !strings.HasPrefix(e.Name(), ".") {
				dirs = append(dirs, dirEntry{
					Name: e.Name(),
					Path: filepath.Join(clean, e.Name()),
				})
			}
		}
		sort.Slice(dirs, func(i, j int) bool {
			return strings.ToLower(dirs[i].Name) < strings.ToLower(dirs[j].Name)
		})

		parent := filepath.Dir(clean)
		if parent == clean {
			parent = ""
		}

		writeJSON(w, http.StatusOK, map[string]any{
			"current_path": clean,
			"parent_path":  parent,
			"drives":       listSystemDrives(),
			"directories":  dirs,
		})
	})

	if !*daemonMode && !*noBrowser {
		go func() {
			time.Sleep(350 * time.Millisecond)
			openDesktopWindow(url)
		}()
	}

	fmt.Printf("AutoCleaner Server activo en %s\n", url)
	if err := http.ListenAndServe(addr, mux); err != nil {
		fmt.Fprintf(os.Stderr, "Error en servidor local: %v\n", err)
		os.Exit(1)
	}
}

func checkAndRunDueTasks(store *AppStore) {
	now := time.Now()
	store.mu.Lock()
	var dueTasks []DeleteTask
	for _, t := range store.Tasks {
		if IsTaskDue(t, now) {
			dueTasks = append(dueTasks, t)
		}
	}
	store.mu.Unlock()

	for _, t := range dueTasks {
		rep := EvaluateOrExecuteTask(t, false, now)
		store.mu.Lock()
		for i := range store.Tasks {
			if store.Tasks[i].ID == t.ID {
				store.Tasks[i].LastRunAt = rep.ExecutedAt
				store.Tasks[i].LastRunStatus = rep.Message
				break
			}
		}
		_ = store.saveTasksLocked()
		store.mu.Unlock()
		store.AddHistory(rep)
	}
}

func listSystemDrives() []string {
	if runtime.GOOS != "windows" {
		return []string{"/", "/home/antigravity"}
	}
	var drives []string
	for _, c := range "CDEFGHIJKLMNOPQRSTUVWXYZ" {
		d := fmt.Sprintf("%c:\\", c)
		if _, err := os.Stat(d); err == nil {
			drives = append(drives, d)
		}
	}
	return drives
}

func openDesktopWindow(url string) {
	if runtime.GOOS != "windows" {
		return
	}
	// Try Microsoft Edge in App Window mode (looks like a native desktop app without browser chrome)
	edgePaths := []string{
		`C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe`,
		`C:\Program Files\Microsoft\Edge\Application\msedge.exe`,
	}
	for _, ep := range edgePaths {
		if _, err := os.Stat(ep); err == nil {
			_ = exec.Command(ep, "--app="+url, "--window-size=1260,840").Start()
			return
		}
	}
	// Fallback to default Windows handler
	_ = exec.Command("rundll32", "url.dll,FileProtocolHandler", url).Start()
}

func writeJSON(w http.ResponseWriter, status int, payload any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(payload)
}
