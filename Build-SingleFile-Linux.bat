@echo off
dotnet publish AniDownloaderTerminal.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o ./bin/linux-x64/