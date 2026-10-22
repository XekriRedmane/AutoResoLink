using System.Net;
using System.Net.Sockets;
using FrooxEngine;
using ResoniteModLoader;

namespace AutoResoLink;

// Chooses TCP ports for ResoniteLink and starts ResoniteLink on them.
internal static class LinkPorts {
	// The number of ports StartOnAvailablePort tries before giving up. A port that
	// probes as free can be taken by another process before ResoniteLink binds it,
	// so a failed start moves on to the next free port.
	const int MaxStartAttempts = 10;

	// Returns true if no world in this process runs ResoniteLink on the port, and a
	// TCP listener can bind the port on both the IPv4 loopback and wildcard
	// addresses.
	//
	// ResoniteLink's WatsonWsServer listens on http://localhost:<port>/ through
	// HttpListener. Probing both addresses detects a process that holds the port on
	// either one.
	public static bool IsAvailable(int port) {
		if (port < 1 || port > 65535) {
			return false;
		}
		return !PortsUsedByWorlds().Contains(port) && CanBind(port);
	}

	// Returns the lowest port from first through 65535 that IsAvailable accepts, or
	// null if there is none.
	//
	// This probes one port at a time, so it is O(N) in the number of taken ports
	// above first. That number is normally zero or a handful.
	public static int? FindAvailable(int first) {
		HashSet<int> used = PortsUsedByWorlds();
		for (int port = Math.Max(first, 1); port <= 65535; port++) {
			if (!used.Contains(port) && CanBind(port)) {
				return port;
			}
		}
		return null;
	}

	// Starts ResoniteLink in the world. The preferred port is tried first if it is
	// available. Otherwise, the port is the lowest available one at or above the
	// configured starting port.
	//
	// Returns:
	//   True if ResoniteLink started, false if every attempt failed.
	//
	// Raises:
	//   InvalidOperationException: from World.StartResoniteLink, if ResoniteLink is
	//   already running in the world, the world is not hosted here, or the world's
	//   permissions do not allow ResoniteLink.
	public static bool StartOnAvailablePort(World world, int? preferred) {
		int first = AutoResoLinkMod.StartingPortKey.Value;
		for (int attempt = 0; attempt < MaxStartAttempts; attempt++) {
			int? port = null;
			if (attempt == 0 && preferred.HasValue && IsAvailable(preferred.Value)) {
				port = preferred;
			}
			port ??= FindAvailable(first);
			if (port == null) {
				ResoniteMod.Error($"No available port for ResoniteLink from {first} through 65535");
				return false;
			}
			// A port is given, so this call runs the game's own StartResoniteLink. See
			// WorldStartResoniteLinkPatch.
			if (world.StartResoniteLink(port.Value)) {
				return true;
			}
			ResoniteMod.Warn($"ResoniteLink failed to start on port {port.Value}, trying the next free port");
			first = port.Value + 1;
		}
		ResoniteMod.Error($"ResoniteLink failed to start after {MaxStartAttempts} attempts");
		return false;
	}

	static HashSet<int> PortsUsedByWorlds() {
		var worlds = new List<World>();
		Engine.Current.WorldManager.GetWorlds(worlds);
		var ports = new HashSet<int>();
		foreach (World world in worlds) {
			int port = world.ResoniteLink?.Port ?? -1;
			if (port > 0) {
				ports.Add(port);
			}
		}
		return ports;
	}

	static bool CanBind(int port) {
		return CanBind(IPAddress.Loopback, port) && CanBind(IPAddress.Any, port);
	}

	static bool CanBind(IPAddress address, int port) {
		var listener = new TcpListener(address, port);
		// On Windows, a socket without exclusive use can sometimes bind beside
		// another socket that holds the same port on a different address. With
		// exclusive use, the bind fails if any socket holds the port. This is from
		// Microsoft's "Using SO_REUSEADDR and SO_EXCLUSIVEADDRUSE". The option is
		// Windows-only, and on Linux a plain bind already fails in that case.
		if (OperatingSystem.IsWindows()) {
			listener.ExclusiveAddressUse = true;
		}
		try {
			listener.Start();
			return true;
		} catch (SocketException e) when (
				e.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied) {
			// AccessDenied is what Windows returns for a port in an excluded range,
			// such as one reserved by Hyper-V.
			return false;
		} catch (SocketException) {
			// Any other error is not evidence that the port is taken. If the port
			// cannot be used after all, StartOnAvailablePort moves on to the next one.
			return true;
		} finally {
			listener.Stop();
		}
	}
}
