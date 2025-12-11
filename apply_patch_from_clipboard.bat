@echo off
setlocal

rem Переходим в корень репозитория
for /f "delims=" %%i in ('git rev-parse --show-toplevel') do set ROOT=%%i
cd "%ROOT%"

rem Применяем патч из буфера обмена
powershell -NoLogo -Command ^
  "Get-Clipboard -Raw | git apply --3way --whitespace=nowarn -"

if %errorlevel% neq 0 (
  echo.
  echo Ошибка применения патча
  exit /b 1
)

echo Патч успешно применен
