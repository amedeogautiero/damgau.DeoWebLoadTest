@echo off
setlocal

:: Si posiziona nella cartella in cui si trova lo script (root del repository)
cd /d "%~dp0"

echo ==========================================
echo [1/3] Aggiornamento repository (git pull)
echo ==========================================
git pull origin
if %errorlevel% neq 0 (
    echo [ERRORE] git pull fallito. Interruzione dello script.
    exit /b %errorlevel%
)

echo.
echo ==========================================
echo [2/3] Compilazione progetto .NET Host
echo ==========================================
dotnet build ".\src\LoadTest.VSCode.Host" -c Debug
if %errorlevel% neq 0 (
    echo [ERRORE] Compilazione .NET fallita. Interruzione dello script.
    exit /b %errorlevel%
)

echo.
echo ==========================================
echo [3/3] Compilazione progetto Node Extension
echo ==========================================
pushd ".\src\LoadTest.VSCode.Extension"
call npm install
if %errorlevel% neq 0 (
    popd
    echo [ERRORE] npm install fallito. Interruzione dello script.
    exit /b %errorlevel%
)

call npm run compile
if %errorlevel% neq 0 (
    popd
    echo [ERRORE] Compilazione Node fallita. Interruzione dello script.
    exit /b %errorlevel%
)
popd

echo.
echo ==========================================
echo Compilazione completata con successo!
echo ==========================================
pause