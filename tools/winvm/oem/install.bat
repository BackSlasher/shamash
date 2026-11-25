@echo off
rem dockur/windows copies this folder to C:\OEM and runs install.bat in the last install step.
powershell -NoProfile -ExecutionPolicy Bypass -File C:\OEM\setup.ps1 > C:\OEM\setup.log 2>&1
