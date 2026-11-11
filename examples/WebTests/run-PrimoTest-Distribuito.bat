@echo off
setlocal

REM Directory del BAT: examples\WebTests
set "TESTS_DIR=%~dp0"

REM Root del repository
for %%I in ("%TESTS_DIR%..\..") do set "REPO_DIR=%%~fI"

REM Percorso CLI
set "CLI_DIR=%REPO_DIR%\src\LoadTest.Cli\bin\Debug\net10.0"
set "EXE=%CLI_DIR%\loadtest.exe"

REM File YAML e risultati
set "TEST_FILE=%TESTS_DIR%PrimoTest\primo-test.loadtest.yaml"
set "RESULTS_DIR=%TESTS_DIR%results"

REM Verifica CLI
if not exist "%EXE%" (
    echo ERRORE: loadtest.exe non trovato:
    echo "%EXE%"
    pause
    exit /b 2
)

REM Verifica YAML
if not exist "%TEST_FILE%" (
    echo ERRORE: file YAML non trovato:
    echo "%TEST_FILE%"
    pause
    exit /b 2
)

REM Crea cartella risultati
if not exist "%RESULTS_DIR%" mkdir "%RESULTS_DIR%"

echo.
echo === AVVIO CONTROLLER ===

start "LoadTest Controller" /D "%CLI_DIR%" "%ComSpec%" /k ""%EXE%" controller --listen http://0.0.0.0:5001 --api http://127.0.0.1:5000 --allow-anonymous-agents --results-db "%RESULTS_DIR%\controller-results.db"

timeout /t 3 /nobreak >nul

echo.
echo === AVVIO AGENT ===

start "LoadTest Agent" /D "%CLI_DIR%" "%ComSpec%" /k ""%EXE%" agent --controller http://localhost:5001"

timeout /t 3 /nobreak >nul

echo.
echo === AVVIO TEST ===
echo.

"%EXE%" run "%TEST_FILE%" --controller http://localhost:5000 --upload --db "%RESULTS_DIR%\loadtest-results.db"

set "TEST_EXIT_CODE=%ERRORLEVEL%"

echo.
echo === TEST TERMINATO ===
echo Exit code: %TEST_EXIT_CODE%

echo.
echo Controller e Agent restano aperti.
pause

exit /b %TEST_EXIT_CODE%