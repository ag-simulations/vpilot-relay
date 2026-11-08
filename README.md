# vPilot Relay

**A vPilot plugin that forwards vPilot notifications to Telegram and lets you answer controllers from Telegram.**

Never miss a VATSIM controller, even when you're away from your PC: private messages, frequency messages addressed to you and SELCAL arrive on your phone.
Reply to a notification to answer directly, or use `/menu` to tune radios, request METAR/ATIS and change your squawk.

By [AG Simulations](https://ag-simulations.com). Free and open source under the [GPL-3.0](#license).

## Download

Get the latest **`vPilotRelay-Setup-x.y.z.exe`** from the [Releases](../../releases/latest) page and run it — see [Easy install](#easy-install-recommended).

---

## Features

**Notifications on your phone**

| Icon | Notification | Can you reply? |
|---|---|---|
| ✉️ | Private message (PM) | ✅ Reply → PM to the controller |
| 📻❗ | Frequency message containing your callsign | ✅ Reply → sent on frequency |
| 📻 | Other frequency messages (optional) | — |
| 🔔 | SELCAL | — |
| 📢 | Supervisor / broadcast message | — |
| 🟢 / 🔴 | Connected to / disconnected from VATSIM | — |

**`/menu` — button-based control menu**

- ✉️ **Private message:** pick from nearby controllers or type a callsign
- 📻 **Tune radio:** UNICOM 122.800 at the top, nearby ATC below — tap a button to set COM1 active, or ✍️ type a frequency yourself (COM2/standby included)
- 📡 **Send on frequency:** text message on the active transmit frequency
- 🗼 **Online ATC:** controllers in range with frequency and distance
- 🌤 **METAR** and ℹ️ **ATIS / controller info**
- 🔢 Set **squawk**, 🆔 **Ident**
- 📊 **Status:** connection time, altitude, speed, heading, squawk, COM frequencies
- 🔕 **Silent mode:** notifications arrive silently (PMs and SELCAL still make a sound)
- 📻 **Radio alerts:** "mentions only" or "all"
- 🟢 **Connect to VATSIM:** shown while disconnected — one tap with a recent connection, or callsign → aircraft type → SELCAL step by step
- 🔌 **Disconnect** (shown while connected, asks for confirmation)

---

## Requirements

- Windows, [vPilot](https://vpilot.rosscarlson.dev/) (3.x)
- A Telegram account (Telegram app on your phone)
- For radio/squawk/status features: MSFS 2020 / MSFS 2024 (SimConnect)

---

## Easy install (recommended)

1. **Close vPilot.**
2. Download `vPilotRelay-Setup-x.y.z.exe` from [Releases](../../releases/latest) and run it.
   If Windows says "Windows protected your PC", click **More info → Run anyway** (the installer is not code-signed).
3. Follow the wizard:
   - **vPilot:** finds your vPilot folder and offers to close vPilot if it's running.
   - **Telegram bot:** shows you step by step how to create a bot with BotFather.
   - **Bot token:** paste the token; the wizard checks it with Telegram. You can paste BotFather's whole message — it picks the token out.
   - **Your account:** press "Start" in your bot; the wizard recognises you automatically, no need to look up your Chat ID.
   - **Install:** copies the plugin to the `Plugins` folder, saves the settings and sends a test message to Telegram.
4. **Start vPilot.** When `✈️ vPilot Relay is running.` arrives on Telegram, you're all set.

To update, just run the new version's installer: the wizard detects the existing installation and
**"Keep settings, update"** skips the bot steps and only replaces the plugin. Your other settings (`[Relay]` section) are kept.

---

## Manual install

### 1. Create the Telegram bot

1. Open **@BotFather** in Telegram (the one with the blue verified check).
2. Send `/newbot`.
3. Give the bot a **name** (e.g. `My vPilot Bot`).
4. Give it a **username** ending in `bot` (e.g. `my_vpilot_bot`).
5. BotFather sends you a **token** like this:
   ```
   1234567890:AAH...a-long-string...
   ```
   Copy it. **Don't share it**: anyone with the token can control your bot.

> 💡 Tap the token in BotFather to copy it, so `l` (lowercase L) and `1`, or `O` and `0`, don't get mixed up.

### 2. Download and copy the plugin

1. **Close vPilot.**
2. Download `vPilotRelay-x.y.z.zip` from [Releases](../../releases/latest) and extract it.
3. Copy **`vPilotRelay.dll`** and **`vPilotRelay.ini`** into vPilot's `Plugins` folder:
   ```
   %LOCALAPPDATA%\vPilot\Plugins
   ```
   (Paste it into the File Explorer address bar and press Enter. It's usually `C:\Users\<your user name>\AppData\Local\vPilot\Plugins`.)
4. **Unblock the DLL:** right-click `vPilotRelay.dll` → **Properties** → tick **"Unblock"** at the bottom if it's there → **OK**.
   (Windows blocks DLLs downloaded from the internet; if you skip this, the plugin won't load.)

### 3. Put the token in the settings file

Open `vPilotRelay.ini` in the `Plugins` folder with Notepad and paste the token:

```ini
[Telegram]
BotToken=1234567890:AAH...
ChatId=
```

Save.

### 4. Get your Chat ID

1. **Start vPilot.**
2. Find your bot in Telegram (the username from step 1), open it and press **Start**.
3. The bot replies:
   ```
   Your Chat ID: 123456789
   ```
4. **Close vPilot**, put this number on the `ChatId=` line of `vPilotRelay.ini` and save:
   ```ini
   ChatId=123456789
   ```
5. **Start vPilot again.** When this arrives on Telegram, you're done:
   ```
   ✈️ vPilot Relay is running.
   ```

These settings are kept in the file; you don't need to enter them again.

> 🔒 The bot only listens to the chat in `ChatId`. Even if someone else finds your bot and messages it, none of their commands will work.

---

## Usage

### Replying to a controller

When a `✉️ EGLL_TWR ...` notification arrives, **swipe it left** (or long-press → **Reply**) and type your answer.
The bot confirms with `✅ Sent → EGLL_TWR`. Reply to the confirmation to keep the conversation going.

### Commands

| Command | What it does |
|---|---|
| `/menu` | Main menu with buttons |
| `/connect BAW123 B738 AB-CD` | Connects to VATSIM (SELCAL optional; the bot asks for anything missing) |
| `/pm EGLL_TWR message` | Sends a private message |
| `/radio message` | Sends on the active TX frequency |
| `/freq 118.100` | Sets COM1 active |
| `/freq 121.800 s` | COM1 standby |
| `/freq 124.350 2` | COM2 active (`2 s` → COM2 standby) |
| `/atc` | Online ATC nearby |
| `/metar EGLL` | METAR |
| `/atis EGLL_ATIS` | ATIS / controller info |
| `/squawk 1234` | Transponder code |
| `/ident` | Squawk ident |
| `/status` | Aircraft and connection status |
| `/mute` | Toggle notification sound |
| `/help` | Command list |

Telegram also lists the commands when you type `/`.

### "Radio alerts: mentions only / all"

- **Mentions only** (default): you only get frequency text messages **containing your callsign**.
- **All:** you get **every** text message on the frequencies you're monitoring (can be a lot in busy areas).

Voice is never relayed in either mode; only text messages.

---

## Settings file (`vPilotRelay.ini`)

```ini
[Telegram]
BotToken=          ; token from @BotFather
ChatId=            ; Chat ID the bot tells you

[Relay]
Private=true       ; private messages
Radio=true         ; frequency text messages
RadioMode=mentions ; mentions = mentions only, all = everything
Selcal=true        ; SELCAL
Broadcast=true     ; broadcast messages
Connection=true    ; connected / disconnected
```

Changes take effect after restarting vPilot. "Silent mode" and "Radio alerts" changed from the menu apply to the current session.

---

## Troubleshooting

| Problem | Fix |
|---|---|
| The bot doesn't answer at all | Is vPilot running? The bot only answers while vPilot is running. |
| No "is running" message | Run the setup wizard again. If you installed manually: are `BotToken` and `ChatId` correct? Is the DLL unblocked? |
| The plugin doesn't load | If `RossCarlson.Vatsim.Vpilot.Plugins.dll` or `.xml` is in the `Plugins` folder, delete it (some plugin installers leave it there and it breaks vPilot plugins). |
| Error details | Look for lines starting with `[vPilot Relay]` in vPilot's message/debug window. |
| ⚠️ "The simulator shows a different frequency" | Some add-on aircraft use their own radio system and ignore external frequency/squawk commands. Tune from the cockpit instead. |
| "Could not connect to the simulator" | The simulator must be running with a flight loaded. |

**Known limitations**

- vPilot's plugin interface can't change frequencies; frequency and squawk are sent to the simulator directly via SimConnect.
- The "Online ATC" list is made of the controllers vPilot sees in range.
- Messages sent to the bot while vPilot is closed are **not** sent to controllers when vPilot starts (so old messages aren't sent by mistake).

---

## Building from source

Requirements: [.NET SDK](https://dotnet.microsoft.com/download) 8 or later and vPilot installed on the machine (vPilot's DLLs are referenced, not included in the repository).

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

Output:

```
dist\vPilotRelay-<version>\     vPilotRelay-Setup-<version>.exe, vPilotRelay.dll, vPilotRelay.ini, README.md
dist\vPilotRelay-<version>.zip
```

If vPilot is in a different folder: `build.ps1 -VpilotDir "D:\vPilot"`

The version number lives in `Directory.Build.props` (shared by the plugin and the setup wizard).

To build only the plugin: `dotnet build vPilotRelay.csproj -c Release`

### Project layout

| File | Purpose |
|---|---|
| `Main.cs` | Plugin entry point (`IPlugin`), vPilot events, controller list |
| `Bot.cs` | Telegram menu, buttons, commands, reply routing |
| `TelegramClient.cs` | Telegram Bot API (long polling, sending/editing messages) |
| `SimRadio.cs` | SimConnect: COM frequencies, squawk, aircraft state |
| `Settings.cs` | Reads `vPilotRelay.ini`, recent connections (`vPilotRelay.recent`) |
| `Installer\` | Setup wizard (WPF, single `.exe`; plugin DLL and ini template embedded) |
| `Installer\MainWindow.xaml(.cs)` | Wizard pages and flow |
| `Installer\TelegramApi.cs` | Token check (`getMe`), Chat ID detection (`getUpdates`), test message |
| `Installer\Vpilot.cs`, `Installer\Payload.cs` | Finding/closing vPilot, copying the plugin, writing the ini while keeping existing settings |
| `Installer\Assets\make-assets.ps1` | Generates the wizard's images and icon from the original logos (not in this repository; the generated images are) |

---

## License

Copyright © 2026 AG Simulations

vPilot Relay is free software: you can redistribute it and/or modify it under the terms of the
GNU General Public License as published by the Free Software Foundation, either version 3 of the
License, or (at your option) any later version. It is distributed in the hope that it will be useful,
but **without any warranty**; see the [LICENSE](LICENSE) file for details.

**Additional permission under GNU GPL version 3 section 7:** if you modify this program, or any
covered work, by linking or combining it with the vPilot plugin API (`RossCarlson.Vatsim.Vpilot.Plugins`),
Microsoft Flight Simulator SimConnect (`Microsoft.FlightSimulator.SimConnect`), or a modified version
of those libraries, containing parts covered by the terms of their respective licenses, AG Simulations
grants you additional permission to convey the resulting work.

**Trademarks:** the vPilot Relay logo and the AG Simulations / AG Sim logo are trademarks of
AG Simulations and are **not** covered by the GPL. If you distribute a modified version, please use
your own name and artwork.

vPilot is developed by Ross Carlson; this project is not affiliated with vPilot, VATSIM or Telegram.

---

Inspired by [blt950/vPilot-Pushover](https://github.com/blt950/vPilot-Pushover)
