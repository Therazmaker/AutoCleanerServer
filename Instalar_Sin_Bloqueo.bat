@echo off
chcp 65001 >nul
title Instalador Nativo de AutoCleaner Server (Sin Bloqueo SmartScreen)
echo =========================================================================
echo   AUTOCLEANER SERVER v2.0 — GENERADOR NATIVO MICROSOFT .NET
echo =========================================================================
echo.

cd /d "%~dp0"

REM 1. Quitar marca de descarga de internet (Mark-of-the-Web / SmartScreen)
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-ChildItem -Path '%~dp0' -Recurse | Unblock-File -ErrorAction SilentlyContinue" >nul 2>&1

REM 2. Localizar el compilador oficial de Microsoft C# incluido en todo Windows Server
set "CSC_PATH=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC_PATH%" (
    set "CSC_PATH=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)

if exist "%~dp0AutoCleanerServer.cs" (
    if exist "%CSC_PATH%" (
        echo [1/2] Compilando AutoCleanerServer.exe localmente con Microsoft .NET Framework...
        "%CSC_PATH%" /nologo /target:winexe /optimize+ /out:"%~dp0AutoCleanerServer.exe" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Xml.dll "%~dp0AutoCleanerServer.cs"
        if %errorlevel% equ 0 (
            echo [OK] AutoCleanerServer.exe generado localmente en este servidor (100%% libre de SmartScreen).
        ) else (
            echo [ADVERTENCIA] Hubo un detalle al compilar, se usara el ejecutable precompilado.
        )
    )
)

echo.
echo [2/2] Iniciando interfaz grafica de AutoCleaner Server...
start "" "%~dp0AutoCleanerServer.exe"
echo.
echo Listo. La ventana de AutoCleaner Server ya esta abierta.
timeout /t 3 >nul
exit /b 0
