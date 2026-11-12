@echo off
chcp 65001 >nul
title Instalar AutoCleaner Server en Windows Server
echo ====================================================================
echo   INSTALADOR DE AUTOCLEANER SERVER PARA WINDOWS SERVER
echo ====================================================================
echo.
set "EXE_PATH=%~dp0AutoCleanerServer.exe"

if not exist "%EXE_PATH%" (
    echo [ERROR] No se encontro AutoCleanerServer.exe en esta carpeta: %~dp0
    pause
    exit /b 1
)

echo Registrando servicio automatico en el Programador de Tareas de Windows...
schtasks /Create /F /TN "\AutoCleanerServer\ServicioResidente" /TR "\"%EXE_PATH%\" --daemon" /SC ONSTART /RU SYSTEM /RL HIGHEST
if %errorlevel% equ 0 (
    echo [OK] Registrado exitosamente como tarea de inicio del sistema (SYSTEM).
) else (
    echo [INFO] Registrando para el usuario actual al iniciar sesion...
    schtasks /Create /F /TN "\AutoCleanerServer\ServicioResidente" /TR "\"%EXE_PATH%\" --daemon" /SC ONLOGON
)

echo.
echo Abriendo interfaz grafica de AutoCleaner Server...
start "" "%EXE_PATH%"
echo.
echo Listo. Puedes cerrar esta ventana.
pause
