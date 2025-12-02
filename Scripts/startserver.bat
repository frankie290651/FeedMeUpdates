@echo off
cd /d "%~dp0"

:loop
REM Avvia RustDedicated e attende la sua chiusura
start "" /wait RustDedicated.exe -batchmode -logfile "log.txt" +server.port 28015 +server.level "Procedural Map" +server.seed 1234 +server.worldsize 4000 +server.maxplayers 1 +server.hostname "TestServer1" +server.description My TestServer" +server.identity "testserver" +rcon.port 28016 +rcon.password 1234 +rcon.web 1

REM Attende 5 secondi
timeout /t 5 >nul

REM Se è presente updater.lock termina lo script, altrimenti riavvia il server
if exist "updating.lock" goto end
goto loop

:end
exit
