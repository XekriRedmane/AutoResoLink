// AutoResoLink started as a port of ResoniteLinkPlus by hazre
// (https://github.com/hazre/ResoniteLinkPlus) from BepisLoader to the Resonite Mod
// Loader. ResoniteLinkPlus is used under the MIT License. See LICENSE.

using FrooxEngine;
using HarmonyLib;
using ResoniteModLoader;

namespace AutoResoLink;

// Represents the mod. It adds ResoniteLink controls to the New World dialog, starts
// ResoniteLink on the lowest available port at or above a configurable starting
// port whenever no port is specified, and lists the running ResoniteLink sessions
// in a JSON file (see SessionFile).
public class AutoResoLinkMod : ResoniteMod {
	public override string Name => "AutoResoLink";
	public override string Author => "Robert Baruch";
	public override string Version => "1.0.0";
	public override string Link => "https://github.com/XekriRedmane/AutoResoLink";

	// 49152 is the first port of the IANA dynamic/private range.
	internal const int DefaultStartingPort = 49152;

	[AutoRegisterConfigKey]
	static readonly ModConfigurationKey<bool> AutoEnableKey = new(
		"auto_enable",
		"Check the ResoniteLink box in the New World dialog by default.",
		() => false);

	[AutoRegisterConfigKey]
	static readonly ModConfigurationKey<int> StartingPortKey = new(
		"starting_port",
		"The first port to try for ResoniteLink. Ports that are in use are skipped.",
		() => DefaultStartingPort,
		valueValidator: port => port >= 1 && port <= 65535);

	static ModConfiguration? config;

	// Whether the ResoniteLink checkbox in a new New World dialog starts out checked.
	internal static bool AutoEnable => config?.GetValue(AutoEnableKey) ?? false;

	// The lowest port that this mod chooses for ResoniteLink, from 1 to 65535.
	internal static int StartingPort => config?.GetValue(StartingPortKey) ?? DefaultStartingPort;

	public override void OnEngineInit() {
		config = GetConfiguration();
		// Writes the defaults to rml_config so that they can be found and edited.
		config?.Save(true);
		// The Resonite Mod Manifest requires the Harmony ID to equal the mod's
		// manifest ID.
		new Harmony("Xekri.AutoResoLink").PatchAll();
		Engine.Current.RunPostInit(SessionFile.Start);
	}
}
