// Copyright (c) 2026 Aelita Styles
// Licensed under the MIT License. See LICENSE in the project root for details.

using System.Collections.Generic;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using HarmonyLib;
using UnityEngine;

namespace SprayerMkII;

/// <summary>
/// Scales spray paint can capacity and applies configured Spray Gun efficiency on prefab load.
/// </summary>
[HarmonyPatch(typeof(Thing), nameof(Thing.OnPrefabLoad))]
internal static class PrefabPatches {
	internal const string PrimarySlotKey = "PrimaryPaint";
	internal const string SecondarySlotKey = "SecondaryPaint";
	internal const string PrimarySlotDisplayName = "Primary Paint";
	internal const string SecondarySlotDisplayName = "Secondary Paint";

	private static readonly Dictionary<int, float> BaseCanCapacities = new();
	private static readonly List<SprayCan> CanPrefabs = new();
	private static readonly List<SprayGun> GunPrefabs = new();

	internal static void Reset() {
		CanPrefabs.Clear();
		GunPrefabs.Clear();
	}

	[HarmonyPostfix]
	private static void Postfix(Thing __instance) {
		if (__instance is SprayCan can) {
			ApplyCanCapacity(can);
		} else if (__instance is SprayGun gun) {
			ApplyGunEfficiency(gun);
		}
	}

	internal static void RefreshSprayCans() {
		foreach (var can in CanPrefabs) {
			if (can != null) {
				ApplyCanCapacity(can);
			}
		}
	}

	internal static void RefreshSprayGuns() {
		foreach (var gun in GunPrefabs) {
			if (gun != null) {
				ApplyGunEfficiency(gun);
			}
		}
	}

	private static void ApplyCanCapacity(SprayCan can) {
		if (!BaseCanCapacities.TryGetValue(can.PrefabHash, out var baseMax)) {
			baseMax = can.MaxQuantity;
			BaseCanCapacities[can.PrefabHash] = baseMax;
			CanPrefabs.Add(can);
		}

		var multiplier = SprayerMkIIPlugin.GetSanitizedCanCapacityMultiplier();
		var scaledMax = Mathf.Max(baseMax, baseMax * multiplier);

		can.MaxQuantity = scaledMax;
		can.quantity = scaledMax;
	}

	internal static void ApplyGunEfficiency(SprayGun gun) {
		if (!GunPrefabs.Contains(gun)) {
			GunPrefabs.Add(gun);
		}

		gun.Efficiency = SprayerMkIIPlugin.GetSanitizedSprayGunEfficiency();

		// Copy spray audio from a standard paint can so network spraying plays sound while active.
		var templateCan = Prefab.Find<SprayCan>("ItemSprayCanWhite");
		if (templateCan != null) {
			if (string.IsNullOrEmpty(gun.UsingSound) && !string.IsNullOrEmpty(templateCan.UsingSound)) {
				gun.UsingSound = templateCan.UsingSound;
			}
			if (string.IsNullOrEmpty(gun.UseCompleteSound) && !string.IsNullOrEmpty(templateCan.UseCompleteSound)) {
				gun.UseCompleteSound = templateCan.UseCompleteSound;
			}
		}
	}

	internal static SprayCan GetSecondarySprayCan(this SprayGun gun) {
		if (gun?.Slots == null) {
			return null;
		}

		foreach (var slot in gun.Slots) {
			if (slot.StringKey == SecondarySlotKey) {
				return slot.Get<SprayCan>();
			}
		}

		return null;
	}
}

[HarmonyPatch(typeof(Slot), nameof(Slot.DisplayName), MethodType.Getter)]
internal static class SlotDisplayNamePatch {
	[HarmonyPrefix]
	private static bool Prefix(Slot __instance, ref string __result) {
		if (__instance.StringKey == PrefabPatches.PrimarySlotKey) {
			__result = PrefabPatches.PrimarySlotDisplayName;
			return false;
		}

		if (__instance.StringKey == PrefabPatches.SecondarySlotKey) {
			__result = PrefabPatches.SecondarySlotDisplayName;
			return false;
		}

		return true;
	}
}

/// <summary>
/// Falls back to the secondary paint can only when the primary slot has no can at all,
/// so a lone can in either slot works while an empty primary can still blocks painting.
/// </summary>
[HarmonyPatch(typeof(SprayGun), nameof(SprayGun.SprayCan), MethodType.Getter)]
internal static class SprayGunSprayCanPatch {
	[HarmonyPostfix]
	private static void Postfix(SprayGun __instance, ref SprayCan __result) {
		if (__result != null) {
			return;
		}

		var secondary = __instance.GetSecondarySprayCan();
		if (secondary != null) {
			__result = secondary;
		}
	}
}

/// <summary>
/// Vanilla SprayGun.GetUseAmount() divides by Efficiency once, and SprayGun.OnUseItem()
/// divides by Efficiency a second time. Skip the duplicate division so Efficiency matches config.
/// </summary>
[HarmonyPatch(typeof(SprayGun), nameof(SprayGun.OnUseItem))]
internal static class SprayGunOnUseItemPatch {
	[HarmonyPrefix]
	private static bool Prefix(SprayGun __instance, float quantity, Thing useOnThing, ref bool __result) {
		var can = __instance.IsOperable ? __instance.SprayCan : null;
		__result = can != null && can.OnUseItem(quantity, useOnThing);
		return false;
	}
}
