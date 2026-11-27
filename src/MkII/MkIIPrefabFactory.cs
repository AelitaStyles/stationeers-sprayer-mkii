// Copyright (c) 2026 Aelita Styles
// Licensed under the MIT License. See LICENSE in the project root for details.

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.UI;
using HarmonyLib;
using Reagents;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SprayerMkII;

/// <summary>
/// Clones the vanilla ItemSprayGun prefab, applies the MkII model and twin slots,
/// renders an inventory thumbnail, and registers ItemSprayGunMkII with Stationeers.
/// </summary>
internal static class MkIIPrefabFactory {
	internal const string PrefabName = "ItemSprayGunMkII";
	internal const string VanillaPrefabName = "ItemSprayGun";
	internal static readonly int PrefabHash = Animator.StringToHash(PrefabName);

	internal static SprayGun MkIIPrefab { get; private set; }

	internal static void Reset() => MkIIPrefab = null;

	internal static SprayGun EnsureRegistered() {
		if (MkIIPrefab != null) {
			return MkIIPrefab;
		}

		if (Prefab.Find<SprayGun>(PrefabName) is { } existing) {
			MkIIPrefab = existing;
			return existing;
		}

		var vanillaGun = Prefab.Find<SprayGun>(VanillaPrefabName);
		if (vanillaGun == null) {
			SprayerMkIIPlugin.Log.LogError($"Could not find vanilla prefab '{VanillaPrefabName}' to clone.");
			return null;
		}

		// Clone under an inactive root so Awake() stays dormant while modifying the prefab,
		// while keeping cloneGo.activeSelf true (matching vanilla). Setting activeSelf false
		// prevents spawned instances from running Awake(), leaving DamageState null on save.
		var tempRoot = new GameObject("SprayerMkII_TempCloneRoot");
		tempRoot.SetActive(false);

		SprayGun mk2;
		try {
			var cloneGo = Object.Instantiate(vanillaGun.gameObject, tempRoot.transform);
			cloneGo.name = PrefabName;
			cloneGo.SetActive(vanillaGun.gameObject.activeSelf);

			mk2 = cloneGo.GetComponent<SprayGun>();
			if (mk2 == null) {
				Object.DestroyImmediate(cloneGo);
				SprayerMkIIPlugin.Log.LogError("Cloned prefab is missing SprayGun component.");
				return null;
			}

			mk2.PrefabName = PrefabName;
			mk2.PrefabHash = PrefabHash;
			mk2._originalPrefab = mk2;
			mk2.RecipeTier = MachineTier.TierTwo;

			MkIIModelBuilder.ApplyModelAndSlots(mk2, vanillaGun);
			PrefabPatches.ApplyGunEfficiency(mk2);

			if (Prefab.PrefabsGameObject != null) {
				cloneGo.transform.SetParent(Prefab.PrefabsGameObject.transform, false);
			} else {
				cloneGo.transform.SetParent(null, false);
				Object.DontDestroyOnLoad(cloneGo);
			}
		} finally {
			Object.DestroyImmediate(tempRoot);
		}

		AssignThumbnail(mk2, vanillaGun);

		if (WorldManager.Instance?.SourcePrefabs is { } sourcePrefabs && !sourcePrefabs.Contains(mk2)) {
			sourcePrefabs.Add(mk2);
		}

		Prefab.RegisterExisting(mk2);

		if (DynamicThing.DynamicThingPrefabs is { } dynamicPrefabs) {
			if (!dynamicPrefabs.Contains(mk2)) {
				dynamicPrefabs.Add(mk2);
			}
			dynamicPrefabs.Sort((x, y) => x.CompareTo(y));
		}

		EnsureInputPrefabsEntry(mk2);

		MkIIPrefab = mk2;
		SprayerMkIIPlugin.Log.LogInfo($"Registered custom prefab '{PrefabName}' (hash {PrefabHash}).");
		return mk2;
	}

	internal static void EnsureInputPrefabsEntry(SprayGun mk2) {
		if (mk2 == null || InputPrefabs.Instance == null || InputPrefabs.PrefabReferences == null || InputPrefabs.PrefabReferences.Count == 0) {
			return;
		}

		if (InputPrefabs.PrefabReferences.ContainsKey(mk2.PrefabHash)) {
			return;
		}

		if (!InputPrefabs.Instance._transformLookup.TryGetValue(mk2.SortingClass, out var parent) || parent == null) {
			return;
		}

		var prefabRef = Object.Instantiate(InputPrefabs.Instance.PrefabReference, parent);
		prefabRef.SetPrefab(mk2, InputPrefabs.Fabricator?.GetRecipeSafe(mk2) ?? Recipe.INVALID);
		prefabRef.SetVisible(false);
		InputPrefabs.PrefabReferences.Add(prefabRef.PrefabHash, prefabRef);
	}

	internal static void RefreshThumbnail() {
		if (MkIIPrefab == null) {
			return;
		}

		var vanillaGun = Prefab.Find<SprayGun>(VanillaPrefabName);
		if (MkIIPrefab.Thumbnail != null && MkIIPrefab.Thumbnail != vanillaGun?.Thumbnail) {
			SyncInputPrefabsThumbnail(MkIIPrefab);
			return;
		}

		AssignThumbnail(MkIIPrefab, vanillaGun);
		SyncInputPrefabsThumbnail(MkIIPrefab);
	}

	private static void SyncInputPrefabsThumbnail(SprayGun mk2) {
		if (mk2?.Thumbnail == null || InputPrefabs.PrefabReferences == null) {
			return;
		}

		if (InputPrefabs.PrefabReferences.TryGetValue(mk2.PrefabHash, out var prefabRef)
			&& prefabRef != null
			&& prefabRef.Thumbnail != null) {
			prefabRef.Thumbnail.sprite = mk2.Thumbnail;
		}
	}

	private static void AssignThumbnail(SprayGun mk2, SprayGun vanillaGun) {
		try {
			var sprite = RenderThumbnailSprite(mk2);
			if (sprite != null) {
				mk2.Thumbnail = sprite;
				mk2.Thumbnails = new[] { sprite };
				return;
			}
		} catch (Exception ex) {
			SprayerMkIIPlugin.Log.LogWarning($"Thumbnail render failed, falling back to vanilla icon: {ex.Message}");
		}

		if (vanillaGun != null) {
			mk2.Thumbnail ??= vanillaGun.Thumbnail;
			if (mk2.Thumbnails == null || mk2.Thumbnails.Length == 0) {
				mk2.Thumbnails = vanillaGun.Thumbnails;
			}
		}
	}

	private static Sprite RenderThumbnailSprite(SprayGun mk2) {
		var tex = RenderThumbnailTexture(mk2);
		if (tex == null) {
			return null;
		}

		return Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
	}

	/// <summary>
	/// Renders an alpha-cropped inventory/Stationpedia icon via an isolated off-screen camera.
	/// Stationeers' built-in ThumbnailGenerator.Instance is null during normal gameplay, and
	/// other scene lights/skybox reflections must be masked off layer 30 to preserve contrast.
	/// </summary>
	private static Texture2D RenderThumbnailTexture(SprayGun mk2) {
		const int size = 512;
		const float padRatio = 0.02f;
		const int previewLayer = 30;

		// Stage visual meshes far off-world on an isolated layer so scene geometry and skybox stay out.
		var stage = new GameObject("SprayerMkII_ThumbStage") { layer = previewLayer };
		stage.transform.position = new Vector3(-5000f, -5000f, -5000f);

		var hasBounds = false;
		var worldBounds = default(Bounds);
		foreach (var mf in mk2.GetComponentsInChildren<MeshFilter>(true)) {
			if (mf.sharedMesh == null) {
				continue;
			}

			var mr = mf.GetComponent<MeshRenderer>();
			if (mr == null) {
				continue;
			}

			var copy = new GameObject("ThumbPart") { layer = previewLayer };
			copy.transform.SetParent(stage.transform, false);
			copy.transform.localPosition = mk2.transform.InverseTransformPoint(mf.transform.position);
			copy.transform.localRotation = Quaternion.Inverse(mk2.transform.rotation) * mf.transform.rotation;
			copy.transform.localScale = mf.transform.lossyScale;

			copy.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
			var copyMr = copy.AddComponent<MeshRenderer>();
			copyMr.sharedMaterials = mr.sharedMaterials;

			if (!hasBounds) {
				worldBounds = copyMr.bounds;
				hasBounds = true;
			} else {
				worldBounds.Encapsulate(copyMr.bounds);
			}
		}

		if (!hasBounds) {
			Object.DestroyImmediate(stage);
			return null;
		}

		var camGo = new GameObject("SprayerMkII_ThumbCam");
		var cam = camGo.AddComponent<Camera>();
		cam.enabled = false;
		cam.cullingMask = 1 << previewLayer;
		cam.clearFlags = CameraClearFlags.SolidColor;
		cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
		cam.orthographic = false;
		cam.fieldOfView = 32f;
		cam.allowMSAA = true;
		cam.allowHDR = false;
		cam.nearClipPlane = 0.01f;
		cam.farClipPlane = 20f;

		// Match vanilla Spray Gun's 3/4 perspective icon angle.
		var viewDir = new Vector3(-0.52f, 0.28f, 0.81f).normalized;
		var radius = worldBounds.extents.magnitude;
		var dist = radius / Mathf.Sin(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.15f;
		camGo.transform.position = worldBounds.center + viewDir * dist;
		camGo.transform.LookAt(worldBounds.center, Vector3.up);
		camGo.transform.Rotate(0f, 0f, -18f, Space.Self);

		var keyLightGo = new GameObject("SprayerMkII_ThumbKeyLight");
		var keyLight = keyLightGo.AddComponent<Light>();
		keyLight.type = LightType.Directional;
		keyLight.color = new Color(1f, 0.97f, 0.94f);
		keyLight.intensity = 1.05f;
		keyLight.shadows = LightShadows.Soft;
		keyLight.shadowStrength = 1f;
		keyLight.cullingMask = 1 << previewLayer;
		keyLightGo.transform.rotation = Quaternion.LookRotation(new Vector3(0.68f, -0.58f, -0.44f).normalized);

		// Temporarily mask previewLayer out of all other scene lights.
		var layerBit = 1 << previewLayer;
		var maskedLights = new List<(Light Light, int PrevMask)>();
		foreach (var other in Object.FindObjectsOfType<Light>()) {
			if (other != keyLight && (other.cullingMask & layerBit) != 0) {
				maskedLights.Add((other, other.cullingMask));
				other.cullingMask &= ~layerBit;
			}
		}

		var rt = RenderTexture.GetTemporary(size, size, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default, 4);
		var prevRt = RenderTexture.active;
		var prevAmbientMode = RenderSettings.ambientMode;
		var prevAmbientLight = RenderSettings.ambientLight;
		var prevReflectionIntensity = RenderSettings.reflectionIntensity;
		try {
			RenderSettings.ambientMode = AmbientMode.Flat;
			RenderSettings.ambientLight = new Color(0.05f, 0.05f, 0.06f);
			RenderSettings.reflectionIntensity = 0.1f;

			cam.targetTexture = rt;
			RenderTexture.active = rt;
			cam.Render();

			var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
			tex.ReadPixels(new Rect(0f, 0f, size, size), 0, 0);
			tex.Apply(false);

			var crop = ComputeAlphaCropRect(tex, padRatio);
			var cropW = (int)crop.width;
			var cropH = (int)crop.height;
			var croppedTex = new Texture2D(cropW, cropH, TextureFormat.RGBA32, true);
			croppedTex.SetPixels(tex.GetPixels((int)crop.x, (int)crop.y, cropW, cropH));
			croppedTex.Apply(true);
			Object.DestroyImmediate(tex);

			return croppedTex;
		} finally {
			foreach (var (other, prevMask) in maskedLights) {
				if (other != null) {
					other.cullingMask = prevMask;
				}
			}
			RenderSettings.ambientMode = prevAmbientMode;
			RenderSettings.ambientLight = prevAmbientLight;
			RenderSettings.reflectionIntensity = prevReflectionIntensity;
			cam.targetTexture = null;
			RenderTexture.active = prevRt;
			RenderTexture.ReleaseTemporary(rt);
			Object.DestroyImmediate(keyLightGo);
			Object.DestroyImmediate(camGo);
			Object.DestroyImmediate(stage);
		}
	}

	private static Rect ComputeAlphaCropRect(Texture2D tex, float padRatio) {
		var w = tex.width;
		var h = tex.height;
		var pixels = tex.GetPixels32();

		var minX = w;
		var minY = h;
		var maxX = -1;
		var maxY = -1;

		for (var y = 0; y < h; y++) {
			var row = y * w;
			for (var x = 0; x < w; x++) {
				if (pixels[row + x].a > 12) {
					if (x < minX) minX = x;
					if (x > maxX) maxX = x;
					if (y < minY) minY = y;
					if (y > maxY) maxY = y;
				}
			}
		}

		if (maxX < minX || maxY < minY) {
			return new Rect(0f, 0f, w, h);
		}

		var contentW = maxX - minX + 1;
		var contentH = maxY - minY + 1;
		var side = Mathf.CeilToInt(Mathf.Max(contentW, contentH) * (1f + padRatio * 2f));
		side = Mathf.Clamp(side, 16, Mathf.Min(w, h));

		var cx = (minX + maxX) * 0.5f;
		var cy = (minY + maxY) * 0.5f;
		var rx = Mathf.Clamp(Mathf.RoundToInt(cx - side * 0.5f), 0, w - side);
		var ry = Mathf.Clamp(Mathf.RoundToInt(cy - side * 0.5f), 0, h - side);
		return new Rect(rx, ry, side, side);
	}
}

[HarmonyPatch(typeof(InputPrefabs), nameof(InputPrefabs.PopulateList))]
internal static class InputPrefabsPopulateListPatch {
	[HarmonyPrefix]
	private static void Prefix() {
		try {
			MkIIRegistration.RegisterLocalization();
			MkIIPrefabFactory.EnsureRegistered();
		} catch (Exception ex) {
			SprayerMkIIPlugin.Log.LogError($"Failed to register {MkIIPrefabFactory.PrefabName} before InputPrefabs.PopulateList: {ex}");
		}
	}
}

[HarmonyPatch(typeof(Stationpedia), nameof(Stationpedia.Initialize))]
internal static class StationpediaInitializePatch {
	[HarmonyPrefix]
	private static void Prefix() => MkIIPrefabFactory.RefreshThumbnail();
}

[HarmonyPatch(typeof(WorldManager), nameof(WorldManager.BeforeLoadDataFiles))]
internal static class WorldManagerBeforeLoadDataFilesPatch {
	[HarmonyPrefix]
	private static void Prefix() {
		try {
			MkIIPrefabFactory.EnsureRegistered();
		} catch (Exception ex) {
			SprayerMkIIPlugin.Log.LogError($"Failed to register {MkIIPrefabFactory.PrefabName}: {ex}");
		}
	}
}

[HarmonyPatch(typeof(Prefab), nameof(Prefab.Clear))]
internal static class PrefabClearPatch {
	[HarmonyPostfix]
	private static void Postfix() {
		MkIIPrefabFactory.Reset();
		MkIIModelBuilder.Reset();
		PrefabPatches.Reset();
	}
}
