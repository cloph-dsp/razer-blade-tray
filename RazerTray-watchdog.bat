@echo off
REM RazerTray watchdog - restarts the tray app if it crashes
REM Place a shortcut to this batch file in Startup folder

:loop
start /wait "" "%~dp0RazerTray.exe"
timeout /t 3 /nobreak >nul
goto loop
