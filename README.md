# AniDownloader

AniDownloader is a terminal-based application for automatically managing anime series downloads, transcodes, and file organization. Designed to run on .NET 6, AniDownloader works on both Linux and Windows.

## Features

- **Automated Downloads**: Fetches torrents for specified anime series from Nyaa.si by simply providing the series name.
- **Continuous Operation**: Runs continuously, suitable for daemon operation under Linux.
- **Torrent-Based**: Uses torrents for file transfers, not direct downloads.
- **Intelligent Filtering**: Filters episodes based on criteria such as file size, age, and language.
- **Selective Episode Handling**: Detects and downloads the highest quality version of each episode that includes the required subtitle language.
- **Efficient Resource Management**: Limits bandwidth and request rate to avoid source website rate limits.
- **Transcoding Support**: Automatically transcodes downloaded episodes to a smaller, device-compatible format using `ffmpeg`.
- **Automated Cleanup**: Cleans up incomplete files and temporary folders, helping to recover gracefully from power interruptions or partial downloads.
- **Organized File Management**: Creates folders and assigns correct episode numbers to each file for easy access.

## Requirements

- **.NET 6**: AniDownloader requires .NET 6, which can be installed from the official [Microsoft .NET website](https://dotnet.microsoft.com/download/dotnet/6.0).
- **FFmpeg**: Ensure FFmpeg is installed on your system for transcoding functionality. Installation instructions can be found at [FFmpeg.org](https://ffmpeg.org/).

## Installation

Clone the repository and navigate to the directory:

```bash
git clone https://github.com/yourusername/AniDownloader.git
cd AniDownloader
```

## Build the project

```bash
dotnet build
```

## Usage

To start AniDownloader, navigate to the folder and run:
```bash
dotnet run Anidownloader.dll
```

## Build as an Self-Contained, Single file
It is recommended to build AniDownloader as an stand-alone binary. Pre-Built stand-alone blobs are availiable in Releases but you can build your own with Build-SingleFile-Linux.bat or running the following command:
```bash
dotnet publish -c Release -r linux-x64 --self-contained /p:PublishSingleFile=true /p:PublishSelfContained=true
```


## Running as a background task (Linux)

To run as a background task on Linux, It's recommended to use `screen` since the output prints continuously:
```bash
/usr/bin/screen -dmS AniDownloader dotnet run AniDownloader.dll
```
If you use `nohup` It's strongly recommended to redirect the output buffer to /dev/null.

# How It Works

* Episode Fetching: AniDownloader periodically checks Nyaa.si for new episodes of your specified series.
* Filtering & Selection: Episodes are filtered based on size, release date, language, and quality to download only the most suitable files.
* Downloading & Transcoding: Each episode is downloaded via torrent, then transcoded with ffmpeg to a more compatible and smaller format.
* Seeding & Cleanup: Once transcoded, the file continues seeding until it reaches a defined ratio, and unnecessary temporary files are removed.
* File Organization: AniDownloader organizes each series into its own directory and names files according to episode number for easy identification.

# Configuration

Configuration options can be set in SeriesData.xml. 

A simple Web-Server is in the works.

# Contributing

Contributions are welcome! Please feel free to submit issues, feature requests, and pull requests. 

For pull request and code contributions, please follow the [style guide](./Style.md).