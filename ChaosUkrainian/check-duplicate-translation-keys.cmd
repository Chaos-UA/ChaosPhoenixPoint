@echo off
setlocal

chcp 65001 >nul
set PYTHONIOENCODING=utf-8

python "%~dp0check-duplicate-translation-keys.py"
exit /b %ERRORLEVEL%