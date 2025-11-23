# AniDownloader  - Automated Anime Downloader, Transcoder and Manager


**Disclaimer:** Before using AniDownloader, please ensure you comply with local laws and regulations regarding the downloading, copying, and distribution of copyrighted materials. This software should only be used with content that is legally available for download or where you have explicit rights.

AniDownloader is a terminal-based application for automatically managing anime series downloads, transcoding, and file organization. Designed to run on .NET 6, AniDownloader works on both Linux and Windows. It uses nyaa.si as a source for files.


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

- **.NET 8**: AniDownloader requires .NET 8, which can be installed from the official [Microsoft .NET website](https://dotnet.microsoft.com/download/dotnet/8.0).
- **FFmpeg**: Ensure FFmpeg is installed on your system for transcoding functionality. Installation instructions can be found at [FFmpeg.org](https://ffmpeg.org/).
- **mkvmerge**: If you use MKV as a container. Used to optimize MKV files for quick seeking/streaming. Check [The MKVToolNix website](https://mkvtoolnix.org/) for more info about this tool.

## Installation

Clone the repository and navigate to the directory:

```bash
git clone https://github.com/MarioFinale/AniDownloader.git
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

Configuration options can be set in AniDownloader.cfg and Series Data is stored in SeriesData.xml.

# Web Interface
Access the web server at 127.0.0.1:8080 to manage downloads, view current list and change settings. Changing the default bind IP may require admin/root privileges.

# Troubleshooting
If the program crashes or becomes unresponsive, check logs in /var/log/AniDownloader.log.
Ensure ffmpeg is in your PATH for transcoding to work.

# Support
For issues or feature requests, open a ticket on GitHub.

# Contributing
Contributions are welcome! Please feel free to submit issues, feature requests, and pull requests.
For pull request and code contributions, please follow the [style guide](./STYLE.md).

# License
AniDownloader is released under the GNU GENERAL PUBLIC LICENSE VERSION 2 (GPLv2). See the [LICENSE](./LICENSE) for details.

View on [GitHub](github.com/MarioFinale/AniDownloader) | [Report a Bug or Issue](github.com/MarioFinale/AniDownloader/issues)
