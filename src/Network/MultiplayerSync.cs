// Copyright (c) 2026 Aelita Styles
// Licensed under the MIT License. See LICENSE in the project root for details.

using System;
using System.Runtime.CompilerServices;
using Assets.Scripts;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using HarmonyLib;
using UnityEngine;

namespace SprayerMkII;

/// <summary>
/// Synchronises server-authoritative paint can capacity / Spray Gun efficiency to joining clients
/// and attaches per-attack spray options (single-segment modifier + stripe pattern) to AttackWithMessage.
/// </summary>
internal static class MultiplayerSync {
	private const byte FlagHasSprayOptions = 1 << 0;
	private const byte FlagSingleSegment = 1 << 1;

	internal sealed class SprayAttackOptions {
		public readonly bool SingleSegment;
		public readonly bool[] StripePattern;

		public SprayAttackOptions(bool singleSegment, bool[] stripePattern) {
			SingleSegment = singleSegment;
			StripePattern = stripePattern;
		}
	}

	private static readonly ConditionalWeakTable<AttackWithMessage, SprayAttackOptions> MessageOptions = new();

	private static float? _serverCanCapacityMultiplier;
	private static float? _serverSprayGunEfficiency;

	internal static SprayAttackOptions CurrentRemoteSprayOptions { get; set; }

	internal static float GetEffectiveCanCapacityMultiplier() {
		if (NetworkManager.IsClient && _serverCanCapacityMultiplier.HasValue) {
			return _serverCanCapacityMultiplier.Value;
		}

		return SprayerMkIIPlugin.GetSanitizedCanCapacityMultiplier();
	}

	internal static float GetEffectiveSprayGunEfficiency(float fallback = 6f) {
		if (NetworkManager.IsClient && _serverSprayGunEfficiency.HasValue) {
			return _serverSprayGunEfficiency.Value;
		}

		return SprayerMkIIPlugin.GetSanitizedSprayGunEfficiency(fallback);
	}

	internal static void ApplyServerOverrides(float canCapacityMultiplier, float sprayGunEfficiency) {
		_serverCanCapacityMultiplier = SanitizeFloat(canCapacityMultiplier, 1f, 100f, 5f);
		_serverSprayGunEfficiency = SanitizeFloat(sprayGunEfficiency, 1f, 32f, 6f);
		PrefabPatches.RefreshSprayCans();
		PrefabPatches.RefreshSprayGuns();
	}

	internal static void ClearServerOverrides() {
		var hadOverrides = _serverCanCapacityMultiplier.HasValue || _serverSprayGunEfficiency.HasValue;
		_serverCanCapacityMultiplier = null;
		_serverSprayGunEfficiency = null;
		CurrentRemoteSprayOptions = null;

		if (hadOverrides) {
			PrefabPatches.RefreshSprayCans();
			PrefabPatches.RefreshSprayGuns();
		}
	}

	internal static void AttachOptions(AttackWithMessage message, SprayAttackOptions options) {
		if (message == null || options == null) {
			return;
		}

		MessageOptions.Remove(message);
		MessageOptions.Add(message, options);
	}

	internal static bool TryGetOptions(AttackWithMessage message, out SprayAttackOptions options) {
		if (message == null) {
			options = null;
			return false;
		}

		return MessageOptions.TryGetValue(message, out options);
	}

	internal static void WriteSprayAttackOptions(AttackWithMessage message, RocketBinaryWriter writer) {
		var parent = Thing.Find(message.AttackParentId);
		var activeSlot = parent?.Slots != null && message.ActiveHandSlotId < parent.Slots.Count
			? parent.Slots[message.ActiveHandSlotId]
			: null;

		if (activeSlot?.Get() is not SprayGun) {
			writer.WriteByte(0);
			return;
		}

		byte flags = FlagHasSprayOptions;
		if (SprayInputHelper.IsSinglePaintModifierHeld()) {
			flags |= FlagSingleSegment;
		}

		var pattern = SprayPatches.GetStripePattern();
		var len = Mathf.Clamp(pattern.Length, 1, SprayPatches.MaxStripePatternLength);
		ushort bits = 0;
		for (var i = 0; i < len; i++) {
			if (pattern[i]) {
				bits |= (ushort)(1 << i);
			}
		}

		writer.WriteByte(flags);
		writer.WriteByte((byte)len);
		writer.WriteUInt16(bits);
	}

	internal static void ReadSprayAttackOptions(AttackWithMessage message, RocketBinaryReader reader) {
		var flags = reader.ReadByte();
		if ((flags & FlagHasSprayOptions) == 0) {
			return;
		}

		var singleSegment = (flags & FlagSingleSegment) != 0;
		var rawLen = reader.ReadByte();
		var bits = reader.ReadUInt16();

		var len = Mathf.Clamp(rawLen, 0, SprayPatches.MaxStripePatternLength);
		bool[] pattern;
		if (len > 0) {
			pattern = new bool[len];
			for (var i = 0; i < len; i++) {
				pattern[i] = (bits & (1 << i)) != 0;
			}
		} else {
			pattern = SprayPatches.DefaultStripePattern;
		}

		AttachOptions(message, new SprayAttackOptions(singleSegment, pattern));
	}

	private static float SanitizeFloat(float value, float min, float max, float fallback) {
		if (float.IsNaN(value) || float.IsInfinity(value)) {
			return fallback;
		}

		return Mathf.Clamp(value, min, max);
	}
}

[HarmonyPatch(typeof(WorldManager), nameof(WorldManager.SerializeOnJoin))]
internal static class WorldManagerSerializeOnJoinPatch {
	[HarmonyPostfix]
	private static void Postfix(RocketBinaryWriter writer) {
		writer.WriteSingle(SprayerMkIIPlugin.GetSanitizedCanCapacityMultiplier());
		writer.WriteSingle(SprayerMkIIPlugin.GetSanitizedSprayGunEfficiency());
	}
}

[HarmonyPatch(typeof(WorldManager), nameof(WorldManager.DeserializeOnJoin))]
internal static class WorldManagerDeserializeOnJoinPatch {
	[HarmonyPostfix]
	private static void Postfix(RocketBinaryReader reader) {
		var capacityMultiplier = reader.ReadSingle();
		var sprayGunEfficiency = reader.ReadSingle();
		MultiplayerSync.ApplyServerOverrides(capacityMultiplier, sprayGunEfficiency);
	}
}

[HarmonyPatch(typeof(WorldManager), nameof(WorldManager.ClearWorld))]
internal static class WorldManagerClearWorldPatch {
	[HarmonyPostfix]
	private static void Postfix() {
		MultiplayerSync.ClearServerOverrides();
	}
}

[HarmonyPatch(typeof(AttackWithMessage), nameof(AttackWithMessage.Serialize))]
internal static class AttackWithMessageSerializePatch {
	[HarmonyPostfix]
	private static void Postfix(AttackWithMessage __instance, RocketBinaryWriter writer) {
		MultiplayerSync.WriteSprayAttackOptions(__instance, writer);
	}
}

[HarmonyPatch(typeof(AttackWithMessage), nameof(AttackWithMessage.Deserialize))]
internal static class AttackWithMessageDeserializePatch {
	[HarmonyPostfix]
	private static void Postfix(AttackWithMessage __instance, RocketBinaryReader reader) {
		MultiplayerSync.ReadSprayAttackOptions(__instance, reader);
	}
}

[HarmonyPatch(typeof(AttackWithMessage), nameof(AttackWithMessage.Process))]
internal static class AttackWithMessageProcessPatch {
	[HarmonyPrefix]
	private static void Prefix(AttackWithMessage __instance) {
		MultiplayerSync.CurrentRemoteSprayOptions = MultiplayerSync.TryGetOptions(__instance, out var options)
			? options
			: null;
	}

	[HarmonyFinalizer]
	private static Exception Finalizer(Exception __exception) {
		MultiplayerSync.CurrentRemoteSprayOptions = null;
		return __exception;
	}
}
