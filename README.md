# 🧹 AutoCleaner Server (Windows Server x64)

Programa autónomo (`.exe` sin dependencias externas) para crear y programar **tareas automáticas de borrado de archivos** en **Windows Server**.

## ⬇️ Descarga Directa para Windows Server

- **[📦 Descargar AutoCleanerServer_Windows.zip (Recomendado: .exe + instalador .bat)](https://github.com/Therazmaker/AutoCleanerServer/releases/download/v1.0.0/AutoCleanerServer_Windows.zip)**
- **[💻 Descargar solo AutoCleanerServer.exe](https://github.com/Therazmaker/AutoCleanerServer/releases/download/v1.0.0/AutoCleanerServer.exe)**

---

## 🚀 Cómo instalarlo en tu Windows Server

1. Crea una carpeta en tu servidor, por ejemplo `C:\AutoCleanerServer\`.
2. Descarga y descomprime ahí `AutoCleanerServer.exe` y `Instalar_Windows_Server.bat`.
3. Haz **clic derecho sobre `Instalar_Windows_Server.bat` → "Ejecutar como administrador"** (o abre directamente `AutoCleanerServer.exe` como administrador).
4. Se abrirá la ventana visual del programa donde podrás:
   - Seleccionar la carpeta a limpiar (con el explorador visual de discos `C:\`, `D:\`, etc.).
   - Elegir qué día y hora se ejecuta (ej. **El día 1 de cada mes a las 02:00**).
   - Elegir el rango de fechas a borrar (ej. **Todo el mes calendario anterior**, o **Desde Fecha A hasta Fecha B**, o **Más de X días de antigüedad**).
   - Configurar **qué archivos conservar intactos ("Y solo deja tal")**: por nombre/comodín (`*cierre*`, `*IMPORTANTE*`), extensión, día 1 del mes, último día del mes, o los `N` archivos más recientes.
   - Probar primero con el botón **🔍 Simular Ahora (Dry Run)** antes de guardar.

## 🔒 ¿Sigue funcionando al cerrar la ventana o reiniciar el servidor?

**Sí.** Al guardar una tarea:
- Se registra automáticamente en el **Programador de Tareas de Windows (`schtasks.exe`)** bajo `\AutoCleanerServer\` con permisos `SYSTEM`.
- También registra el servicio residente al arranque de Windows Server (`ONSTART`).
