using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Xml.Serialization;

[assembly: AssemblyTitle("AutoCleaner Server - Gestor de Borrado Programado")]
[assembly: AssemblyDescription("Utilidad de administracion para programar tareas de limpieza de archivos en Windows Server")]
[assembly: AssemblyCompany("Administracion de Servidores")]
[assembly: AssemblyProduct("AutoCleaner Server")]
[assembly: AssemblyCopyright("Copyright (c) 2026")]
[assembly: AssemblyVersion("2.0.0.0")]
[assembly: AssemblyFileVersion("2.0.0.0")]

namespace AutoCleanerServer
{
    [Serializable]
    public class DeleteTask
    {
        public string ID { get; set; }
        public string Name { get; set; }
        public string FolderPath { get; set; }
        public bool Enabled { get; set; }
        public bool IncludeSubfolders { get; set; }
        public bool RemoveEmptyFolders { get; set; }

        // Schedule: "MONTHLY", "WEEKLY", "DAILY", "ONCE", "MANUAL"
        public string ScheduleType { get; set; }
        public int DayOfMonth { get; set; } // 1..31, or 0 for Last Day of Month
        public int DayOfWeek { get; set; }  // 0=Sun..6=Sat
        public string RunTime { get; set; } // "HH:mm"
        public string OnceDate { get; set; } // "yyyy-MM-dd"
        public bool SyncWindowsTask { get; set; }

        // Date Filter: "PREV_MONTH", "DATE_RANGE", "OLDER_DAYS", "CURR_MONTH", "ALL_DATES"
        public string DateField { get; set; } // "MODIFIED" or "CREATED"
        public string DateRuleType { get; set; }
        public string DateFrom { get; set; } // "yyyy-MM-dd"
        public string DateTo { get; set; }   // "yyyy-MM-dd"
        public int MonthDayFrom { get; set; }
        public int MonthDayTo { get; set; }
        public int OlderThanDays { get; set; }

        // Inclusion Filters
        public string TargetExtensions { get; set; }
        public string TargetNamePatterns { get; set; }

        // Retention / Exclusion ("Solo deja tal")
        public string KeepNamePatterns { get; set; }
        public string KeepExtensions { get; set; }
        public string KeepDaysOfMonth { get; set; }
        public bool KeepFirstOfMonth { get; set; }
        public bool KeepLastOfMonth { get; set; }
        public int KeepNewestCount { get; set; }
        public int KeepRecentDays { get; set; }
        public bool UseKeepDateRange { get; set; }
        public string KeepDateFrom { get; set; }
        public string KeepDateTo { get; set; }

        public string LastRunAt { get; set; }
        public string LastRunSummary { get; set; }

        public DeleteTask()
        {
            ID = "T_" + DateTime.Now.Ticks.ToString();
            Name = "Limpieza Mensual Dia 1";
            FolderPath = "";
            Enabled = true;
            IncludeSubfolders = true;
            RemoveEmptyFolders = false;
            ScheduleType = "MONTHLY";
            DayOfMonth = 1;
            DayOfWeek = 1;
            RunTime = "02:00";
            OnceDate = DateTime.Now.ToString("yyyy-MM-dd");
            SyncWindowsTask = true;
            DateField = "MODIFIED";
            DateRuleType = "PREV_MONTH";
            DateFrom = DateTime.Now.AddMonths(-1).ToString("yyyy-MM-dd");
            DateTo = DateTime.Now.ToString("yyyy-MM-dd");
            MonthDayFrom = 1;
            MonthDayTo = 31;
            OlderThanDays = 30;
            TargetExtensions = "";
            TargetNamePatterns = "";
            KeepNamePatterns = "";
            KeepExtensions = "";
            KeepDaysOfMonth = "";
            KeepFirstOfMonth = false;
            KeepLastOfMonth = true;
            KeepNewestCount = 0;
            KeepRecentDays = 0;
            UseKeepDateRange = false;
            KeepDateFrom = DateTime.Now.ToString("yyyy-MM-dd");
            KeepDateTo = DateTime.Now.ToString("yyyy-MM-dd");
            LastRunAt = "Nunca";
            LastRunSummary = "";
        }
    }

    public class FileEvalItem
    {
        public string FullPath { get; set; }
        public string RelativePath { get; set; }
        public string FileName { get; set; }
        public long SizeBytes { get; set; }
        public DateTime FileTime { get; set; }
        public string Action { get; set; } // "BORRAR", "CONSERVAR", "BORRADO", "ERROR"
        public string Reason { get; set; }
    }

    public class ExecutionResult
    {
        public string WindowDescription { get; set; }
        public int TotalScanned { get; set; }
        public int DeleteCount { get; set; }
        public int KeepCount { get; set; }
        public int ErrorCount { get; set; }
        public long BytesFreed { get; set; }
        public string SummaryMessage { get; set; }
        public List<FileEvalItem> Items { get; set; }

        public ExecutionResult()
        {
            Items = new List<FileEvalItem>();
        }
    }

    public static class CleanerEngine
    {
        public static string BaseDir
        {
            get { return AppDomain.CurrentDomain.BaseDirectory; }
        }

        public static string TasksXmlPath
        {
            get
            {
                string dir = Path.Combine(BaseDir, "data");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return Path.Combine(dir, "tasks.xml");
            }
        }

        public static string LogsDir
        {
            get
            {
                string dir = Path.Combine(BaseDir, "logs");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static List<DeleteTask> LoadTasks()
        {
            try
            {
                if (!File.Exists(TasksXmlPath)) return new List<DeleteTask>();
                XmlSerializer ser = new XmlSerializer(typeof(List<DeleteTask>));
                using (FileStream fs = new FileStream(TasksXmlPath, FileMode.Open, FileAccess.Read))
                {
                    return (List<DeleteTask>)ser.Deserialize(fs);
                }
            }
            catch
            {
                return new List<DeleteTask>();
            }
        }

        public static void SaveTasks(List<DeleteTask> tasks)
        {
            XmlSerializer ser = new XmlSerializer(typeof(List<DeleteTask>));
            using (FileStream fs = new FileStream(TasksXmlPath, FileMode.Create, FileAccess.Write))
            {
                ser.Serialize(fs, tasks);
            }
        }

        public static bool IsDangerousRoot(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return true;
            string clean = Path.GetFullPath(path).TrimEnd('\\', '/');
            if (clean.Length <= 3 && clean.Contains(":")) return true;
            string low = clean.ToLowerInvariant();
            if (low == @"c:\windows" || low == @"c:\windows\system32" || low == @"c:\program files" || low == @"c:\program files (x86)")
                return true;
            return false;
        }

        public static ExecutionResult EvaluateOrRun(DeleteTask task, bool dryRun, DateTime now)
        {
            ExecutionResult res = new ExecutionResult();
            if (String.IsNullOrWhiteSpace(task.FolderPath) || !Directory.Exists(task.FolderPath))
            {
                res.SummaryMessage = "ERROR: La carpeta seleccionada no existe o no es accesible: " + task.FolderPath;
                res.ErrorCount = 1;
                return res;
            }
            if (IsDangerousRoot(task.FolderPath))
            {
                res.SummaryMessage = "ERROR DE SEGURIDAD: No se permite borrar directamente la raiz del disco o carpetas de sistema.";
                res.ErrorCount = 1;
                return res;
            }

            DateTime winStart, winEnd;
            string winDesc;
            ComputeDateWindow(task, now, out winStart, out winEnd, out winDesc);
            res.WindowDescription = winDesc;

            List<string> targetExts = ParseCsv(task.TargetExtensions, true);
            List<string> targetPats = ParseCsv(task.TargetNamePatterns, false);
            List<string> keepPats = ParseCsv(task.KeepNamePatterns, false);
            List<string> keepExts = ParseCsv(task.KeepExtensions, true);
            HashSet<int> keepDays = ParseDays(task.KeepDaysOfMonth);

            DateTime keepFrom = DateTime.MinValue;
            DateTime keepTo = DateTime.MaxValue;
            bool hasKeepRange = false;
            if (task.UseKeepDateRange)
            {
                DateTime kf, kt;
                if (DateTime.TryParseExact(task.KeepDateFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out kf) &&
                    DateTime.TryParseExact(task.KeepDateTo, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out kt))
                {
                    keepFrom = kf.Date;
                    keepTo = kt.Date.AddDays(1).AddTicks(-1);
                    hasKeepRange = true;
                }
            }

            SearchOption opt = task.IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            List<FileInfo> allFiles = new List<FileInfo>();
            try
            {
                DirectoryInfo di = new DirectoryInfo(task.FolderPath);
                allFiles.AddRange(di.GetFiles("*", opt));
            }
            catch (Exception ex)
            {
                res.SummaryMessage = "Error al explorar archivos: " + ex.Message;
                res.ErrorCount = 1;
                return res;
            }

            // Sort newest first
            allFiles.Sort(delegate(FileInfo a, FileInfo b)
            {
                DateTime ta = (task.DateField == "CREATED") ? a.CreationTime : a.LastWriteTime;
                DateTime tb = (task.DateField == "CREATED") ? b.CreationTime : b.LastWriteTime;
                return tb.CompareTo(ta);
            });

            res.TotalScanned = allFiles.Count;

            HashSet<string> newestProtected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (task.KeepNewestCount > 0)
            {
                for (int i = 0; i < allFiles.Count && i < task.KeepNewestCount; i++)
                {
                    newestProtected.Add(allFiles[i].FullName);
                }
            }

            string rootClean = Path.GetFullPath(task.FolderPath).TrimEnd('\\') + "\\";

            foreach (FileInfo fi in allFiles)
            {
                DateTime fTime = (task.DateField == "CREATED") ? fi.CreationTime : fi.LastWriteTime;
                string rel = fi.FullName.StartsWith(rootClean, StringComparison.OrdinalIgnoreCase)
                    ? fi.FullName.Substring(rootClean.Length)
                    : fi.Name;

                FileEvalItem item = new FileEvalItem
                {
                    FullPath = fi.FullName,
                    RelativePath = rel,
                    FileName = fi.Name,
                    SizeBytes = fi.Length,
                    FileTime = fTime
                };

                // 1. Inclusion Filters
                if (targetExts.Count > 0)
                {
                    string ext = (fi.Extension ?? "").ToLowerInvariant();
                    if (!targetExts.Contains(ext))
                    {
                        item.Action = "CONSERVAR";
                        item.Reason = "No coincide con extensiones a borrar (" + task.TargetExtensions + ")";
                        res.KeepCount++;
                        res.Items.Add(item);
                        continue;
                    }
                }
                if (targetPats.Count > 0)
                {
                    string matchedPat;
                    if (!MatchesAnyPattern(fi.Name, targetPats, out matchedPat))
                    {
                        item.Action = "CONSERVAR";
                        item.Reason = "Nombre no coincide con patron a borrar (" + task.TargetNamePatterns + ")";
                        res.KeepCount++;
                        res.Items.Add(item);
                        continue;
                    }
                }

                // 2. Date Window Check
                if (fTime < winStart || fTime > winEnd)
                {
                    item.Action = "CONSERVAR";
                    item.Reason = "Fecha fuera del rango a borrar (" + winDesc + ")";
                    res.KeepCount++;
                    res.Items.Add(item);
                    continue;
                }

                // 3. Retention / Protection Rules ("Solo deja tal")
                if (keepExts.Count > 0)
                {
                    string ext = (fi.Extension ?? "").ToLowerInvariant();
                    if (keepExts.Contains(ext))
                    {
                        item.Action = "CONSERVAR";
                        item.Reason = "Protegido por extension excluida (" + ext + ")";
                        res.KeepCount++;
                        res.Items.Add(item);
                        continue;
                    }
                }
                if (keepPats.Count > 0)
                {
                    string patMatch;
                    if (MatchesAnyPattern(fi.Name, keepPats, out patMatch))
                    {
                        item.Action = "CONSERVAR";
                        item.Reason = "Protegido por regla 'Solo deja nombre' (" + patMatch + ")";
                        res.KeepCount++;
                        res.Items.Add(item);
                        continue;
                    }
                }
                if (hasKeepRange && fTime >= keepFrom && fTime <= keepTo)
                {
                    item.Action = "CONSERVAR";
                    item.Reason = String.Format("Protegido por rango de fechas a conservar ({0} a {1})", task.KeepDateFrom, task.KeepDateTo);
                    res.KeepCount++;
                    res.Items.Add(item);
                    continue;
                }
                if (task.KeepFirstOfMonth && fTime.Day == 1)
                {
                    item.Action = "CONSERVAR";
                    item.Reason = "Protegido: Conservar archivos del dia 1 de cada mes";
                    res.KeepCount++;
                    res.Items.Add(item);
                    continue;
                }
                if (task.KeepLastOfMonth && fTime.Day == DateTime.DaysInMonth(fTime.Year, fTime.Month))
                {
                    item.Action = "CONSERVAR";
                    item.Reason = "Protegido: Conservar archivos del ultimo dia del mes (cierre)";
                    res.KeepCount++;
                    res.Items.Add(item);
                    continue;
                }
                if (keepDays.Contains(fTime.Day))
                {
                    item.Action = "CONSERVAR";
                    item.Reason = String.Format("Protegido: Conservar archivos del dia {0} del mes", fTime.Day);
                    res.KeepCount++;
                    res.Items.Add(item);
                    continue;
                }
                if (task.KeepRecentDays > 0 && fTime > now.AddDays(-task.KeepRecentDays))
                {
                    item.Action = "CONSERVAR";
                    item.Reason = String.Format("Protegido: Menos de {0} dias de antiguedad", task.KeepRecentDays);
                    res.KeepCount++;
                    res.Items.Add(item);
                    continue;
                }
                if (newestProtected.Contains(fi.FullName))
                {
                    item.Action = "CONSERVAR";
                    item.Reason = String.Format("Protegido: Entre los {0} archivos mas recientes", task.KeepNewestCount);
                    res.KeepCount++;
                    res.Items.Add(item);
                    continue;
                }

                // File qualifies for deletion
                if (dryRun)
                {
                    item.Action = "BORRAR";
                    item.Reason = "Se borrara (cumple fecha " + winDesc + ")";
                    res.DeleteCount++;
                    res.BytesFreed += fi.Length;
                }
                else
                {
                    try
                    {
                        File.SetAttributes(fi.FullName, FileAttributes.Normal);
                        File.Delete(fi.FullName);
                        item.Action = "BORRADO";
                        item.Reason = "Borrado exitosamente (" + winDesc + ")";
                        res.DeleteCount++;
                        res.BytesFreed += fi.Length;
                    }
                    catch (Exception ex)
                    {
                        item.Action = "ERROR";
                        item.Reason = "No se pudo borrar: " + ex.Message;
                        res.ErrorCount++;
                    }
                }
                res.Items.Add(item);
            }

            if (!dryRun && task.IncludeSubfolders && task.RemoveEmptyFolders)
            {
                RemoveEmptySubdirectories(task.FolderPath);
            }

            if (dryRun)
            {
                res.SummaryMessage = String.Format("SIMULACION: {0} archivos se borraran ({1}) y {2} archivos se conservaran intactos.",
                    res.DeleteCount, FormatBytes(res.BytesFreed), res.KeepCount);
            }
            else
            {
                res.SummaryMessage = String.Format("EJECUCION REAL: {0} archivos borrados ({1} liberados), {2} conservados, {3} errores.",
                    res.DeleteCount, FormatBytes(res.BytesFreed), res.KeepCount, res.ErrorCount);
            }

            WriteAuditLog(task, dryRun, res);
            return res;
        }

        private static void ComputeDateWindow(DeleteTask task, DateTime now, out DateTime start, out DateTime end, out string desc)
        {
            if (task.DateRuleType == "PREV_MONTH")
            {
                DateTime firstCurrent = new DateTime(now.Year, now.Month, 1);
                DateTime lastPrev = firstCurrent.AddDays(-1);
                int maxDay = lastPrev.Day;
                int dFrom = Math.Max(1, Math.Min(maxDay, task.MonthDayFrom <= 0 ? 1 : task.MonthDayFrom));
                int dTo = task.MonthDayTo <= 0 ? maxDay : Math.Max(dFrom, Math.Min(maxDay, task.MonthDayTo));
                start = new DateTime(lastPrev.Year, lastPrev.Month, dFrom, 0, 0, 0);
                end = new DateTime(lastPrev.Year, lastPrev.Month, dTo, 23, 59, 59);
                desc = String.Format("Mes anterior ({0:yyyy-MM-dd} al {1:yyyy-MM-dd})", start, end);
                return;
            }
            if (task.DateRuleType == "CURR_MONTH")
            {
                int maxDay = DateTime.DaysInMonth(now.Year, now.Month);
                int dFrom = Math.Max(1, Math.Min(maxDay, task.MonthDayFrom <= 0 ? 1 : task.MonthDayFrom));
                int dTo = task.MonthDayTo <= 0 ? maxDay : Math.Max(dFrom, Math.Min(maxDay, task.MonthDayTo));
                start = new DateTime(now.Year, now.Month, dFrom, 0, 0, 0);
                end = new DateTime(now.Year, now.Month, dTo, 23, 59, 59);
                desc = String.Format("Mes actual ({0:yyyy-MM-dd} al {1:yyyy-MM-dd})", start, end);
                return;
            }
            if (task.DateRuleType == "DATE_RANGE")
            {
                DateTime t1, t2;
                if (DateTime.TryParseExact(task.DateFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out t1) &&
                    DateTime.TryParseExact(task.DateTo, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out t2))
                {
                    start = t1.Date;
                    end = t2.Date.AddDays(1).AddTicks(-1);
                    desc = String.Format("Desde {0:yyyy-MM-dd} hasta {1:yyyy-MM-dd}", start, end);
                    return;
                }
            }
            if (task.DateRuleType == "OLDER_DAYS")
            {
                int days = Math.Max(0, task.OlderThanDays);
                start = new DateTime(1970, 1, 1);
                end = now.AddDays(-days);
                desc = String.Format("Mas antiguos que {0:yyyy-MM-dd HH:mm} (> {1} dias)", end, days);
                return;
            }

            start = new DateTime(1970, 1, 1);
            end = new DateTime(2100, 12, 31, 23, 59, 59);
            desc = "Todas las fechas";
        }

        private static List<string> ParseCsv(string raw, bool ensureDotPrefix)
        {
            List<string> list = new List<string>();
            if (String.IsNullOrWhiteSpace(raw)) return list;
            foreach (string p in raw.Split(','))
            {
                string s = p.Trim().ToLowerInvariant();
                if (s.Length == 0) continue;
                if (ensureDotPrefix && !s.StartsWith(".")) s = "." + s;
                list.Add(s);
            }
            return list;
        }

        private static HashSet<int> ParseDays(string raw)
        {
            HashSet<int> set = new HashSet<int>();
            if (String.IsNullOrWhiteSpace(raw)) return set;
            foreach (string p in raw.Split(','))
            {
                int d;
                if ( int.TryParse(p.Trim(), out d) && d >= 1 && d <= 31) set.Add(d);
            }
            return set;
        }

        private static bool MatchesAnyPattern(string fileName, List<string> patterns, out string matched)
        {
            string lowName = fileName.ToLowerInvariant();
            foreach (string pat in patterns)
            {
                if (pat.Contains("*") || pat.Contains("?"))
                {
                    string regexPattern = "^" + Regex.Escape(pat).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                    if (Regex.IsMatch(lowName, regexPattern, RegexOptions.IgnoreCase))
                    {
                        matched = pat;
                        return true;
                    }
                }
                else if (lowName.Contains(pat))
                {
                    matched = pat;
                    return true;
                }
            }
            matched = "";
            return false;
        }

        private static void RemoveEmptySubdirectories(string root)
        {
            try
            {
                foreach (string d in Directory.GetDirectories(root))
                {
                    RemoveEmptySubdirectories(d);
                    if (Directory.GetFiles(d).Length == 0 && Directory.GetDirectories(d).Length == 0)
                    {
                        Directory.Delete(d, false);
                    }
                }
            }
            catch { }
        }

        private static void WriteAuditLog(DeleteTask task, bool dryRun, ExecutionResult res)
        {
            try
            {
                string mode = dryRun ? "SIMULACION" : "BORRADO_REAL";
                string fileName = String.Format("{0:yyyy-MM-dd_HHmmss}_{1}_{2}.log", DateTime.Now, mode, SanitizeName(task.Name));
                string fullPath = Path.Combine(LogsDir, fileName);

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("=========================================================================");
                sb.AppendLine("AutoCleaner Server - Registro de " + mode);
                sb.AppendLine("Tarea:          " + task.Name + " (" + task.ID + ")");
                sb.AppendLine("Carpeta:        " + task.FolderPath);
                sb.AppendLine("Fecha Ejec.:    " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("Ventana Fechas: " + res.WindowDescription);
                sb.AppendLine("Resumen:        " + res.SummaryMessage);
                sb.AppendLine("=========================================================================");
                sb.AppendLine();
                foreach (FileEvalItem it in res.Items)
                {
                    sb.AppendLine(String.Format("[{0,-9}] {1:yyyy-MM-dd HH:mm} | {2,10} | {3,-38} | {4}",
                        it.Action, it.FileTime, FormatBytes(it.SizeBytes), it.RelativePath, it.Reason));
                }
                File.WriteAllText(fullPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        public static string SyncWithWindowsTaskScheduler(DeleteTask task)
        {
            string exePath = Application.ExecutablePath;
            string taskWinName = String.Format("\\AutoCleanerServer\\{0}_{1}", SanitizeName(task.Name), task.ID);

            if (!task.Enabled || !task.SyncWindowsTask || task.ScheduleType == "MANUAL")
            {
                RunSchtasks(String.Format("/Delete /TN \"{0}\" /F", taskWinName));
                return "Desvinculado del Programador de Tareas de Windows";
            }

            string runTime = String.IsNullOrWhiteSpace(task.RunTime) ? "02:00" : task.RunTime.Trim();
            string tr = String.Format("\\\"{0}\\\" --run-task {1}", exePath, task.ID);
            string scPart = "";

            if (task.ScheduleType == "MONTHLY")
            {
                if (task.DayOfMonth <= 0 || task.DayOfMonth > 31)
                    scPart = "/SC MONTHLY /M * /MO LASTDAY";
                else
                    scPart = String.Format("/SC MONTHLY /D {0}", task.DayOfMonth);
            }
            else if (task.ScheduleType == "WEEKLY")
            {
                string[] days = new string[] { "SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT" };
                int d = Math.Max(0, Math.Min(6, task.DayOfWeek));
                scPart = "/SC WEEKLY /D " + days[d];
            }
            else
            {
                scPart = "/SC DAILY";
            }

            // Try registering as SYSTEM first (runs even when logged off or rebooted)
            string argsSystem = String.Format("/Create /F /TN \"{0}\" /TR \"{1}\" {2} /ST {3} /RU SYSTEM /RL HIGHEST",
                taskWinName, tr, scPart, runTime);
            if (RunSchtasks(argsSystem))
            {
                return "Registrado en Windows Server como SYSTEM (corre 24/7 aun con sesion cerrada)";
            }

            // Fallback to current user
            string argsUser = String.Format("/Create /F /TN \"{0}\" /TR \"{1}\" {2} /ST {3}",
                taskWinName, tr, scPart, runTime);
            if (RunSchtasks(argsUser))
            {
                return "Registrado en Programador de Tareas de Windows (usuario actual)";
            }

            return "Guardado localmente (ejecuta como Administrador para sincronizar con SYSTEM)";
        }

        public static void DeleteFromWindowsTaskScheduler(DeleteTask task)
        {
            string taskWinName = String.Format("\\AutoCleanerServer\\{0}_{1}", SanitizeName(task.Name), task.ID);
            RunSchtasks(String.Format("/Delete /TN \"{0}\" /F", taskWinName));
        }

        private static bool RunSchtasks(string arguments)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe", arguments);
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit(5000);
                    return p.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        public static string SanitizeName(string s)
        {
            if (String.IsNullOrWhiteSpace(s)) return "Tarea";
            StringBuilder sb = new StringBuilder();
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-') sb.Append(c);
                else if (c == ' ') sb.Append('_');
            }
            return sb.Length > 0 ? sb.ToString() : "Tarea";
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return kb.ToString("0.0") + " KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return mb.ToString("0.00") + " MB";
            double gb = mb / 1024.0;
            return gb.ToString("0.00") + " GB";
        }

        public static string CreateDemoFolder(DateTime now)
        {
            string demoDir = Path.Combine(BaseDir, "Carpeta_Prueba_Demo");
            if (!Directory.Exists(demoDir)) Directory.CreateDirectory(demoDir);

            DateTime firstCurr = new DateTime(now.Year, now.Month, 1);
            DateTime lastPrev = firstCurr.AddDays(-1);
            int py = lastPrev.Year;
            int pm = lastPrev.Month;

            var specs = new[]
            {
                new { Name = String.Format("backup_{0:0000}_{1:00}_01_apertura.bak", py, pm), Date = new DateTime(py, pm, 1, 8, 30, 0), Size = 20480 },
                new { Name = String.Format("log_transacciones_{0:0000}_{1:00}_05.log", py, pm), Date = new DateTime(py, pm, 5, 14, 15, 0), Size = 15360 },
                new { Name = String.Format("log_transacciones_{0:0000}_{1:00}_12.log", py, pm), Date = new DateTime(py, pm, 12, 11, 0, 0), Size = 45056 },
                new { Name = String.Format("reporte_quincenal_{0:0000}_{1:00}_15_IMPORTANTE.xlsx", py, pm), Date = new DateTime(py, pm, 15, 17, 45, 0), Size = 32768 },
                new { Name = String.Format("temporal_sistema_{0:0000}_{1:00}_20.tmp", py, pm), Date = new DateTime(py, pm, 20, 9, 20, 0), Size = 61440 },
                new { Name = String.Format("backup_diario_{0:0000}_{1:00}_25.bak", py, pm), Date = new DateTime(py, pm, 25, 22, 0, 0), Size = 51200 },
                new { Name = String.Format("cierre_mensual_{0:0000}_{1:00}_{2:00}.bak", py, pm, lastPrev.Day), Date = new DateTime(py, pm, lastPrev.Day, 23, 30, 0), Size = 81920 },
                new { Name = String.Format("operaciones_mes_actual_{0:yyyy_MM_dd}.log", now), Date = now.AddHours(-2), Size = 12288 }
            };

            foreach (var s in specs)
            {
                string p = Path.Combine(demoDir, s.Name);
                byte[] data = new byte[s.Size];
                File.WriteAllBytes(p, data);
                File.SetCreationTime(p, s.Date);
                File.SetLastWriteTime(p, s.Date);
            }

            return demoDir;
        }
    }

    public class MainForm : Form
    {
        private List<DeleteTask> tasks = new List<DeleteTask>();
        private string editingTaskId = null;

        // Top controls
        private DataGridView gridTasks;
        private Label lblRuleSummary;

        // Editor controls
        private TextBox txtName;
        private TextBox txtFolder;
        private CheckBox chkEnabled;
        private CheckBox chkSubfolders;
        private CheckBox chkRemoveEmpty;

        private ComboBox cmbScheduleType;
        private NumericUpDown numDayOfMonth;
        private CheckBox chkLastDaySchedule;
        private ComboBox cmbDayOfWeek;
        private TextBox txtRunTime;
        private CheckBox chkSyncWinTask;

        private ComboBox cmbDateRuleType;
        private ComboBox cmbDateField;
        private NumericUpDown numMonthDayFrom;
        private NumericUpDown numMonthDayTo;
        private DateTimePicker dtpDateFrom;
        private DateTimePicker dtpDateTo;
        private NumericUpDown numOlderDays;

        private TextBox txtTargetExts;
        private TextBox txtTargetNames;

        private TextBox txtKeepNames;
        private TextBox txtKeepExts;
        private TextBox txtKeepDays;
        private CheckBox chkKeepFirstDay;
        private CheckBox chkKeepLastDay;
        private NumericUpDown numKeepNewest;
        private NumericUpDown numKeepRecentDays;
        private CheckBox chkUseKeepDates;
        private DateTimePicker dtpKeepFrom;
        private DateTimePicker dtpKeepTo;

        // Bottom Simulation / Results grid
        private Label lblSimStatus;
        private DataGridView gridPreview;

        public MainForm()
        {
            Text = "AutoCleaner Server v2.0 — Programador Nativo de Borrado para Windows Server";
            Size = new Size(1240, 860);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular);
            BackColor = Color.FromArgb(245, 247, 250);

            InitializeLayout();
            LoadTasksIntoGrid();
            ResetEditorForNewTask();
        }

        private void InitializeLayout()
        {
            // Header Banner
            Panel header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 56,
                BackColor = Color.FromArgb(15, 23, 42),
                Padding = new Padding(14, 8, 14, 8)
            };
            Label title = new Label
            {
                Text = "AutoCleaner Server — Gestor de Tareas de Borrado (Windows Server)",
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 13F, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(16, 8)
            };
            Label sub = new Label
            {
                Text = "Ejecucion automatica en segundo plano integrada con el Programador de Tareas de Windows (schtasks.exe)",
                ForeColor = Color.FromArgb(148, 163, 184),
                Font = new Font("Segoe UI", 8.5F),
                AutoSize = true,
                Location = new Point(18, 32)
            };
            Button btnDemo = new Button
            {
                Text = "Crear Carpeta Demo + Ejemplo Dia 1 del Mes",
                BackColor = Color.FromArgb(245, 158, 11),
                ForeColor = Color.Black,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Size = new Size(300, 34),
                Location = new Point(720, 11),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            btnDemo.Click += delegate { CreateAndLoadDemoScenario(); };

            Button btnOpenLogs = new Button
            {
                Text = "Abrir Carpeta de Logs",
                BackColor = Color.FromArgb(51, 65, 85),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(165, 34),
                Location = new Point(1035, 11),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            btnOpenLogs.Click += delegate
            {
                try { Process.Start("explorer.exe", CleanerEngine.LogsDir); } catch { }
            };

            header.Controls.Add(title);
            header.Controls.Add(sub);
            header.Controls.Add(btnDemo);
            header.Controls.Add(btnOpenLogs);

            // Main container panel
            Panel mainPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12), AutoScroll = true };

            // 1. Saved Tasks Group
            GroupBox grpTasks = new GroupBox
            {
                Text = "1. Tareas Programadas Activas (Selecciona una para editar, simular o ejecutar)",
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Dock = DockStyle.Top,
                Height = 165,
                Padding = new Padding(8)
            };

            gridTasks = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = Color.White,
                RowHeadersVisible = false,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular)
            };
            gridTasks.Columns.Add("Name", "Nombre de Tarea");
            gridTasks.Columns.Add("Folder", "Carpeta Objetivo");
            gridTasks.Columns.Add("Schedule", "Programacion");
            gridTasks.Columns.Add("DateRule", "Fechas a Borrar");
            gridTasks.Columns.Add("KeepRule", "Reglas 'Solo Deja Tal'");
            gridTasks.Columns.Add("LastRun", "Ultima Ejecucion");
            gridTasks.SelectionChanged += delegate { OnTaskGridSelected(); };

            Panel taskBtnsPanel = new Panel { Dock = DockStyle.Right, Width = 175, Padding = new Padding(6, 0, 0, 0) };
            Button btnNewTask = CreateButton("Nueva Tarea Limpia", Color.FromArgb(37, 99, 235), Color.White, DockStyle.Top);
            btnNewTask.Click += delegate { ResetEditorForNewTask(); };

            Button btnRunReal = CreateButton("Ejecutar Borrado Real", Color.FromArgb(16, 185, 129), Color.White, DockStyle.Top);
            btnRunReal.Click += delegate { RunSelectedOrCurrent(false); };

            Button btnDeleteTask = CreateButton("Eliminar Tarea", Color.FromArgb(220, 38, 38), Color.White, DockStyle.Top);
            btnDeleteTask.Click += delegate { DeleteSelectedTask(); };

            taskBtnsPanel.Controls.Add(btnDeleteTask);
            taskBtnsPanel.Controls.Add(btnRunReal);
            taskBtnsPanel.Controls.Add(btnNewTask);

            grpTasks.Controls.Add(gridTasks);
            grpTasks.Controls.Add(taskBtnsPanel);

            // 2. Editor Group
            GroupBox grpEditor = new GroupBox
            {
                Text = "2. Configurar / Editar Regla de Borrado y Retencion",
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Dock = DockStyle.Top,
                Height = 365,
                Padding = new Padding(10)
            };

            lblRuleSummary = new Label
            {
                Dock = DockStyle.Top,
                Height = 36,
                BackColor = Color.FromArgb(219, 234, 254),
                ForeColor = Color.FromArgb(30, 58, 138),
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 8, 0),
                Text = "Resumen de la regla..."
            };

            Panel formArea = new Panel { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9F, FontStyle.Regular) };

            // Row 1: Name & Folder
            AddLabel(formArea, "Nombre de Tarea:", 10, 12);
            txtName = new TextBox { Location = new Point(125, 9), Width = 250 };
            txtName.TextChanged += delegate { UpdateRuleSummaryBanner(); };
            formArea.Controls.Add(txtName);

            AddLabel(formArea, "Carpeta a Limpiar:", 390, 12);
            txtFolder = new TextBox { Location = new Point(505, 9), Width = 430 };
            txtFolder.TextChanged += delegate { UpdateRuleSummaryBanner(); };
            formArea.Controls.Add(txtFolder);

            Button btnBrowse = new Button
            {
                Text = "Examinar...",
                Location = new Point(942, 7),
                Size = new Size(95, 26),
                BackColor = Color.FromArgb(226, 232, 240)
            };
            btnBrowse.Click += delegate
            {
                using (FolderBrowserDialog fbd = new FolderBrowserDialog())
                {
                    fbd.Description = "Selecciona la carpeta del Windows Server que deseas limpiar";
                    if (Directory.Exists(txtFolder.Text)) fbd.SelectedPath = txtFolder.Text;
                    if (fbd.ShowDialog() == DialogResult.OK) txtFolder.Text = fbd.SelectedPath;
                }
            };
            formArea.Controls.Add(btnBrowse);

            chkEnabled = new CheckBox { Text = "Tarea Activa", Location = new Point(1050, 10), AutoSize = true, Checked = true };
            formArea.Controls.Add(chkEnabled);

            // Row 2: Schedule
            AddLabel(formArea, "Cuando ejecutar:", 10, 44);
            cmbScheduleType = new ComboBox { Location = new Point(125, 41), Width = 180, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbScheduleType.Items.AddRange(new object[] {
                "MONTHLY - Mensual",
                "WEEKLY - Semanal",
                "DAILY - Diario",
                "MANUAL - Solo Manual"
            });
            cmbScheduleType.SelectedIndex = 0;
            cmbScheduleType.SelectedIndexChanged += delegate { UpdateRuleSummaryBanner(); };
            formArea.Controls.Add(cmbScheduleType);

            AddLabel(formArea, "Dia del mes:", 315, 44);
            numDayOfMonth = new NumericUpDown { Location = new Point(395, 41), Width = 50, Minimum = 1, Maximum = 31, Value = 1 };
            numDayOfMonth.ValueChanged += delegate { UpdateRuleSummaryBanner(); };
            formArea.Controls.Add(numDayOfMonth);

            chkLastDaySchedule = new CheckBox { Text = "Ultimo dia del mes", Location = new Point(455, 43), AutoSize = true };
            chkLastDaySchedule.CheckedChanged += delegate
            {
                numDayOfMonth.Enabled = !chkLastDaySchedule.Checked;
                UpdateRuleSummaryBanner();
            };
            formArea.Controls.Add(chkLastDaySchedule);

            AddLabel(formArea, "Dia sem:", 595, 44);
            cmbDayOfWeek = new ComboBox { Location = new Point(650, 41), Width = 95, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbDayOfWeek.Items.AddRange(new object[] { "Domingo", "Lunes", "Martes", "Miercoles", "Jueves", "Viernes", "Sabado" });
            cmbDayOfWeek.SelectedIndex = 1;
            formArea.Controls.Add(cmbDayOfWeek);

            AddLabel(formArea, "Hora (24h):", 755, 44);
            txtRunTime = new TextBox { Location = new Point(830, 41), Width = 58, Text = "02:00" };
            txtRunTime.TextChanged += delegate { UpdateRuleSummaryBanner(); };
            formArea.Controls.Add(txtRunTime);

            chkSyncWinTask = new CheckBox
            {
                Text = "Registrar en Programador de Windows (schtasks)",
                Location = new Point(900, 43),
                AutoSize = true,
                Checked = true
            };
            formArea.Controls.Add(chkSyncWinTask);

            // Row 3: Date Rule ("Archivos que tengan fecha tal hasta tal")
            AddLabel(formArea, "Fechas a BORRAR:", 10, 78);
            cmbDateRuleType = new ComboBox { Location = new Point(125, 75), Width = 250, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbDateRuleType.Items.AddRange(new object[] {
                "PREV_MONTH - Mes calendario anterior",
                "DATE_RANGE - Rango de fechas fijas",
                "OLDER_DAYS - Mas antiguos que X dias",
                "CURR_MONTH - Dias del mes actual",
                "ALL_DATES - Cualquier fecha"
            });
            cmbDateRuleType.SelectedIndex = 0;
            cmbDateRuleType.SelectedIndexChanged += delegate { UpdateRuleSummaryBanner(); };
            formArea.Controls.Add(cmbDateRuleType);

            AddLabel(formArea, "Del dia:", 385, 78);
            numMonthDayFrom = new NumericUpDown { Location = new Point(438, 75), Width = 46, Minimum = 1, Maximum = 31, Value = 1 };
            numMonthDayFrom.ValueChanged += delegate { UpdateRuleSummaryBanner(); };
            formArea.Controls.Add(numMonthDayFrom);

            AddLabel(formArea, "Al dia:", 490, 78);
            numMonthDayTo = new NumericUpDown { Location = new Point(535, 75), Width = 46, Minimum = 1, Maximum = 31, Value = 31 };
            numMonthDayTo.ValueChanged += delegate { UpdateRuleSummaryBanner(); };
            formArea.Controls.Add(numMonthDayTo);

            AddLabel(formArea, "O Rango Desde:", 590, 78);
            dtpDateFrom = new DateTimePicker { Location = new Point(690, 75), Width = 105, Format = DateTimePickerFormat.Short };
            dtpDateFrom.ValueChanged += delegate { UpdateRuleSummaryBanner(); };
            formArea.Controls.Add(dtpDateFrom);

            AddLabel(formArea, "Hasta:", 800, 78);
            dtpDateTo = new DateTimePicker { Location = new Point(845, 75), Width = 105, Format = DateTimePickerFormat.Short };
            dtpDateTo.ValueChanged += delegate { UpdateRuleSummaryBanner(); };
            formArea.Controls.Add(dtpDateTo);

            AddLabel(formArea, "> Dias:", 958, 78);
            numOlderDays = new NumericUpDown { Location = new Point(1005, 75), Width = 55, Minimum = 1, Maximum = 3650, Value = 30 };
            numOlderDays.ValueChanged += delegate { UpdateRuleSummaryBanner(); };
            formArea.Controls.Add(numOlderDays);

            // Row 4: Inclusion filters & Subfolders
            AddLabel(formArea, "Borrar solo ext.:", 10, 112);
            txtTargetExts = new TextBox { Location = new Point(125, 109), Width = 170 };
            txtTargetExts.TextChanged += delegate { UpdateRuleSummaryBanner(); };
            formArea.Controls.Add(txtTargetExts);
            AddLabel(formArea, "(ej: .log, .bak o vacio=todas)", 300, 112);

            AddLabel(formArea, "Borrar nombres:", 475, 112);
            txtTargetNames = new TextBox { Location = new Point(575, 109), Width = 165 };
            txtTargetNames.TextChanged += delegate { UpdateRuleSummaryBanner(); };
            formArea.Controls.Add(txtTargetNames);

            AddLabel(formArea, "Evaluar:", 750, 112);
            cmbDateField = new ComboBox { Location = new Point(805, 109), Width = 135, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbDateField.Items.AddRange(new object[] { "MODIFIED - Modificacion", "CREATED - Creacion" });
            cmbDateField.SelectedIndex = 0;
            formArea.Controls.Add(cmbDateField);

            chkSubfolders = new CheckBox { Text = "Incluir subcarpetas", Location = new Point(950, 111), AutoSize = true, Checked = true };
            formArea.Controls.Add(chkSubfolders);

            chkRemoveEmpty = new CheckBox { Text = "Borrar carpetas vacias", Location = new Point(1080, 111), AutoSize = true };
            formArea.Controls.Add(chkRemoveEmpty);

            // Row 5 & 6: Retention Box ("Y SOLO DEJA TAL")
            GroupBox grpKeep = new GroupBox
            {
                Text = "Reglas de Proteccion — 'Y SOLO DEJA TAL' (Archivos que NUNCA se borraran aunque esten en la fecha)",
                Location = new Point(10, 142),
                Size = new Size(1170, 115),
                ForeColor = Color.FromArgb(6, 95, 70)
            };

            AddLabel(grpKeep, "Dejar nombres/patron:", 12, 26);
            txtKeepNames = new TextBox { Location = new Point(150, 23), Width = 210 };
            txtKeepNames.TextChanged += delegate { UpdateRuleSummaryBanner(); };
            grpKeep.Controls.Add(txtKeepNames);

            AddLabel(grpKeep, "Dejar extensiones:", 375, 26);
            txtKeepExts = new TextBox { Location = new Point(490, 23), Width = 135 };
            txtKeepExts.TextChanged += delegate { UpdateRuleSummaryBanner(); };
            grpKeep.Controls.Add(txtKeepExts);

            AddLabel(grpKeep, "Dejar dias del mes (ej 1,15):", 640, 26);
            txtKeepDays = new TextBox { Location = new Point(810, 23), Width = 100 };
            txtKeepDays.TextChanged += delegate { UpdateRuleSummaryBanner(); };
            grpKeep.Controls.Add(txtKeepDays);

            AddLabel(grpKeep, "Dejar N mas nuevos:", 925, 26);
            numKeepNewest = new NumericUpDown { Location = new Point(1050, 23), Width = 55, Minimum = 0, Maximum = 1000, Value = 0 };
            numKeepNewest.ValueChanged += delegate { UpdateRuleSummaryBanner(); };
            grpKeep.Controls.Add(numKeepNewest);

            chkKeepFirstDay = new CheckBox { Text = "Dejar siempre el dia 1 de cada mes", Location = new Point(15, 58), AutoSize = true };
            chkKeepFirstDay.CheckedChanged += delegate { UpdateRuleSummaryBanner(); };
            grpKeep.Controls.Add(chkKeepFirstDay);

            chkKeepLastDay = new CheckBox { Text = "Dejar siempre el ultimo dia del mes (Cierre)", Location = new Point(255, 58), AutoSize = true, Checked = true };
            chkKeepLastDay.CheckedChanged += delegate { UpdateRuleSummaryBanner(); };
            grpKeep.Controls.Add(chkKeepLastDay);

            AddLabel(grpKeep, "Proteger ultimos X dias:", 545, 59);
            numKeepRecentDays = new NumericUpDown { Location = new Point(690, 56), Width = 50, Minimum = 0, Maximum = 3650, Value = 0 };
            numKeepRecentDays.ValueChanged += delegate { UpdateRuleSummaryBanner(); };
            grpKeep.Controls.Add(numKeepRecentDays);

            chkUseKeepDates = new CheckBox { Text = "Dejar rango fechas:", Location = new Point(755, 58), AutoSize = true };
            chkUseKeepDates.CheckedChanged += delegate { UpdateRuleSummaryBanner(); };
            grpKeep.Controls.Add(chkUseKeepDates);

            dtpKeepFrom = new DateTimePicker { Location = new Point(895, 55), Width = 100, Format = DateTimePickerFormat.Short };
            grpKeep.Controls.Add(dtpKeepFrom);
            dtpKeepTo = new DateTimePicker { Location = new Point(1005, 55), Width = 100, Format = DateTimePickerFormat.Short };
            grpKeep.Controls.Add(dtpKeepTo);

            formArea.Controls.Add(grpKeep);

            // Row 7: Action Buttons
            Button btnSimulate = new Button
            {
                Text = "Simular Ahora (Dry Run - Ver que se borrara y que se dejara sin borrar nada)",
                Location = new Point(10, 266),
                Size = new Size(520, 34),
                BackColor = Color.FromArgb(245, 158, 11),
                ForeColor = Color.Black,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold)
            };
            btnSimulate.Click += delegate { RunSimulationFromEditor(); };

            Button btnSaveTask = new Button
            {
                Text = "Guardar y Programar Tarea en Windows Server",
                Location = new Point(545, 266),
                Size = new Size(360, 34),
                BackColor = Color.FromArgb(16, 185, 129),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold)
            };
            btnSaveTask.Click += delegate { SaveCurrentTask(); };

            formArea.Controls.Add(btnSimulate);
            formArea.Controls.Add(btnSaveTask);

            grpEditor.Controls.Add(formArea);
            grpEditor.Controls.Add(lblRuleSummary);

            // 3. Preview / Simulation Output Group
            GroupBox grpPreview = new GroupBox
            {
                Text = "3. Vista Previa de Simulacion (Dry Run) y Resultado Archivo por Archivo",
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Dock = DockStyle.Fill,
                Padding = new Padding(8)
            };

            lblSimStatus = new Label
            {
                Dock = DockStyle.Top,
                Height = 26,
                Text = "Pulsa 'Simular Ahora (Dry Run)' para previsualizar exactamente que archivos se borraran y cuales se conservaran.",
                ForeColor = Color.FromArgb(30, 64, 175),
                Font = new Font("Segoe UI", 9F, FontStyle.Bold)
            };

            gridPreview = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = Color.White,
                RowHeadersVisible = false,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular)
            };
            gridPreview.Columns.Add("Action", "Decision");
            gridPreview.Columns.Add("File", "Archivo");
            gridPreview.Columns.Add("Date", "Fecha del Archivo");
            gridPreview.Columns.Add("Size", "Tamano");
            gridPreview.Columns.Add("Reason", "Motivo / Regla Aplicada");
            gridPreview.Columns["Action"].FillWeight = 14;
            gridPreview.Columns["File"].FillWeight = 28;
            gridPreview.Columns["Date"].FillWeight = 16;
            gridPreview.Columns["Size"].FillWeight = 10;
            gridPreview.Columns["Reason"].FillWeight = 32;

            grpPreview.Controls.Add(gridPreview);
            grpPreview.Controls.Add(lblSimStatus);

            mainPanel.Controls.Add(grpPreview);
            mainPanel.Controls.Add(grpEditor);
            mainPanel.Controls.Add(grpTasks);

            Controls.Add(mainPanel);
            Controls.Add(header);
        }

        private void AddLabel(Control parent, string text, int x, int y)
        {
            Label l = new Label
            {
                Text = text,
                Location = new Point(x, y),
                AutoSize = true,
                ForeColor = Color.FromArgb(51, 65, 85),
                Font = new Font("Segoe UI", 8.8F, FontStyle.Regular)
            };
            parent.Controls.Add(l);
        }

        private Button CreateButton(string text, Color bg, Color fg, DockStyle dock)
        {
            Button b = new Button
            {
                Text = text,
                BackColor = bg,
                ForeColor = fg,
                FlatStyle = FlatStyle.Flat,
                Dock = dock,
                Height = 36,
                Font = new Font("Segoe UI", 8.8F, FontStyle.Bold)
            };
            return b;
        }

        private DeleteTask BuildTaskFromForm()
        {
            DeleteTask t = new DeleteTask();
            t.ID = String.IsNullOrWhiteSpace(editingTaskId) ? ("T_" + DateTime.Now.Ticks.ToString()) : editingTaskId;
            t.Name = String.IsNullOrWhiteSpace(txtName.Text) ? "Tarea de Borrado" : txtName.Text.Trim();
            t.FolderPath = txtFolder.Text.Trim();
            t.Enabled = chkEnabled.Checked;
            t.IncludeSubfolders = chkSubfolders.Checked;
            t.RemoveEmptyFolders = chkRemoveEmpty.Checked;

            string schedRaw = (cmbScheduleType.SelectedItem ?? "MONTHLY").ToString();
            t.ScheduleType = schedRaw.Split(' ')[0];
            t.DayOfMonth = chkLastDaySchedule.Checked ? 0 : (int)numDayOfMonth.Value;
            t.DayOfWeek = cmbDayOfWeek.SelectedIndex;
            t.RunTime = String.IsNullOrWhiteSpace(txtRunTime.Text) ? "02:00" : txtRunTime.Text.Trim();
            t.SyncWindowsTask = chkSyncWinTask.Checked;

            string dateRuleRaw = (cmbDateRuleType.SelectedItem ?? "PREV_MONTH").ToString();
            t.DateRuleType = dateRuleRaw.Split(' ')[0];
            string fieldRaw = (cmbDateField.SelectedItem ?? "MODIFIED").ToString();
            t.DateField = fieldRaw.Split(' ')[0];

            t.MonthDayFrom = (int)numMonthDayFrom.Value;
            t.MonthDayTo = (int)numMonthDayTo.Value;
            t.DateFrom = dtpDateFrom.Value.ToString("yyyy-MM-dd");
            t.DateTo = dtpDateTo.Value.ToString("yyyy-MM-dd");
            t.OlderThanDays = (int)numOlderDays.Value;

            t.TargetExtensions = txtTargetExts.Text.Trim();
            t.TargetNamePatterns = txtTargetNames.Text.Trim();

            t.KeepNamePatterns = txtKeepNames.Text.Trim();
            t.KeepExtensions = txtKeepExts.Text.Trim();
            t.KeepDaysOfMonth = txtKeepDays.Text.Trim();
            t.KeepFirstOfMonth = chkKeepFirstDay.Checked;
            t.KeepLastOfMonth = chkKeepLastDay.Checked;
            t.KeepNewestCount = (int)numKeepNewest.Value;
            t.KeepRecentDays = (int)numKeepRecentDays.Value;
            t.UseKeepDateRange = chkUseKeepDates.Checked;
            t.KeepDateFrom = dtpKeepFrom.Value.ToString("yyyy-MM-dd");
            t.KeepDateTo = dtpKeepTo.Value.ToString("yyyy-MM-dd");

            return t;
        }

        private void PopulateFormFromTask(DeleteTask t)
        {
            editingTaskId = t.ID;
            txtName.Text = t.Name;
            txtFolder.Text = t.FolderPath;
            chkEnabled.Checked = t.Enabled;
            chkSubfolders.Checked = t.IncludeSubfolders;
            chkRemoveEmpty.Checked = t.RemoveEmptyFolders;

            SelectComboPrefix(cmbScheduleType, t.ScheduleType);
            if (t.DayOfMonth == 0)
            {
                chkLastDaySchedule.Checked = true;
                numDayOfMonth.Value = 1;
            }
            else
            {
                chkLastDaySchedule.Checked = false;
                numDayOfMonth.Value = Math.Max(1, Math.Min(31, t.DayOfMonth));
            }
            cmbDayOfWeek.SelectedIndex = Math.Max(0, Math.Min(6, t.DayOfWeek));
            txtRunTime.Text = t.RunTime;
            chkSyncWinTask.Checked = t.SyncWindowsTask;

            SelectComboPrefix(cmbDateRuleType, t.DateRuleType);
            SelectComboPrefix(cmbDateField, t.DateField);
            numMonthDayFrom.Value = Math.Max(1, Math.Min(31, t.MonthDayFrom <= 0 ? 1 : t.MonthDayFrom));
            numMonthDayTo.Value = Math.Max(1, Math.Min(31, t.MonthDayTo <= 0 ? 31 : t.MonthDayTo));

            DateTime dt1, dt2;
            if (DateTime.TryParseExact(t.DateFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt1))
                dtpDateFrom.Value = dt1;
            if (DateTime.TryParseExact(t.DateTo, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt2))
                dtpDateTo.Value = dt2;

            numOlderDays.Value = Math.Max(1, Math.Min(3650, t.OlderThanDays <= 0 ? 30 : t.OlderThanDays));
            txtTargetExts.Text = t.TargetExtensions;
            txtTargetNames.Text = t.TargetNamePatterns;

            txtKeepNames.Text = t.KeepNamePatterns;
            txtKeepExts.Text = t.KeepExtensions;
            txtKeepDays.Text = t.KeepDaysOfMonth;
            chkKeepFirstDay.Checked = t.KeepFirstOfMonth;
            chkKeepLastDay.Checked = t.KeepLastOfMonth;
            numKeepNewest.Value = Math.Max(0, Math.Min(1000, t.KeepNewestCount));
            numKeepRecentDays.Value = Math.Max(0, Math.Min(3650, t.KeepRecentDays));
            chkUseKeepDates.Checked = t.UseKeepDateRange;

            DateTime kf, kt;
            if (DateTime.TryParseExact(t.KeepDateFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out kf))
                dtpKeepFrom.Value = kf;
            if (DateTime.TryParseExact(t.KeepDateTo, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out kt))
                dtpKeepTo.Value = kt;

            UpdateRuleSummaryBanner();
        }

        private void SelectComboPrefix(ComboBox cmb, string prefix)
        {
            if (String.IsNullOrWhiteSpace(prefix)) return;
            for (int i = 0; i < cmb.Items.Count; i++)
            {
                if (cmb.Items[i].ToString().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    cmb.SelectedIndex = i;
                    return;
                }
            }
        }

        private void ResetEditorForNewTask()
        {
            PopulateFormFromTask(new DeleteTask());
            editingTaskId = null;
        }

        private void UpdateRuleSummaryBanner()
        {
            DeleteTask t = BuildTaskFromForm();
            lblRuleSummary.Text = String.Format("RESUMEN: [{0}] -> Carpeta: {1} | Borrar fechas: {2} | Solo Deja Intacto: {3}",
                DescribeSchedule(t),
                String.IsNullOrWhiteSpace(t.FolderPath) ? "(Selecciona carpeta)" : t.FolderPath,
                DescribeDateRule(t),
                DescribeKeepRule(t));
        }

        private string DescribeSchedule(DeleteTask t)
        {
            if (t.ScheduleType == "MONTHLY")
                return t.DayOfMonth == 0 ? ("Ultimo dia del mes a las " + t.RunTime) : String.Format("Dia {0} de cada mes a las {1}", t.DayOfMonth, t.RunTime);
            if (t.ScheduleType == "WEEKLY")
                return "Semanal a las " + t.RunTime;
            if (t.ScheduleType == "DAILY")
                return "Diario a las " + t.RunTime;
            return "Manual";
        }

        private string DescribeDateRule(DeleteTask t)
        {
            if (t.DateRuleType == "PREV_MONTH")
                return String.Format("Mes anterior (dia {0} al {1})", t.MonthDayFrom, t.MonthDayTo);
            if (t.DateRuleType == "DATE_RANGE")
                return String.Format("Desde {0} hasta {1}", t.DateFrom, t.DateTo);
            if (t.DateRuleType == "OLDER_DAYS")
                return String.Format("> {0} dias de antiguedad", t.OlderThanDays);
            if (t.DateRuleType == "CURR_MONTH")
                return String.Format("Mes actual (dia {0} al {1})", t.MonthDayFrom, t.MonthDayTo);
            return "Todas las fechas";
        }

        private string DescribeKeepRule(DeleteTask t)
        {
            List<string> p = new List<string>();
            if (!String.IsNullOrWhiteSpace(t.KeepNamePatterns)) p.Add("Nombres[" + t.KeepNamePatterns + "]");
            if (!String.IsNullOrWhiteSpace(t.KeepExtensions)) p.Add("Ext[" + t.KeepExtensions + "]");
            if (t.KeepFirstOfMonth) p.Add("Dia 1 del mes");
            if (t.KeepLastOfMonth) p.Add("Ultimo dia del mes");
            if (!String.IsNullOrWhiteSpace(t.KeepDaysOfMonth)) p.Add("Dias[" + t.KeepDaysOfMonth + "]");
            if (t.KeepNewestCount > 0) p.Add("Ultimos " + t.KeepNewestCount + " archivos");
            if (t.KeepRecentDays > 0) p.Add("Recientes " + t.KeepRecentDays + "d");
            if (t.UseKeepDateRange) p.Add(t.KeepDateFrom + " a " + t.KeepDateTo);
            return p.Count == 0 ? "Sin excepciones" : String.Join(" + ", p.ToArray());
        }

        private void LoadTasksIntoGrid()
        {
            tasks = CleanerEngine.LoadTasks();
            gridTasks.Rows.Clear();
            foreach (DeleteTask t in tasks)
            {
                int idx = gridTasks.Rows.Add(
                    (t.Enabled ? "[ACTIVA] " : "[PAUSADA] ") + t.Name,
                    t.FolderPath,
                    DescribeSchedule(t),
                    DescribeDateRule(t),
                    DescribeKeepRule(t),
                    t.LastRunAt
                );
                gridTasks.Rows[idx].Tag = t.ID;
            }
        }

        private void OnTaskGridSelected()
        {
            if (gridTasks.SelectedRows.Count == 0) return;
            string id = gridTasks.SelectedRows[0].Tag as string;
            DeleteTask found = tasks.FirstOrDefault(x => x.ID == id);
            if (found != null) PopulateFormFromTask(found);
        }

        private void SaveCurrentTask()
        {
            DeleteTask t = BuildTaskFromForm();
            if (String.IsNullOrWhiteSpace(t.FolderPath))
            {
                MessageBox.Show("Por favor selecciona la carpeta que deseas limpiar.", "Carpeta requerida", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (CleanerEngine.IsDangerousRoot(t.FolderPath))
            {
                MessageBox.Show("Por seguridad no se permite seleccionar directamente la raiz del disco (ej. C:\\). Selecciona una subcarpeta.", "Seguridad", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            int existingIdx = tasks.FindIndex(x => x.ID == t.ID);
            if (existingIdx >= 0)
            {
                t.LastRunAt = tasks[existingIdx].LastRunAt;
                t.LastRunSummary = tasks[existingIdx].LastRunSummary;
                tasks[existingIdx] = t;
            }
            else
            {
                tasks.Add(t);
            }

            CleanerEngine.SaveTasks(tasks);
            string winStatus = CleanerEngine.SyncWithWindowsTaskScheduler(t);
            LoadTasksIntoGrid();
            MessageBox.Show("Tarea guardada exitosamente.\n\nEstado en Windows Server:\n" + winStatus, "Tarea Guardada", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void DeleteSelectedTask()
        {
            if (gridTasks.SelectedRows.Count == 0) return;
            string id = gridTasks.SelectedRows[0].Tag as string;
            DeleteTask found = tasks.FirstOrDefault(x => x.ID == id);
            if (found == null) return;

            if (MessageBox.Show("¿Seguro que deseas eliminar la tarea '" + found.Name + "'?", "Confirmar", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                CleanerEngine.DeleteFromWindowsTaskScheduler(found);
                tasks.RemoveAll(x => x.ID == id);
                CleanerEngine.SaveTasks(tasks);
                LoadTasksIntoGrid();
                ResetEditorForNewTask();
            }
        }

        private void RunSimulationFromEditor()
        {
            DeleteTask t = BuildTaskFromForm();
            ExecutionResult res = CleanerEngine.EvaluateOrRun(t, true, DateTime.Now);
            RenderExecutionResult(res);
        }

        private void RunSelectedOrCurrent(bool dryRun)
        {
            DeleteTask t = BuildTaskFromForm();
            if (!dryRun)
            {
                DialogResult dr = MessageBox.Show(
                    "¿Confirmas ejecutar el BORRADO REAL ahora mismo sobre la carpeta:\n" + t.FolderPath + "?",
                    "Confirmar Borrado Real",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (dr != DialogResult.Yes) return;
            }
            ExecutionResult res = CleanerEngine.EvaluateOrRun(t, dryRun, DateTime.Now);
            if (!dryRun)
            {
                int idx = tasks.FindIndex(x => x.ID == t.ID);
                if (idx >= 0)
                {
                    tasks[idx].LastRunAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                    tasks[idx].LastRunSummary = res.SummaryMessage;
                    CleanerEngine.SaveTasks(tasks);
                    LoadTasksIntoGrid();
                }
            }
            RenderExecutionResult(res);
        }

        private void RenderExecutionResult(ExecutionResult res)
        {
            lblSimStatus.Text = res.SummaryMessage + " | Ventana evaluada: " + res.WindowDescription;
            gridPreview.Rows.Clear();
            foreach (FileEvalItem it in res.Items)
            {
                int r = gridPreview.Rows.Add(
                    it.Action,
                    it.RelativePath,
                    it.FileTime.ToString("yyyy-MM-dd HH:mm"),
                    CleanerEngine.FormatBytes(it.SizeBytes),
                    it.Reason
                );
                if (it.Action == "BORRAR" || it.Action == "BORRADO")
                {
                    gridPreview.Rows[r].DefaultCellStyle.BackColor = Color.FromArgb(254, 226, 226);
                    gridPreview.Rows[r].DefaultCellStyle.ForeColor = Color.FromArgb(153, 27, 27);
                }
                else if (it.Action == "CONSERVAR")
                {
                    gridPreview.Rows[r].DefaultCellStyle.BackColor = Color.FromArgb(209, 250, 229);
                    gridPreview.Rows[r].DefaultCellStyle.ForeColor = Color.FromArgb(6, 95, 70);
                }
            }
        }

        private void CreateAndLoadDemoScenario()
        {
            string demoDir = CleanerEngine.CreateDemoFolder(DateTime.Now);
            DeleteTask demo = new DeleteTask
            {
                ID = "DEMO_DIA1",
                Name = "Ejemplo: Dia 1 borra mes anterior y deja Cierre + Importantes",
                FolderPath = demoDir,
                Enabled = true,
                ScheduleType = "MONTHLY",
                DayOfMonth = 1,
                RunTime = "02:00",
                SyncWindowsTask = true,
                DateRuleType = "PREV_MONTH",
                MonthDayFrom = 1,
                MonthDayTo = 31,
                KeepNamePatterns = "*cierre*, *IMPORTANTE*",
                KeepFirstOfMonth = true,
                KeepLastOfMonth = true
            };

            tasks.RemoveAll(x => x.ID == demo.ID);
            tasks.Insert(0, demo);
            CleanerEngine.SaveTasks(tasks);
            LoadTasksIntoGrid();
            PopulateFormFromTask(demo);
            RunSimulationFromEditor();
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // Headless mode invoked by Windows Task Scheduler: AutoCleanerServer.exe --run-task <ID>
            if (args != null && args.Length >= 2 && args[0].Equals("--run-task", StringComparison.OrdinalIgnoreCase))
            {
                string targetId = args[1];
                List<DeleteTask> all = CleanerEngine.LoadTasks();
                DeleteTask found = all.FirstOrDefault(x => String.Equals(x.ID, targetId, StringComparison.OrdinalIgnoreCase));
                if (found == null) return 1;

                ExecutionResult res = CleanerEngine.EvaluateOrRun(found, false, DateTime.Now);
                found.LastRunAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                found.LastRunSummary = res.SummaryMessage;
                CleanerEngine.SaveTasks(all);
                return res.ErrorCount > 0 ? 2 : 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }
    }
}
