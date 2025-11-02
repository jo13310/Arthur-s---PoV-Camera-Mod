# ArthurRay PoV Camera Mod

Immersive first-person and manager-view camera pack for Football Manager 2026. Switch cameras on demand or let the mod automatically jump into POV mode for key match events (goals, free-kicks, corners, penalties, and replays) to make every highlight feel like you are on the pitch.

## Features

- Hotkeys: `F1` classic FM camera, `F2` player POV, `F3` previous player, `F4` next player, `F5` manager zone POV, `F6` toggle Auto-POV mode.
- Auto-POV slows match speed for dramatic moments and restores your original speed afterwards.
- Context-aware POV selection (penalties switch to shooter/goalkeeper, set pieces follow the taker, etc.).
- Built for BepInEx IL2CPP 6.x; installs cleanly through FM Reloaded Mod Manager using the included manifest.

## Requirements

- Football Manager 2026 (Steam build).
- FM Reloaded Mod Manager `0.5.0` or later.
- BepInEx 6 (IL2CPP) bootstrap already installed in your FM directory.

## Installation

### Through FM Reloaded (recommended)

1. Open the **Mod Store** tab and install `ArthurRay PoV Camera Mod`. The entry downloads the latest `ArthurRayPovMod.dll`, fetches the hosted `manifest.json`, and places the DLL in `BepInEx/plugins/`.
2. Alternatively, download the DLL manually and use **Install From ZIP/Folder** → select the folder containing `manifest.json`.
3. Enable the mod and launch the game.

### Manual install

1. Download `ArthurRayPovMod.dll` from the latest GitHub release.
2. Copy `plugins/ArthurRayPovMod.dll` into your `Football Manager 26/BepInEx/plugins/` folder.
3. (Optional) Keep the bundled `manifest.json` alongside the DLL so FM Reloaded can detect updates.

## Building from Source

1. Open `SourceCode/ArthurRayPovMod.sln` with Visual Studio 2022.
2. Restore NuGet packages (BepInEx feed is already listed in the `.csproj`).
3. Build in `Release` configuration; the DLL will appear in `SourceCode/bin/Release/net6.0/ArthurRayPovMod.dll`.
4. Place the built DLL in the `plugins/` folder and regenerate the release ZIP if needed.

## Manifest Overview

The repository root contains a `manifest.json` that FM Reloaded uses during installation:

```json
{
  "name": "ArthurRay PoV Camera Mod",
  "version": "1.0.0",
  "type": "misc",
  "author": "GerKo & Brululul",
  "files": [
    {
      "source": "plugins/ArthurRayPovMod.dll",
      "target_subpath": "BepInEx/plugins/ArthurRayPovMod.dll",
      "platform": "windows"
    }
  ]
}
```

Keep the manifest updated whenever you ship a new version so the Mod Manager can verify compatibility and perform clean installs.

## License & Credits

- Plugin by **GerKo & Brululul** with contributions from the FM Reloaded community.
- Powered by [BepInEx](https://github.com/BepInEx/BepInEx) and Harmony.

For issues or feature requests, open an issue on [GitHub](https://github.com/jo13310/Arthur-s---PoV-Camera-Mod/issues) or join the FM Reloaded Discord.
