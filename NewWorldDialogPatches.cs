// The approach of adding controls to NewWorldDialog after it builds its UI comes
// from ResoniteLinkPlus by hazre (https://github.com/hazre/ResoniteLinkPlus), which
// credits ResonitePluginSelect by Nytra (https://github.com/Nytra/ResonitePluginSelect).

using System.Runtime.CompilerServices;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using HarmonyLib;
using ResoniteModLoader;

namespace AutoResoLink;

// Adds a row to the session settings column of the New World dialog.
//
//   Enable ResoniteLink [x]   Port:   [49152]
//
// The row goes just above the Start Session button. Pressing Start Session with the
// box checked starts ResoniteLink in the new world.
[HarmonyPatch(typeof(NewWorldDialog))]
internal static class NewWorldDialogPatches {
	// The controls added to one dialog.
	sealed class LinkControls {
		public Checkbox? Enable;
		public TextField? Port;

		// The port written into the field when it was built. A field that still holds
		// it at Start Session means the user did not choose a port.
		public int SuggestedPort;
	}

	// A dialog appears here once its controls have been attempted, even if building
	// them failed, so that a failure is not retried every update.
	static readonly ConditionalWeakTable<NewWorldDialog, LinkControls> Dialogs = new();

	// BuildUI_Async sets _initialized after adding the Start Session button, which is
	// the last element of the dialog.
	static readonly AccessTools.FieldRef<NewWorldDialog, Sync<bool>> Initialized =
		AccessTools.FieldRefAccess<NewWorldDialog, Sync<bool>>("_initialized");

	static readonly AccessTools.FieldRef<NewWorldDialog, SyncRef<Checkbox>> MobileFriendly =
		AccessTools.FieldRefAccess<NewWorldDialog, SyncRef<Checkbox>>("_mobileFriendly");

	// Builds the controls once the dialog's own UI exists, and then keeps the port
	// field enabled only while the box is checked.
	//
	// NewWorldDialog is marked ExceptionAction.DestroySlot, so an exception escaping
	// from here would close the dialog. Every exception is caught and logged.
	[HarmonyPostfix]
	[HarmonyPatch("OnCommonUpdate")]
	static void OnCommonUpdatePostfix(NewWorldDialog __instance) {
		try {
			if (Dialogs.TryGetValue(__instance, out LinkControls? controls)) {
				if (controls.Enable != null && controls.Port != null) {
					Button? button = controls.Port.Slot.GetComponent<Button>();
					if (button != null) {
						button.Enabled = controls.Enable.IsChecked;
					}
				}
				return;
			}
			if (!Initialized(__instance).Value) {
				return;
			}
			controls = new LinkControls();
			Dialogs.Add(__instance, controls);
			BuildControls(__instance, controls);
		} catch (Exception e) {
			ResoniteMod.Error($"Failed to update the New World dialog: {e}");
		}
	}

	static void BuildControls(NewWorldDialog dialog, LinkControls controls) {
		// The session settings column is the nearest VerticalLayout above the Mobile
		// Friendly checkbox, since UIBuilder's Checkbox and Panel add no layout
		// components of their own.
		Slot? column = MobileFriendly(dialog).Target?.Slot.GetComponentInParents<VerticalLayout>()?.Slot;
		if (column == null) {
			ResoniteMod.Warn("Could not find the session settings in the New World dialog, so no ResoniteLink controls were added");
			return;
		}
		Slot? startButton = FindStartSessionButton(column);

		var ui = new UIBuilder(column);
		RadiantUI_Constants.SetupDefaultStyle(ui);
		// Matches the style BuildUI_Async uses for this column.
		ui.Style.MinHeight = 32f;
		ui.Style.PreferredHeight = 32f;

		ui.HorizontalLayout(4f);
		controls.Enable = ui.Checkbox("World.Actions.EnableResoniteLink".AsLocaleKey(), state: AutoResoLinkMod.AutoEnableKey.Value);
		ui.Text("NewWorld.Port".AsLocaleKey());
		int startingPort = AutoResoLinkMod.StartingPortKey.Value;
		controls.SuggestedPort = LinkPorts.FindAvailable(startingPort) ?? startingPort;
		controls.Port = ui.TextField(controls.SuggestedPort.ToString());
		ui.NestOut();

		// Children are ordered by OrderOffset and then by creation, so the new row
		// would otherwise land below the Start Session button.
		if (startButton != null) {
			long last = 0;
			foreach (Slot child in column.Children) {
				if (child != startButton) {
					last = Math.Max(last, child.OrderOffset);
				}
			}
			startButton.OrderOffset = last + 100;
		}
	}

	// Returns the child of column that holds the Start Session button, or null.
	static Slot? FindStartSessionButton(Slot column) {
		foreach (Slot child in column.Children) {
			Button? button = child.GetComponent<Button>();
			if (button?.Pressed.Target?.Method.Name == "OnStartSession") {
				return child;
			}
		}
		return null;
	}

	// Records a LinkRequest for the world that this press creates, if the box is
	// checked.
	//
	// The press is cancelled, as the game does for a bad session port, if the port
	// field does not hold a port number, or if it holds a port the user typed that
	// is already in use. The field flashes red in either case.
	[HarmonyPrefix]
	[HarmonyPatch("OnStartSession")]
	static bool OnStartSessionPrefix(NewWorldDialog __instance) {
		LinkRequest.Pending.Value = null;
		try {
			if (!Dialogs.TryGetValue(__instance, out LinkControls? controls) ||
					controls.Enable == null || controls.Port == null || !controls.Enable.IsChecked) {
				return true;
			}
			if (!ushort.TryParse(controls.Port.TargetString, out ushort port) || port == 0) {
				FlashRed(controls.Port);
				return false;
			}
			bool chosen = port != controls.SuggestedPort;
			if (chosen && !LinkPorts.IsAvailable(port)) {
				ResoniteMod.Warn($"Port {port} is in use, so it cannot be used for ResoniteLink");
				FlashRed(controls.Port);
				return false;
			}
			LinkRequest.Pending.Value = new LinkRequest(port, chosen);
		} catch (Exception e) {
			ResoniteMod.Error($"Failed to read the ResoniteLink settings from the New World dialog: {e}");
		}
		return true;
	}

	// Clears the request from the press's own context. Any task that the press
	// started keeps its own copy.
	[HarmonyPostfix]
	[HarmonyPatch("OnStartSession")]
	static void OnStartSessionPostfix() {
		LinkRequest.Pending.Value = null;
	}

	// Copied from the game's handling of an invalid session port in OnStartSession.
	static void FlashRed(TextField field) {
		field.Slot.GetComponentInChildren<Button>().ColorDrivers[0].NormalColor.TweenFromTo(colorX.Red, colorX.White, 1f);
	}
}
