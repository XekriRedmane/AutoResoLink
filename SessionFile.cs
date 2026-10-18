using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using FrooxEngine;
using ResoniteModLoader;

namespace AutoResoLink;

// Maintains a JSON file that lists the worlds in this Resonite process that are
// running ResoniteLink, so that an external program can find their ports without
// reading Resonite's logs.
//
// Each Resonite process writes its own file, resonite-<pid>.json, in an
// AutoResoLink folder under the OS's local application data folder. That is
// %LOCALAPPDATA% on Windows and $XDG_DATA_HOME or ~/.local/share on Linux. A file
// looks like this.
//
//   {
//     "processId": 34692,
//     "processStartTime": "2026-10-17T23:56:58.6090000Z",
//     "sessions": [
//       {
//         "sessionId": "S-0199f3b2-...",
//         "name": "Xekri's World",
//         "port": 49152
//       }
//     ]
//   }
//
// The name has its rich text tags removed. The file is replaced by a rename, so a
// reader never sees a partly written file. It is deleted when Resonite shuts down
// normally. A file left behind by a crash is deleted the next time Resonite starts,
// and until then a reader can recognize it because no process with processId
// started at processStartTime.
internal static class SessionFile {
	// How often the list of sessions is rebuilt. The file is rewritten only when the
	// list changes, so a change shows up in the file within this many seconds.
	const float PollSeconds = 1f;

	// Process start times read at different moments can differ slightly on some
	// platforms, so start times this close together are treated as equal.
	static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(1);

	static readonly JsonSerializerOptions JsonOptions = new() {
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		// The default encoder escapes apostrophes and all non-ASCII text, which is
		// only needed for JSON embedded in HTML. The relaxed one leaves world names
		// readable.
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	internal sealed record Session(string SessionId, string Name, int Port);

	internal sealed record Contents(int ProcessId, DateTime ProcessStartTime, List<Session> Sessions);

	// The file this process writes, or null if it could not be set up.
	static string? path;

	static int processId;
	static DateTime processStartTime;

	// The JSON most recently written to path, or null if the last write failed.
	static string? written;

	static bool shuttingDown;

	// Whether an error has been logged since the last successful update, so that a
	// persistent failure is logged once and not every second.
	static bool errorLogged;

	// Sets up the file and starts updating it. Must be called after the engine has
	// finished initializing, since it uses the global coroutine manager.
	public static void Start() {
		try {
			string root = Environment.GetFolderPath(
				Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
			if (string.IsNullOrEmpty(root)) {
				ResoniteMod.Warn("This OS has no local application data folder, so no ResoniteLink session file will be written");
				return;
			}
			string dir = Path.Combine(root, "AutoResoLink");
			Directory.CreateDirectory(dir);
			using (Process self = Process.GetCurrentProcess()) {
				processId = self.Id;
				processStartTime = self.StartTime.ToUniversalTime();
			}
			DeleteStaleFiles(dir);
			path = Path.Combine(dir, $"resonite-{processId}.json");
			ResoniteMod.Msg($"Writing ResoniteLink sessions to {path}");
		} catch (Exception e) {
			ResoniteMod.Error($"Could not set up the ResoniteLink session file: {e}");
			return;
		}
		Engine.Current.OnShutdown += Delete;
		Poll();
	}

	static void Poll() {
		if (shuttingDown) {
			return;
		}
		try {
			Update();
			errorLogged = false;
		} catch (Exception e) {
			written = null;
			if (!errorLogged) {
				ResoniteMod.Error($"Could not update {path}: {e}");
				errorLogged = true;
			}
		}
		Engine.Current.GlobalCoroutineManager.RunInSeconds(PollSeconds, Poll);
	}

	static void Update() {
		if (path == null) {
			return;
		}
		var worlds = new List<World>();
		Engine.Current.WorldManager.GetWorlds(worlds);
		var sessions = new List<Session>();
		foreach (World world in worlds) {
			if (world.IsDestroyed || world.IsDisposed) {
				continue;
			}
			// World.StartResoniteLink clears ResoniteLink if the server fails to start,
			// so a ResoniteLink with a port is one that is running.
			int port = world.ResoniteLink?.Port ?? -1;
			if (port > 0) {
				sessions.Add(new Session(world.SessionId, world.RawName, port));
			}
		}
		string json = JsonSerializer.Serialize(new Contents(processId, processStartTime, sessions), JsonOptions);
		if (json == written) {
			return;
		}
		string temp = path + ".tmp";
		File.WriteAllText(temp, json);
		File.Move(temp, path, overwrite: true);
		written = json;
	}

	static void Delete() {
		shuttingDown = true;
		if (path == null) {
			return;
		}
		try {
			File.Delete(path);
		} catch (Exception e) {
			ResoniteMod.Warn($"Could not delete {path}: {e.Message}");
		}
	}

	// Deletes each resonite-<pid>.json in dir whose process is no longer running. A
	// file whose process ID has since been reused is recognized by its start time.
	static void DeleteStaleFiles(string dir) {
		foreach (string file in Directory.GetFiles(dir, "resonite-*.json")) {
			try {
				Contents? contents = JsonSerializer.Deserialize<Contents>(File.ReadAllText(file), JsonOptions);
				if (contents == null || !IsRunning(contents.ProcessId, contents.ProcessStartTime)) {
					File.Delete(file);
				}
			} catch (Exception e) {
				ResoniteMod.Warn($"Could not check {file} for staleness: {e.Message}");
			}
		}
	}

	// Returns true if a process with the ID is running and started at startTime.
	//
	// Raises:
	//   Any exception from reading the process's start time other than the two
	//   handled here, such as Win32Exception when access to the process is denied.
	//   DeleteStaleFiles logs it and leaves the file alone.
	static bool IsRunning(int id, DateTime startTime) {
		try {
			using Process process = Process.GetProcessById(id);
			TimeSpan difference = process.StartTime.ToUniversalTime() - startTime;
			return difference.Duration() <= StartTimeTolerance;
		} catch (ArgumentException) {
			// GetProcessById throws this when no process has the ID.
			return false;
		} catch (InvalidOperationException) {
			// The process exited between the lookup and reading its start time.
			return false;
		}
	}
}
