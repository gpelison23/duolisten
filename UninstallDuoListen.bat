@echo off
chcp 65001 >nul
title Desinstalar DuoListen
cd /d "%~dp0"

echo.
echo  ===============================================
echo   Desinstalar o DuoListen / Uninstall DuoListen
echo  ===============================================
echo.
echo  Isso apaga o DuoListen.exe, os arquivos que vieram no zip,
echo  as configuracoes e os arquivos temporarios do app.
echo  Os alto-falantes saem do mudo.
echo.
echo  This deletes DuoListen.exe, the files from the zip,
echo  settings and temporary files. Speakers are unmuted.
echo.
choice /c SN /n /m " Continuar? / Continue? [S=Sim/Yes, N=Nao/No] "
if errorlevel 2 exit /b

echo.
echo  Fechando o DuoListen... / Closing DuoListen...
taskkill /im DuoListen.exe >nul 2>&1
timeout /t 3 /nobreak >nul
taskkill /f /im DuoListen.exe >nul 2>&1

if exist "%~dp0DuoListen.exe" (
    echo  Removendo... / Removing...
    echo  Pronto! O DuoListen foi removido. / Done! DuoListen was removed.
    timeout /t 3 >nul
    start "" /wait "%~dp0DuoListen.exe" --uninstall & exit
)

rem DuoListen.exe nao encontrado: limpa o que der / exe not found: clean what we can
rmdir /s /q "%APPDATA%\DuoListen" >nul 2>&1
rmdir /s /q "%TEMP%\.net\DuoListen" >nul 2>&1
del /f /q "%~dp0INSTALACAO.txt" "%~dp0LEIA-ME.txt" >nul 2>&1
echo  Pronto! / Done!
timeout /t 3 >nul
(goto) 2>nul & del "%~f0"
