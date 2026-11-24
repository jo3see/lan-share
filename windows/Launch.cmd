@echo off
if not exist "%~dp0out\TetherNest.exe" (
  echo Build TetherNest first using build.ps1 in this folder.
  pause
  exit /b 1
)
start "" "%~dp0out\TetherNest.exe"
