// Copyright (c) 2026 Aelita Styles
// Licensed under the MIT License. See LICENSE in the project root for details.

using System;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;
using Reagents;
using UnityEngine;

namespace SprayerMkII;

/// <summary>
/// Registers display strings and the Tier 2 Tool Manufactory recipe for Spray Gun MkII.
/// </summary>
internal static class MkIIRegistration {
	internal const string DisplayName = "Spray Gun MkII";
	internal const string Description =
		"Holds a primary and secondary {SLOT:SprayCan} side by side to paint whole networks in stripes.";

	internal static void Initialize() {
		Localization.OnLanguageChanged += RegisterLocalization;
		RegisterLocalization();
	}

	internal static void RegisterLocalization() {
		var entry = new Localization.LocalizationThingDat {
			PrefabName = DisplayName,
			Description = Description
		};

		if (Localization.ThingLocalized != null) {
			Localization.ThingLocalized[MkIIPrefabFactory.PrefabHash] = entry;
		}

		if (Localization.FallbackThingsLocalized != null) {
			Localization.FallbackThingsLocalized[MkIIPrefabFactory.PrefabHash] = entry;
		}

		var primarySlotHash = Animator.StringToHash(PrefabPatches.PrimarySlotKey);
		var secondarySlotHash = Animator.StringToHash(PrefabPatches.SecondarySlotKey);
		if (Localization.SlotsName != null) {
			Localization.SlotsName[primarySlotHash] = PrefabPatches.PrimarySlotDisplayName;
			Localization.SlotsName[secondarySlotHash] = PrefabPatches.SecondarySlotDisplayName;
		}

		if (Localization.FallbackSlotsName != null) {
			Localization.FallbackSlotsName[primarySlotHash] = PrefabPatches.PrimarySlotDisplayName;
			Localization.FallbackSlotsName[secondarySlotHash] = PrefabPatches.SecondarySlotDisplayName;
		}
	}

	internal static void RegisterRecipe() {
		var recipes = ToolManufactory.RecipeComparable;
		if (recipes == null) {
			SprayerMkIIPlugin.Log.LogWarning("ToolManufactory.RecipeComparable is null; skipping MkII recipe.");
			return;
		}

		if (recipes.AllRecipes != null) {
			foreach (var existing in recipes.AllRecipes) {
				if (existing.Key?.PrefabName == MkIIPrefabFactory.PrefabName) {
					return;
				}
			}
		}

		var recipeData = new WorldManager.RecipeData {
			PrefabName = MkIIPrefabFactory.PrefabName,
			RecipeTier = MachineTier.TierTwo,
			Recipe = new Recipe {
				Steel = 10f,
				Electrum = 5f,
				Plastic = 5f,
				Time = 10f,
				Energy = 500f
			}
		};

		recipes.AddRecipe(recipeData, null);
		recipes.GenerateRecipieList();
		SprayerMkIIPlugin.Log.LogInfo($"Registered Tier 2 Tool Manufactory recipe for {MkIIPrefabFactory.PrefabName}.");
	}
}

[HarmonyPatch(typeof(Localization), nameof(Localization.LoadLanguageFilesFromXml))]
internal static class LocalizationLoadPatch {
	[HarmonyPostfix]
	private static void Postfix() => MkIIRegistration.RegisterLocalization();
}

[HarmonyPatch(typeof(WorldManager), nameof(WorldManager.AfterLoadDataFiles))]
internal static class WorldManagerAfterLoadDataFilesPatch {
	[HarmonyPrefix]
	private static void Prefix() {
		try {
			MkIIPrefabFactory.EnsureRegistered();
			MkIIRegistration.RegisterLocalization();
			MkIIRegistration.RegisterRecipe();
		} catch (Exception ex) {
			SprayerMkIIPlugin.Log.LogError($"Failed to register MkII recipe/localisation: {ex}");
		}
	}
}
