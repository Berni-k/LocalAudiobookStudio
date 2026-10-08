@echo off
rem Builds and runs Local Audiobook Studio from source. Needs the .NET 10 SDK.
cd /d "%~dp0"
dotnet run --project src\LocalAudiobookStudio.App -- %*
if errorlevel 1 pause
