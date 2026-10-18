# AutoResoLink

AutoResoLink is a [Resonite](https://resonite.com/) mod for the [Resonite Mod Loader](https://github.com/resonite-modding-group/ResoniteModLoader) (RML) that lets you automatically turn on ResoniteLink when you start a world. It has been built against Resonite 2026.9.18.82 and RML 5.0.1.

## Details

The New World dialog gets an extra row above the Start Session button, with an "Enable ResoniteLink" checkbox and a port field. If the box is checked when you press Start Session, ResoniteLink starts in the new world on that port. The same row appears when you open a saved world from its orb, since that uses the same dialog.

The port field is filled in with the *lowest free port* at or above the starting port, which is 49152 by default. That default can be changed in the `starting_port` [setting in the mod's config file](#configuration). 49152 is the start of the range suggested by RFC 6335 and the Internet Assigned Numbers Authority for dynamic or private ports. We detect that a port is free if no open world is running ResoniteLink on it and a TCP listener can bind it on both 127.0.0.1 and 0.0.0.0. If you leave the suggested port alone and something else takes it before the world finishes starting, the mod moves on to the next free port. If you type a port yourself and it's in use, the field flashes red and the session doesn't start.

The box to enable ResoniteLink is unchecked by default. Set the `auto_enable` [setting in the mod's config file](#configuration) to `true` if you want ResoniteLink to be automatically started whenever you create a new world from the New World dialog.

The mod also changes the Enable ResoniteLink button in the Session tab so that it also uses the lowest free port at or above `starting_port` instead of randomly choosing a port.

## Finding ResoniteLink sessions from another program

The mod keeps a JSON file listing every world in the running Resonite process that has ResoniteLink running, so a program that wants to connect doesn't have to dig through the logs. The file lives in an `AutoResoLink` folder under the OS's local application data folder.

| OS | Folder |
|---|---|
| Windows | `%LOCALAPPDATA%\AutoResoLink` |
| Linux, native | `$XDG_DATA_HOME/AutoResoLink`, or `~/.local/share/AutoResoLink` if `XDG_DATA_HOME` isn't set |
| Linux, under Proton | `<Steam library>/steamapps/compatdata/2519830/pfx/drive_c/users/steamuser/AppData/Local/AutoResoLink` |

Each Resonite process writes its own file, `resonite-<process ID>.json`, so if you have two copies of Resonite running, you'll have two such files.

### File format

```json
{
  "processId": 34692,
  "processStartTime": "2026-10-17T23:56:58.6090000Z",
  "sessions": [
    {
      "sessionId": "S-0199f3b2-...",
      "name": "Xekri's World",
      "port": 49152
    }
  ]
}
```

The `name` is the world name with its rich text tags stripped. The mod checks the worlds once a second and rewrites the file when something changes, so a ResoniteLink that starts or stops, a world that closes, or a world that gets renamed shows up within about a second. The file is replaced with an atomic rename, so you should never encounter a half-written one.

The file is deleted when Resonite exits normally. If Resonite crashes, the file stays until the next time Resonite starts with the mod. A reader can spot a leftover file because there's probably no running process with that `processId` whose start time is within about a second of `processStartTime`.

### Reading the file from Python

The functions below find the session files of running Resonite processes and read the sessions out of one. Checking a process's start time portably takes [psutil](https://pypi.org/project/psutil/), so you'll need to `pip install psutil`. The code needs Python 3.11 or later, because earlier versions of `datetime.fromisoformat` can't parse the `Z` at the end of `processStartTime`. It was tested with Python 3.13 and psutil 7.2.2 on Windows.

```python
import json
import os
import sys
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path

import psutil  # pip install psutil


@dataclass(frozen=True)
class SessionFile:
    """Represents the session file of a running Resonite process."""

    process_id: int
    # When the process started, in UTC.
    process_start_time: datetime
    path: Path


@dataclass(frozen=True)
class ResoniteLinkSession:
    """Represents a world running ResoniteLink."""

    session_id: str
    # The world name, with rich text tags removed.
    name: str
    # The port ResoniteLink listens on, on localhost.
    port: int


def session_dir() -> Path:
    """Returns the folder that AutoResoLink writes its session files to."""
    if sys.platform == "win32":
        return Path(os.environ["LOCALAPPDATA"]) / "AutoResoLink"
    # This is where .NET puts LocalApplicationData on Linux.
    data_home = os.environ.get("XDG_DATA_HOME") or Path.home() / ".local" / "share"
    return Path(data_home) / "AutoResoLink"


def _is_running(file: SessionFile) -> bool:
    """Returns True if the process that wrote a session file is still running.

    A process ID can be reused after a process exits, so the process's start time
    must also match the one in the file, to within a second.
    """
    try:
        created = psutil.Process(file.process_id).create_time()
    except psutil.Error:
        # NoSuchProcess, or AccessDenied. Resonite normally runs as the current
        # user, so a process that we can't inspect is usually not Resonite, or
        # a Resonite running elevated or as another user, in which case we don't
        # allow it.
        return False
    return abs(created - file.process_start_time.timestamp()) <= 1.0


def _live_files() -> list[tuple[SessionFile, list[ResoniteLinkSession]]]:
    """Returns each session file whose process is running, with its sessions.

    The most recently started process comes first.
    """
    live = []
    for path in session_dir().glob("resonite-*.json"):
        try:
            contents = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            # The file was deleted after glob found it.
            continue
        file = SessionFile(
            process_id=contents["processId"],
            process_start_time=datetime.fromisoformat(contents["processStartTime"]),
            path=path,
        )
        if _is_running(file):
            sessions = [
                ResoniteLinkSession(session_id=s["sessionId"], name=s["name"], port=s["port"])
                for s in contents["sessions"]
            ]
            live.append((file, sessions))
    live.sort(key=lambda item: item[0].process_start_time, reverse=True)
    return live


def session_files() -> list[SessionFile]:
    """Returns the session files of running Resonite processes.

    The file of the most recently started process comes first.
    """
    return [file for file, _ in _live_files()]


def read_sessions(process_id: int = 0) -> list[ResoniteLinkSession]:
    """Returns the ResoniteLink sessions of a running Resonite process.

    Args:
        process_id (int): The ID of the Resonite process, or 0 for the most
            recently started Resonite process that has a session file.

    Returns:
        A ResoniteLinkSession for each world running ResoniteLink. The list is
        empty if no world is running ResoniteLink.

    Raises:
        FileNotFoundError: No running Resonite process with that ID has a session
            file.
    """
    for file, sessions in _live_files():
        if process_id in (0, file.process_id):
            return sessions
    which = "any Resonite process" if process_id == 0 else f"Resonite process {process_id}"
    raise FileNotFoundError(f"No AutoResoLink session file for {which} in {session_dir()}")
```

For example, this prints each accessible running copy of Resonite with its start time, followed by the port, session ID, and name of every world running ResoniteLink in it.

```python
for file in session_files():
    print(f"Resonite process {file.process_id}, started {file.process_start_time:%Y-%m-%d %H:%M:%S} UTC")
    for session in read_sessions(file.process_id):
        print(f"  {session.port}  {session.session_id}  {session.name}")
```

A process can exit between the two calls, in which case `read_sessions` raises `FileNotFoundError`.

If Resonite runs under Proton, the process-ID check only works for a program that also runs under Proton in the same prefix. Wine gives each Windows process its own process ID, which doesn't match the Linux process ID that a native Linux program sees, so from native Linux every file looks stale.

## Installation

### With Resolute

[Resolute](https://github.com/Gawdl3y/Resolute) is a mod manager for RML that installs, updates, and removes mods. It can only install mods that are listed in the [Resonite Mod Manifest](https://github.com/resonite-modding-group/resonite-mod-manifest).

1. Download Resolute from its [Releases](https://github.com/Gawdl3y/Resolute/releases) page, install it, and start it.
2. The first time Resolute starts, it runs a setup guide that asks for your Resonite install folder, installs RML and Harmony for you, and tells you which Steam launch option to add. Follow it to the end.
3. Go to the **Mod Index**, type `AutoResoLink` into the **Filter** box, and press **Install** on AutoResoLink.
4. Start Resonite and check that the mod works, as in step 3 of the manual installation below.

Resolute lists AutoResoLink on its **Installed Mods** page, which is also where you update or remove it.

### Manually

1. Install [RML](https://github.com/resonite-modding-group/ResoniteModLoader#installation).
2. Copy `AutoResoLink.dll` into the `rml_mods` folder in your Resonite install directory.
3. Start Resonite and open the New World dialog. You should see the "Enable ResoniteLink" row above Start Session, and the Resonite log should contain a line from `[ResoniteModLoader/AutoResoLink]` when a world starts with ResoniteLink.

## Configuration

RML saves the settings in `rml_config/AutoResoLink.json`. The mod creates that file with the default values the first time Resonite runs with the mod installed, so you don't have to create it yourself. You can edit them there, or in game with a configuration mod such as [ResoniteModSettings](https://github.com/badhaloninja/ResoniteModSettings).

| Setting | Default | Meaning |
|---|---|---|
| `auto_enable` | `false` | The ResoniteLink checkbox in the New World dialog starts out checked. |
| `starting_port` | `49152` | The lowest port the mod picks for ResoniteLink, from 1 to 65535. |

## Building

The project references `FrooxEngine.dll`, `Elements.Core.dll`, `ResoniteModLoader.dll`, and `0Harmony.dll` straight from a Resonite install with RML in it, so you need one to build.

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
2. If Resonite isn't in the default Steam library, copy `Config.Build.user.props.template` to `Config.Build.user.props` and set `ResonitePath` in it. Setting `CopyToMods` to `true` in the same file copies each build into `rml_mods`.
3. Run `dotnet build -c Release`. The mod ends up in `bin/Release/AutoResoLink.dll`.

## Acknowledgements

AutoResoLink started as a port of [ResoniteLinkPlus](https://github.com/hazre/ResoniteLinkPlus) by hazre, which does the same job for [BepisLoader](https://github.com/ResoniteModding/BepisLoader). ResoniteLinkPlus is under the MIT License, and its copyright notice is in [LICENSE](LICENSE). ResoniteLinkPlus in turn credits [ResonitePluginSelect](https://github.com/Nytra/ResonitePluginSelect) as its reference for patching the New World dialog.

## License

AutoResoLink is under the MIT License. See [LICENSE](LICENSE).
