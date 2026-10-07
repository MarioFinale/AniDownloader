@echo off
dotnet publish AniDownloaderTerminal.csproj -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true -o ./bin/osx-arm64/