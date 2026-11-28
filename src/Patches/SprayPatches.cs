// Copyright (c) 2026 Aelita Styles
// Licensed under the MIT License. See LICENSE in the project root for details.

using System;
using System.Collections.Generic;
using System.Text;
using Assets.Scripts;
using Assets.Scripts.Inventory;
using Assets.Scripts.Localization2;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using HarmonyLib;
using UnityEngine;

namespace SprayerMkII;

/// <summary>
/// Intercepts SprayGun actions on networked segments to paint the whole network,
/// applying the configured stripe pattern when a secondary paint can is loaded.
/// </summary>
[HarmonyPatch(typeof(Thing), nameof(Thing.AttackWith))]
internal static class SprayPatches {
	private const float FallbackSprayCanDuration = 0.5f;
	private const float MkIDurationMultiplier = 0.50f;
	private const float MkIIDurationMultiplier = 0.25f;
	private const float HissLoopStartRatio = 0.25f;
	private const float HissLoopEndRatio = 0.78f;
	internal const int MaxStripePatternLength = 16;

	internal static readonly bool[] DefaultStripePattern = { false, false, true };
	private static string _cachedPatternRaw;
	private static bool[] _cachedPattern = DefaultStripePattern;

	private static SprayGun _activeSprayGun;

	internal static void TickSprayAudio() {
		var inv = InventoryManager.Instance;
		if (inv == null || inv.ActionCoroutine == null || _activeSprayGun == null) {
			return;
		}

		if (InventoryManager.ActiveHandSlot?.Get() != _activeSprayGun) {
			_activeSprayGun = null;
			return;
		}

		// Loop the middle hiss section of the spray clip without repeating the initial click.
		var baseAudio = inv.ActionSoundAudio != null
			? inv.ActionSoundAudio.GetComponentInChildren<AudioSource>()
			: null;
		if (baseAudio != null && baseAudio.clip != null && baseAudio.clip.length > 0.2f) {
			baseAudio.loop = true;
			var loopEnd = baseAudio.clip.length * HissLoopEndRatio;
			if (baseAudio.time >= loopEnd) {
				baseAudio.time = baseAudio.clip.length * HissLoopStartRatio;
			}
			if (!baseAudio.isPlaying) {
				baseAudio.Play();
			}
		}
	}

	[HarmonyPrefix]
	private static bool Prefix(
		Thing __instance,
		Attack attack,
		bool doAction,
		ref Thing.DelayedActionInstance __result
	) {
		if (__instance == null || !__instance.IsPaintable || __instance.HasColorState) {
			return true;
		}

		// Harmony003 false-positives when reading a property on a non-ref struct parameter.
#pragma warning disable Harmony003
		if (attack.SourceItem is not SprayGun sprayGun) {
			return true;
		}
		var isLocalPlayer = !GameManager.IsBatchMode && attack.ActiveHand?.Parent == InventoryManager.Parent;
#pragma warning restore Harmony003

		var remoteOptions = MultiplayerSync.CurrentRemoteSprayOptions;
		var singleSegment = remoteOptions != null
			? remoteOptions.SingleSegment
			: isLocalPlayer && SprayInputHelper.IsSinglePaintModifierHeld();
		if (singleSegment) {
			if (isLocalPlayer) {
				_activeSprayGun = null;
			}
			return true;
		}

		if (!NetworkPaintHelper.TryGetNetworkSegments(__instance, out var networkSegments)) {
			if (isLocalPlayer) {
				_activeSprayGun = null;
			}
			return true;
		}

		var pattern = remoteOptions?.StripePattern ?? GetStripePattern();
		__result = DoNetworkSpray(sprayGun, __instance, networkSegments, pattern, doAction, isLocalPlayer);
		return false;
	}

	private static Thing.DelayedActionInstance DoNetworkSpray(
		SprayGun gun,
		Thing targetThing,
		List<NetworkPaintHelper.SegmentEntry> networkSegments,
		bool[] pattern,
		bool doAction,
		bool isLocalPlayer
	) {
		var action = new Thing.DelayedActionInstance {
			Duration = gun.TimeToUse(),
			ActionMessage = ActionStrings.Paint
		};

		var primaryCan = gun.SprayCan;
		var primaryMat = gun.GetPaintMaterial();
		if (primaryCan == null || primaryMat == null) {
			return action.Fail(GameStrings.NotEnoughSprayPaint, gun.ToTooltip());
		}

		if (!gun.OnOff) {
			return action.Fail(GameStrings.CannotUseWhenNotOn, gun.ToTooltip());
		}

		if (gun.IsEmpty) {
			return action.Fail(GameStrings.NotEnoughSprayPaint, gun.ToTooltip());
		}

		var primarySwatch = GameManager.GetColorSwatch(primaryMat);
		var secondaryCan = gun.GetSecondarySprayCan();
		var isDualCan = secondaryCan != null && secondaryCan != primaryCan;

		// If both slots hold a can and the secondary can is empty, refuse to paint so
		// players don't accidentally overwrite a striped network with a solid colour.
		if (isDualCan && secondaryCan.IsEmpty) {
			return action.Fail(GameStrings.NotEnoughSprayPaint, gun.ToTooltip());
		}

		var hasSecondary = isDualCan && secondaryCan.PaintMaterial != null;
		var secondarySwatch = hasSecondary ? GameManager.GetColorSwatch(secondaryCan.PaintMaterial) : null;

		var toPaint = new List<(Thing Segment, bool UseSecondary)>(networkSegments.Count);
		foreach (var entry in networkSegments) {
			var useSecondary = hasSecondary && pattern[entry.PatternIndex % pattern.Length];
			var desiredMat = useSecondary ? secondaryCan.PaintMaterial : primaryMat;

			if (NeedsRepaint(entry.Segment, desiredMat)) {
				toPaint.Add((entry.Segment, useSecondary));
			}
		}

		if (toPaint.Count == 0) {
			return action.Fail(GameStrings.CantPaintSameColour, targetThing.ToTooltip(), primarySwatch.ToTooltip());
		}

		var efficiency = MultiplayerSync.GetEffectiveSprayGunEfficiency(gun.Efficiency);
		var costScale = 1f / efficiency;
		var paintableCount = CountPaintableSegments(toPaint, primaryCan, hasSecondary ? secondaryCan : null, costScale);
		if (paintableCount == 0) {
			return action.Fail(GameStrings.NotEnoughSprayPaint, gun.ToTooltip());
		}

		var canDuration = primaryCan.TimeToUse();
		if (canDuration <= float.Epsilon) {
			canDuration = FallbackSprayCanDuration;
		}
		var durationMultiplier = gun.PrefabHash == MkIIPrefabFactory.PrefabHash
			? MkIIDurationMultiplier
			: MkIDurationMultiplier;

		action.Duration = paintableCount * canDuration * durationMultiplier;
		action.ActionSoundHash = ISprayer.SprayPaintSoundHash;
		action.ActionCompleteSoundHash = gun.FinishedConstructingSoundHash;

		action.ExtendedMessage = BuildTooltip(
			paintableCount,
			toPaint.Count,
			pattern,
			primaryCan,
			primarySwatch,
			hasSecondary ? secondaryCan : null,
			secondarySwatch
		);

		if (!doAction || !GameManager.RunSimulation) {
			if (isLocalPlayer) {
				_activeSprayGun = doAction ? null : gun;
			}
			return action;
		}

		if (isLocalPlayer) {
			_activeSprayGun = null;
		}

		foreach (var (segment, useSecondary) in toPaint) {
			var can = useSecondary ? secondaryCan : primaryCan;
			var swatch = useSecondary ? secondarySwatch : primarySwatch;

			// Stop as soon as either required can runs dry so the pattern stays intact.
			if (can == null || can.IsEmpty || !can.OnUseItem(can.UseAmount * costScale, segment)) {
				break;
			}

			OnServer.SetCustomColor(segment, swatch.Index);
		}

		return action;
	}

	private static int CountPaintableSegments(
		List<(Thing Segment, bool UseSecondary)> toPaint,
		SprayCan primaryCan,
		SprayCan secondaryCan,
		float costScale
	) {
		var primaryRemaining = primaryCan.Quantity;
		var secondaryRemaining = secondaryCan != null ? secondaryCan.Quantity : 0f;
		var count = 0;

		foreach (var (_, useSecondary) in toPaint) {
			var can = useSecondary ? secondaryCan : primaryCan;
			if (can == null || can.IsEmpty) {
				break;
			}

			var cost = can.UseAmount * costScale;
			if (useSecondary) {
				if (secondaryRemaining <= float.Epsilon) {
					break;
				}
				secondaryRemaining = Mathf.Max(0f, secondaryRemaining - cost);
			} else {
				if (primaryRemaining <= float.Epsilon) {
					break;
				}
				primaryRemaining = Mathf.Max(0f, primaryRemaining - cost);
			}

			count++;
		}

		return count;
	}

	internal static bool[] GetStripePattern() {
		var raw = SprayerMkIIPlugin.StripePattern?.Value ?? "PPS";
		if (string.Equals(raw, _cachedPatternRaw, StringComparison.Ordinal)) {
			return _cachedPattern;
		}

		var parsed = new List<bool>(Mathf.Min(raw.Length, MaxStripePatternLength));
		foreach (var ch in raw) {
			if (parsed.Count >= MaxStripePatternLength) {
				break;
			}
			switch (ch) {
				case 'P':
				case 'p':
				case '1':
					parsed.Add(false);
					break;
				case 'S':
				case 's':
				case '2':
					parsed.Add(true);
					break;
			}
		}

		_cachedPatternRaw = raw;
		_cachedPattern = parsed.Count > 0 ? parsed.ToArray() : DefaultStripePattern;
		return _cachedPattern;
	}

	private static bool NeedsRepaint(Thing segment, Material targetMaterial) {
		var currentMaterial = segment.CustomColor?.Normal != null
			? segment.CustomColor.Normal
			: segment.PaintableMaterial;
		return currentMaterial != targetMaterial;
	}

	private static string BuildTooltip(
		int paintableCount,
		int totalCount,
		bool[] pattern,
		SprayCan primaryCan,
		ColorSwatch primarySwatch,
		SprayCan secondaryCan,
		ColorSwatch secondarySwatch
	) {
		var sb = new StringBuilder();
		var modifierKey = SprayerMkIIPlugin.GetSanitizedSinglePaintKey();
		var unit = totalCount == 1 ? "segment" : "segments";
		var countText = paintableCount < totalCount
			? $"{paintableCount} of {totalCount} {unit}"
			: $"{totalCount} {unit}";

		if (secondaryCan != null && secondarySwatch != null) {
			sb.Append($"Network ({countText}): ");
			var previewLen = Mathf.Min(pattern.Length, 8);
			for (var i = 0; i < previewLen; i++) {
				if (i > 0) {
					sb.Append(" / ");
				}
				sb.Append(pattern[i] ? secondarySwatch.ToTooltip() : primarySwatch.ToTooltip());
			}
			if (pattern.Length > previewLen) {
				sb.Append(" / ...");
			}
			sb.AppendLine();
		} else {
			sb.AppendLine($"Network ({countText}): {primarySwatch.ToTooltip()}");
		}

		sb.AppendLine(GameStrings.ItemInSlotValue.AsString(primaryCan.ToTooltip(), primaryCan.GetQuantityText()));

		if (secondaryCan != null) {
			sb.AppendLine(GameStrings.ItemInSlotValue.AsString(secondaryCan.ToTooltip(), secondaryCan.GetQuantityText()));
		}

		if (modifierKey != KeyCode.None) {
			sb.AppendLine($"Hold {SprayInputHelper.FormatKeyName(modifierKey)} to paint a single segment");
		}
		return sb.ToString();
	}
}

internal static class SprayInputHelper {
	internal static bool IsSinglePaintModifierHeld() {
		var key = SprayerMkIIPlugin.GetSanitizedSinglePaintKey();
		if (key == KeyCode.None) {
			return false;
		}
		return Input.GetKey(key) || (key == KeyCode.LeftShift && Input.GetKey(KeyCode.RightShift));
	}

	internal static string FormatKeyName(KeyCode key) => key switch {
		KeyCode.LeftShift or KeyCode.RightShift => "Shift",
		KeyCode.LeftControl or KeyCode.RightControl => "Ctrl",
		KeyCode.LeftAlt or KeyCode.RightAlt => "Alt",
		_ => key.ToString()
	};
}
