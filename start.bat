@echo off
rem TOKENIFY - double-click to start a local server and open the site in your browser.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-server.ps1" %*
