@echo off
dotnet publish AniDownloaderTerminal.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./bin/win-x64/