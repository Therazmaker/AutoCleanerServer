# 🧹 AutoCleaner Server v2.0 (Nativo Microsoft .NET Framework — Sin Bloqueo de Windows Defender / SmartScreen)

Aplicación nativa **Windows Forms (`.NET Framework 4.x`)** para crear y programar **tareas automáticas de borrado de archivos** en **Windows Server**.

## ⬇️ Descarga Directa (Versión v2.0 Libre de Falsos Positivos)

- **[📦 Descargar AutoCleanerServer_Windows.zip (~35 KB — Recomendado)](https://github.com/Therazmaker/AutoCleanerServer/raw/main/AutoCleanerServer_Windows.zip)**
  *(Incluye `Instalar_Sin_Bloqueo.bat` + código fuente `AutoCleanerServer.cs` + `AutoCleanerServer.exe` nativo de Windows Forms)*
- **[💻 Descargar solo AutoCleanerServer.exe (~32 KB)](https://github.com/Therazmaker/AutoCleanerServer/raw/main/AutoCleanerServer.exe)**

---

## 🛡️ ¿Por qué la versión v2.0 NO es bloqueada por Windows Defender ni SmartScreen?

1. **Reemplazamos el binario de Go de 7 MB por una aplicación nativa de Microsoft `.NET Framework` Windows Forms de solo ~32 KB** (`AutoCleanerServer.cs`):
   - No abre puertos de red locales ni lanza navegadores en segundo plano (que es lo que activaba la heurística de Windows Defender en el binario anterior).
   - Utiliza únicamente las librerías nativas incluidas dentro de Windows Server (`System.Windows.Forms`, `System.IO`, `System.Xml`).
2. **Generación Local en 1 Clic (`Instalar_Sin_Bloqueo.bat`)**:
   - Cuando descargas cualquier `.exe` de internet, el navegador le pega una etiqueta oculta llamada *Mark-of-the-Web (`Zone.Identifier`)* que hace saltar la pantalla azul de **Windows SmartScreen**.
   - Al ejecutar **`Instalar_Sin_Bloqueo.bat`**, el script elimina esa etiqueta (`Unblock-File`) y usa el compilador oficial de Microsoft que ya viene de fábrica en tu Windows Server (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`) para generar `AutoCleanerServer.exe` **directamente dentro de tu propio servidor en 1 segundo**.
   - Al nacer localmente en tu servidor, **Windows confía en él al 100% sin mostrar advertencias**.

---

## 🚀 Cómo usarlo en tu Windows Server

1. Descarga **[AutoCleanerServer_Windows.zip](https://github.com/Therazmaker/AutoCleanerServer/raw/main/AutoCleanerServer_Windows.zip)** y descomprímelo en una carpeta fija, por ejemplo `C:\AutoCleanerServer\`.
2. Haz doble clic en **`Instalar_Sin_Bloqueo.bat`** (o clic derecho → *Ejecutar como administrador*).
3. Se abrirá inmediatamente la ventana nativa de **AutoCleaner Server v2.0**.
