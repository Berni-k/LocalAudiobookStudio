#!/bin/sh
# Builds and runs Local Audiobook Studio from source. Needs the .NET 10 SDK.
cd "$(dirname "$0")" || exit 1
exec dotnet run --project src/LocalAudiobookStudio.App -- "$@"
