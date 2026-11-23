// Copyright (c) 2026 Aelita Styles
// Licensed under the MIT License. See LICENSE in the project root for details.

using System;
using System.Globalization;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using StationeersMods.Interface;
using UnityEngine;

namespace SprayerMkII;

/// <summary>
/// BepInEx entry point for Sprayer MkII. Binds and sanitises mod settings,
/// registers localisation/recipe hooks, applies all Harmony patches, and
/// ticks the continuous spray audio loop while whole-network painting is active.
/// </summary>
[StationeersMod(PluginGuid, PluginName, PluginVersion)]
[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class SprayerMkIIPlugin : BaseUnityPlugin {
	public const string PluginGuid = ThisAssembly.ModInfo.AssemblyGuid;
	public const string PluginName = ThisAssembly.ModInfo.AssemblyName;
	public const string PluginVersion = ThisAssembly.ModInfo.Version;
	public const string PluginVersionGit = ThisAssembly.ModInfo.VersionGit;
	public static readonly string PluginBuildTime =
		DateTime.Parse(ThisAssembly.ModInfo.BuiltTime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
			.ToLocalTime()
			.ToString("o", CultureInfo.InvariantCulture);

	internal static ManualLogSource Log { get; private set; }
	internal static ConfigEntry<float> PaintCanCapacityMultiplier { get; private set; }
	internal static ConfigEntry<float> SprayGunEfficiency { get; private set; }
	internal static ConfigEntry<KeyCode> SinglePaintModifierKey { get; private set; }
	internal static ConfigEntry<string> StripePattern { get; private set; }

	private Harmony _harmony;

	internal static float GetSanitizedCanCapacityMultiplier() =>
		SanitizeFloat(PaintCanCapacityMultiplier?.Value ?? 5f, 1f, 100f, 5f);

	internal static float GetSanitizedSprayGunEfficiency(float fallback = 6f) =>
		SanitizeFloat(SprayGunEfficiency?.Value ?? fallback, 1f, 32f, fallback);

	internal static KeyCode GetSanitizedSinglePaintKey() {
		var key = SinglePaintModifierKey?.Value ?? KeyCode.LeftShift;
		return Enum.IsDefined(typeof(KeyCode), key) ? key : KeyCode.LeftShift;
	}

	private static float SanitizeFloat(float value, float min, float max, float fallback) {
		if (float.IsNaN(value) || float.IsInfinity(value)) {
			return fallback;
		}
		return Mathf.Clamp(value, min, max);
	}

	private void Awake() {
		Log = Logger;

		try {
			PaintCanCapacityMultiplier = Config.Bind(
				"Paint Cans",
				"CapacityMultiplier",
				5f,
				new ConfigDescription(
					"Multiplier for spray paint can capacity. At 5x, a standard can holds 100 sprays by hand.",
					new AcceptableValueRange<float>(1f, 100f)
				)
			);

			SprayGunEfficiency = Config.Bind(
				"Spray Gun",
				"Efficiency",
				6f,
				new ConfigDescription(
					"How many times further a paint can goes in a Spray Gun compared to using it by hand. At 6x (with 5x cans), one can gives 600 sprays.",
					new AcceptableValueRange<float>(1f, 32f)
				)
			);

			SinglePaintModifierKey = Config.Bind(
				"Spray Gun",
				"SinglePaintModifierKey",
				KeyCode.LeftShift,
				new ConfigDescription(
					"Hold this key while using a Spray Gun to paint a single segment instead of the whole network."
				)
			);

			StripePattern = Config.Bind(
				"Spray Gun",
				"StripePattern",
				"PPS",
				new ConfigDescription(
					"Repeating stripe pattern when Spray Gun MkII has two paint cans loaded. Use P (or 1) for Primary and S (or 2) for Secondary, such as PS, PPS, or PPPS (up to 16 steps)."
				)
			);

			// Clamp out-of-range or NaN values if the config file was edited manually.
			var cleanCap = GetSanitizedCanCapacityMultiplier();
			if (!Mathf.Approximately(PaintCanCapacityMultiplier.Value, cleanCap) || float.IsNaN(PaintCanCapacityMultiplier.Value)) {
				PaintCanCapacityMultiplier.Value = cleanCap;
			}
			var cleanEff = GetSanitizedSprayGunEfficiency();
			if (!Mathf.Approximately(SprayGunEfficiency.Value, cleanEff) || float.IsNaN(SprayGunEfficiency.Value)) {
				SprayGunEfficiency.Value = cleanEff;
			}

			PaintCanCapacityMultiplier.SettingChanged += (_, _) => PrefabPatches.RefreshSprayCans();
			SprayGunEfficiency.SettingChanged += (_, _) => PrefabPatches.RefreshSprayGuns();

			MkIIRegistration.Initialize();

			Logger.LogInfo($"[{PluginName}] Loaded {PluginVersionGit} (built {PluginBuildTime})");

			_harmony = new Harmony(PluginGuid);
			_harmony.PatchAll();
		} catch (Exception ex) {
			Logger.LogError($"[{PluginName}] Failed to initialise: {ex}");
		}
	}

	private void Update() {
		SprayPatches.TickSprayAudio();
	}

	private void OnDestroy() {
		_harmony = null;
	}
}
