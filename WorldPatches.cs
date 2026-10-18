using FrooxEngine;
using HarmonyLib;
using ResoniteModLoader;

namespace AutoResoLink;

// Represents a press of Start Session in a New World dialog with the ResoniteLink
// box checked. The world that the press creates gets ResoniteLink on Port.
internal sealed class LinkRequest(int port, bool chosen) {
	// The request made by the current Start Session press, or null.
	//
	// A dialog opened from a saved world's orb creates the world in an async task
	// that outlives the press. An AsyncLocal value flows into that task, and into
	// nothing started after the press, which is the same mechanism the game uses
	// for CoroutineManager.Manager.
	public static readonly AsyncLocal<LinkRequest?> Pending = new();

	public readonly int Port = port;

	// True if the user typed Port. False if Port is the one the dialog suggested,
	// in which case another free port may be substituted.
	public readonly bool Chosen = chosen;

	// Set by the first world that claims this request, so that no other world
	// created in the same task gets ResoniteLink.
	public bool Claimed;
}

// Makes World.StartResoniteLink() with no port use the lowest available port at or
// above the configured starting port. The game would otherwise pick a random port
// with RandomX.Range(2000, 65535), retrying until one works. This covers the
// Enable ResoniteLink button in the Session tab as well as this mod's own calls.
[HarmonyPatch(typeof(World), nameof(World.StartResoniteLink))]
internal static class WorldStartResoniteLinkPatch {
	static bool Prefix(World __instance, int? port, ref bool __result) {
		if (port.HasValue) {
			return true;
		}
		// StartOnAvailablePort calls StartResoniteLink again with a port, which runs
		// the original method.
		__result = LinkPorts.StartOnAvailablePort(__instance, null);
		return false;
	}
}

// Starts ResoniteLink in a world created by a Start Session press that requested
// it. Every hosted session, whether new or loaded from a saved world, is created
// by World.StartSession.
[HarmonyPatch(typeof(World), nameof(World.StartSession))]
internal static class WorldStartSessionPatch {
	// The number of world updates to wait for the world's permissions to allow
	// ResoniteLink. The world's init action or saved data creates the permission
	// components during its first update, so one or two updates normally suffice.
	// The limit ends the wait for a world whose permissions forbid ResoniteLink.
	const int MaxWaitUpdates = 600;

	static void Postfix(World __result) {
		LinkRequest? request = LinkRequest.Pending.Value;
		if (request == null || request.Claimed || __result == null) {
			return;
		}
		request.Claimed = true;
		World world = __result;
		// StartSession runs on the Userspace world's thread, and the new world has not
		// had its first update yet, so the work is queued onto the new world's update.
		world.RunSynchronously(() => StartWhenAllowed(world, request, MaxWaitUpdates));
	}

	static void StartWhenAllowed(World world, LinkRequest request, int updatesLeft) {
		if (world.IsDestroyed || world.IsDisposed || world.ResoniteLink != null) {
			return;
		}
		if (!world.IsAllowedToRunResoniteLink()) {
			if (updatesLeft <= 0) {
				ResoniteMod.Warn($"World '{world.RawName}' does not allow ResoniteLink, so it was not started");
				return;
			}
			world.RunInUpdates(1, () => StartWhenAllowed(world, request, updatesLeft - 1));
			return;
		}
		try {
			bool started = request.Chosen
				? world.StartResoniteLink(request.Port)
				: LinkPorts.StartOnAvailablePort(world, request.Port);
			if (started) {
				ResoniteMod.Msg($"Started ResoniteLink on port {world.ResoniteLink?.Port} for world '{world.RawName}'");
			} else {
				ResoniteMod.Error($"Could not start ResoniteLink for world '{world.RawName}'");
			}
		} catch (Exception e) {
			ResoniteMod.Error($"Could not start ResoniteLink for world '{world.RawName}': {e}");
		}
	}
}
