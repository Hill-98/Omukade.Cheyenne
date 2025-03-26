# Omukade Cheyenne
![logo](logo.png)

This is the server software for running private Pokemon TCG Live servers (codename Rainier). It provides complete support for all gameplay aspects (eg, cards, gamerules), and basic matchmaking and friend support, without relying on official Pokemon TCG Live servers.
(Clients may still require official servers for storing decks, maintaining friend lists, and fetching assets.)

**If you just want to play the game**, this is not the correct software. You want the [Native Omukade Connector](https://github.com/Hastwell/Rainer.NativeOmukadeConnector).

## Requirements
* [.NET 6 Runtime or SDK](https://dotnet.microsoft.com/en-us/download/dotnet/6.0) for your platform
* A valid Pokemon Trainer's Club account that has logged in to TCGL at least once (used for fetching game data).
* Supports Windows x64 and Linux x64 + ARM64
* Clients need to use a Connector (such as [Native Omukade Connector](https://github.com/Hastwell/Rainer.NativeOmukadeConnector))
to connect their game to an Omukade Cheyenne or other Omukade server.
* For developing:
    * Visual Studio 2022 (any edition) with C#

## Usage

### Quick(ish) Install Guide
0. _(Recommended for security, but optional)_ Create a seperate user dedicated to running Cheyenne and other Omukade software. This user should not have administrative rights.
1. Download the current release of Omukade Cheyenne for your OS and architecture.
2. Extract the Omukade Cheyenne release files to the desired directory. This directory should be accessable by the user that will run Omukade Cheyenne.
4. Inside the installation directory, create the file `secrets.json` and enter a valid Trainer's Club username and password to download the game data.
**The account chosen must have signed into TCG Live at least once and completed the "Authorize TCGL to use your account" prompts.** This account does not have to be your own, and can be one dedicated solely to this purpose.
An example `secrets.json` resembles:
```json
{"username":"your-trainer-club-username","password":"your-trainer-club-password"}
```

This application uses AutoPAR to load the TCGL assemblies; it will usually automatically download the game and any updates to it when the server is started. If this does not work, see _AutoPAR Issues_.

The default port the server uses is 10850. 10851 will be used in the future to provide secure WSS support.
### Configuration
#### config.json - General Settings
The default settings are usually sufficient to start a server without additional settings.
* autopar-search-directory - The folder to search for the Pokemon TCG Live assemblies, usually the game's `Managed` directory or a copy thereof. **Required only if** AutoPAR cannot retrieve game updates correctly.
* autopar-autodetect-rainier-install-directory: Attempts to search the computer the server is running on for a PTCGL install, and use its game binaries to run the server. **Required only if** AutoPAR cannot retrieve game updates correctly; Windows only and requires a PTCGL install.
* ws-port - The port to run the server on. Defaults to 10850.
* cardsource-overrides-enable - Enables experimental support for overriding card definitions with custom versions.
* cardsource-overrides-directory - The directory to search for card definition overrides. Defaults to `overrides` under the server's install directory.
* discord-error-webhook-enable - Enables support for notifing server operators of errors via a Discord webhook. Default is `false`.
* discord-error-webhook-url - The URL to use for the Discord error webhook. **Required if** discord-error-webhook-enable is `true`.
* run-as-daemon - Starts the server headless without user interactivity or commands, typically used for running as a service (eg, by systemd). Starting with the `--daemon` argument will always enable this mode.
* enable-game-timers - **BETA** - games will have a timer enabled. Defaults to false. _The server does not perform any enforcement of timers; misbeahving and malicious clients can easily bypass them even if enabled._

#### config.json - Advanced Settings
These should generally not be changed from their defaults; doing so may have significant undesirable side effects.

* carddata-directory - The folder containing all game rules downloaded by the server. Defaults to `PTCGL-CardDefinitions` under the [Omukade Shared Data Directory](#Omukade-Shared-Data-Directory).
* disable-player-order-randomization - Disables the randomization of which player is considered P1 vs P2 when a game starts. P1 and P2 instead become eg, the first and second player to queue. P1 will always call the opening coinflip. Default is `false`.
* debug-fixed-rng-seed - Uses a fixed RNG seed for all games, ensuring all coinflips/deckshuffles/other randomization is the same each time. Default is `false`.
* enable-reporting-all-implemented-cards - Whether to enable reporting all implemented expanded cards to clients. Default is `true`.
* debug-prizes-per-player - Changes the starting number of prizes. Default: 6.
* debug-game-timer-time - Change the game timer time in seconds. Default: 1500

### Omukade Shared Data Directory
The various Omukade projects all share common TCGL data (eg, game binaries, card and rule definitions) to avoid duplication of data, and reduce maintainance requirements that would otherwise occur if each Omukade program used its own copy of TCGL data. This directory is located:
* Windows: `%LOCALAPPDATA%\omukade\rainier-shared`
* Linux: `~/.local/share/omukade`

Regardless of OS, the directory's location is relative to the user running the Omukade software; each user will have a seperate directory for that user. If the directory does not exist, it will be created when an Omukade software starts.

### AutoPAR Issues
Usually, AutoPAR will be able to fetch the game executable and use it to run the server. If it is not able to do this, one of these alternatives may be used to provide the requisite executables:
* Windows Only, Recommended: Install Pokemon TCG Live, and set `autopar-autodetect-rainier-install-directory` to true in config.json. It will be detected by AutoPAR and used for Cheyenne.
* Windows Only: Add the setting `autopar-search-directory` with the location of your TCGL install directory to config.json. Backslashes and quotes must be escaped (`\\` and `\"` respectively).
* Windows, Linux: Copy the TCGL assemblies (DLL files) from your TCGL install directory (`C:\Install\Folder\Pokémon Trading Card Game Live\Pokemon TCG Live_Data\Managed`) to the folder `autopar` under the server's directory.
  `autopar-search-folder` can be used to set any other name or location for this directory if prefered. *You must manually update this folder whenever the game updates!*

## API
The server includes an API to retrieve basic information about the server and its players. The API is reachable via HTTP on the server's main port (10850).

Currently, these endpoints are:
* `GET /api/v1/players => {"count":123}`
* `GET /api/v1/players?names=true => {"count":123, "players":["Foo","Bar"]}`

## Compiling

### Rainier Dependencies with AutoPAR
When checking out a project using AutoPAR for the first time, you may see errors related to types and assemblies not found.
The `Omukade.AutoPAR.BuildPipeline.Rainier` package will fetch and prepare these dependencies for you when attempting a build.
No manual action should be needed, although client updates may require changing referenced libraries (eg, old libs removed, new ones added that are now required)

### Building
* Use Visual Studio 2022 or later, build the project.
* With the .NET 6 SDK, `dotnet build Omukade.Cheyenne.sln`

### Publishing
* With Visual Studio, publish the project using one of the included publish profiles.
* With the .NET 6 SDK, `dotnet publish Omukade.Cheyenne.sln -p:PublishProfile="Omukade.Cheyenne\Properties\PublishProfiles\[target platform].pubxml"`

The resulting binary can be pulled from `Omukade.Cheyenne\bin\Release\net6.0\publish\[target platform]\`
All included publish profiles are self-contained and do not require installation of .NET Core or other frameworks on the machine the binary is ultimately run on.

## License
This software is licensed under the terms of the [GNU AGPL v3.0](https://www.gnu.org/licenses/agpl-3.0.en.html)