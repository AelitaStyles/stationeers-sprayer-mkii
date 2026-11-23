// Copyright (c) 2026 Aelita Styles
// Licensed under the MIT License. See LICENSE in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SprayerMkII;

/// <summary>
/// Loads the ItemSprayGunMkII model from Assets/sprayermkii.assets (preloaded, disk, or embedded),
/// replaces the cloned vanilla SprayGun visuals and collider, wires up twin paint can slots,
/// and handles recolouring the paintable MkII body.
/// </summary>
internal static class MkIIModelBuilder {
	private const string BundleFileName = "sprayermkii.assets";
	private const string BundleDiskPath = "Assets/" + BundleFileName;
	private const string EmbeddedBundleResource = "SprayerMkII.Assets." + BundleFileName;

	// Fallback slot offsets (in metres) when building without the optional AssetBundle.
	private static readonly Vector3 DefaultPrimaryBayPos = new(-0.16f, -0.06f, -0.056f);
	private static readonly Vector3 DefaultSecondaryBayPos = new(-0.16f, -0.06f, 0.056f);

	// Orange swatch (#FF662B) in the model texture atlas that gets recoloured when the MkII is painted.
	private static readonly Color32 PaintableOrangeRgb = new(255, 102, 43, 255);
	private static readonly Dictionary<int, Material> PaintedMaterials = new();
	private static AssetBundle _cachedBundle;
	private static Color32[] _baseAtlasPixels;
	private static int _baseAtlasWidth;
	private static int _baseAtlasHeight;

	internal static Material BaseMaterial { get; private set; }

	internal static void Reset() {
		BaseMaterial = null;
		PaintedMaterials.Clear();
		_baseAtlasPixels = null;
		_baseAtlasWidth = 0;
		_baseAtlasHeight = 0;
	}

	internal static void ApplyModelAndSlots(SprayGun mk2, SprayGun vanillaGun) {
		Transform primaryMount = null;
		Transform secondaryMount = null;

		if (TryApplyAssetBundle(mk2, vanillaGun, out var bundleBounds, out primaryMount, out secondaryMount)) {
			FitCollider(mk2, bundleBounds);
			ReplaceBlueprint(mk2, vanillaGun);
		} else {
			SprayerMkIIPlugin.Log.LogWarning($"AssetBundle '{BundleFileName}' not found or empty; using vanilla SprayGun mesh.");
		}

		SetupTwinSlots(mk2, primaryMount, secondaryMount);
		mk2.CachePrefabBounds();
	}

	private static bool TryApplyAssetBundle(
		SprayGun mk2,
		SprayGun vanillaGun,
		out Bounds combinedBounds,
		out Transform primaryMount,
		out Transform secondaryMount
	) {
		combinedBounds = default;
		primaryMount = null;
		secondaryMount = null;

		var bundle = LoadBundle();
		if (bundle == null) {
			return false;
		}

		var prefabAsset = bundle.LoadAsset<GameObject>(MkIIPrefabFactory.PrefabName);
		if (prefabAsset == null) {
			foreach (var name in bundle.GetAllAssetNames()) {
				prefabAsset = bundle.LoadAsset<GameObject>(name);
				if (prefabAsset != null) {
					break;
				}
			}
		}

		if (prefabAsset == null) {
			SprayerMkIIPlugin.Log.LogWarning($"AssetBundle '{BundleFileName}' contained no GameObject prefab.");
			return false;
		}

		var modelRoot = Object.Instantiate(prefabAsset, mk2.transform, false);
		modelRoot.name = "MkII_BundleModel";
		SetLayerRecursively(modelRoot, mk2.gameObject.layer);

		var bundleLever = FindChildByNames(modelRoot.transform, "SprayPaintGunLever");
		var bundleHasLeverMesh = bundleLever != null && bundleLever.GetComponentInChildren<MeshFilter>(true) != null;

		StripVanillaVisuals(mk2, preserveVanillaLever: !bundleHasLeverMesh, excludeRoot: modelRoot.transform);

		var hasBounds = false;
		foreach (var mf in modelRoot.GetComponentsInChildren<MeshFilter>(true)) {
			if (mf.sharedMesh == null) {
				continue;
			}

			var b = TransformBounds(mf.transform, mk2.transform, mf.sharedMesh.bounds);
			if (!hasBounds) {
				combinedBounds = b;
				hasBounds = true;
			} else {
				combinedBounds.Encapsulate(b);
			}
		}

		var instancedShader = ResolveStationeersShader(vanillaGun);
		Material bundleMaterial = null;
		foreach (var mr in modelRoot.GetComponentsInChildren<MeshRenderer>(true)) {
			foreach (var mat in mr.sharedMaterials) {
				if (mat == null) {
					continue;
				}
				bundleMaterial ??= mat;
				if (instancedShader != null && mat.shader != null && mat.shader.name == "Standard") {
					mat.shader = instancedShader;
				}
			}
		}

		primaryMount = FindChildByNames(modelRoot.transform, "SlotLocation_Primary", "Slot1");
		secondaryMount = FindChildByNames(modelRoot.transform, "SlotLocation_Secondary", "Slot2");
		WireLatchToVanillaTransform(mk2, modelRoot, bundleHasLeverMesh);

		// Clear stale vanilla renderer caches so Thing.Awake() registers the bundle renderers on spawn.
		// Keep vanilla's PaintableMaterial so painting resolves a valid swatch while our SetCustomColor
		// postfixes swap in the recoloured atlas material.
		mk2.Renderers?.Clear();
		mk2.RocketRenderers?.Clear();
		BaseMaterial = bundleMaterial;
		mk2.PaintableMaterial = vanillaGun.PaintableMaterial;

		SprayerMkIIPlugin.Log.LogInfo($"Applied AssetBundle model '{prefabAsset.name}' for {MkIIPrefabFactory.PrefabName}.");
		return hasBounds;
	}

	private static AssetBundle LoadBundle() {
		if (_cachedBundle != null) {
			return _cachedBundle;
		}

		// StationeersLaunchPad preloads *.assets files from <ModDir>/Assets/.
		foreach (var loaded in AssetBundle.GetAllLoadedAssetBundles()) {
			if (loaded != null && string.Equals(loaded.name, BundleFileName, StringComparison.OrdinalIgnoreCase)) {
				SprayerMkIIPlugin.Log.LogInfo($"Using preloaded MkII AssetBundle '{loaded.name}'.");
				_cachedBundle = loaded;
				return _cachedBundle;
			}
		}

		var modDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
		if (!string.IsNullOrEmpty(modDir)) {
			var diskPath = Path.Combine(modDir, BundleDiskPath);
			if (File.Exists(diskPath)) {
				SprayerMkIIPlugin.Log.LogInfo($"Loading MkII AssetBundle from disk: {diskPath}");
				_cachedBundle = AssetBundle.LoadFromFile(diskPath);
				return _cachedBundle;
			}
		}

		using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedBundleResource);
		if (stream != null) {
			SprayerMkIIPlugin.Log.LogInfo("Loading embedded MkII AssetBundle.");
			_cachedBundle = AssetBundle.LoadFromStream(stream);
			return _cachedBundle;
		}

		return null;
	}

	private static void StripVanillaVisuals(SprayGun mk2, bool preserveVanillaLever, Transform excludeRoot) {
		foreach (var lod in mk2.GetComponentsInChildren<LODGroup>(true)) {
			if (excludeRoot != null && lod.transform.IsChildOf(excludeRoot)) {
				continue;
			}
			Object.DestroyImmediate(lod);
		}

		foreach (var mf in mk2.GetComponentsInChildren<MeshFilter>(true)) {
			if (excludeRoot != null && mf.transform.IsChildOf(excludeRoot)) {
				continue;
			}
			if (preserveVanillaLever && IsUnderNamedTransform(mf.transform, "SprayPaintGunLever")) {
				continue;
			}

			if (mf.TryGetComponent<MeshRenderer>(out var mr)) {
				Object.DestroyImmediate(mr);
			}
			Object.DestroyImmediate(mf);
		}
	}

	private static bool IsUnderNamedTransform(Transform t, string name) {
		for (var cur = t; cur != null; cur = cur.parent) {
			if (string.Equals(cur.name, name, StringComparison.OrdinalIgnoreCase)) {
				return true;
			}
		}
		return false;
	}

	private static void FitCollider(SprayGun mk2, Bounds bounds) {
		foreach (var mc in mk2.GetComponentsInChildren<MeshCollider>(true)) {
			if (!mc.isTrigger) {
				Object.DestroyImmediate(mc);
			}
		}

		BoxCollider box = null;
		foreach (var col in mk2.GetComponentsInChildren<BoxCollider>(true)) {
			if (!col.isTrigger) {
				box = col;
				break;
			}
		}

		box ??= mk2.gameObject.AddComponent<BoxCollider>();
		box.center = bounds.center;
		box.size = bounds.size;
	}

	private static void ReplaceBlueprint(SprayGun mk2, SprayGun vanillaGun) {
		var firstMesh = mk2.GetComponentInChildren<MeshFilter>(true)?.sharedMesh;
		if (vanillaGun.Blueprint == null || firstMesh == null) {
			return;
		}

		var bp = Object.Instantiate(vanillaGun.Blueprint);
		bp.name = "ItemSprayGunMkII_Blueprint";
		bp.SetActive(false);

		if (Prefab.PrefabsGameObject != null) {
			bp.transform.SetParent(Prefab.PrefabsGameObject.transform, false);
		}

		var filter = bp.GetComponentInChildren<MeshFilter>(true);
		if (filter != null) {
			filter.sharedMesh = firstMesh;
		}

		mk2.Blueprint = bp;
	}

	private static void SetupTwinSlots(SprayGun mk2, Transform primaryOverride, Transform secondaryOverride) {
		if (mk2.Slots == null || mk2.Slots.Count == 0) {
			SprayerMkIIPlugin.Log.LogWarning("Cloned SprayGun has no slots to configure.");
			return;
		}

		var primary = mk2.Slots[0];
		var canRotation = primary.Location != null ? primary.Location.localRotation : Quaternion.identity;

		Interactable slot1Interactable = null;
		if (mk2.Interactables != null) {
			foreach (var item in mk2.Interactables) {
				if (item != null && item.Action == InteractableType.Slot1) {
					slot1Interactable = item;
					break;
				}
			}
		}

		primary.Parent = mk2;
		primary.StringKey = PrefabPatches.PrimarySlotKey;
		primary.StringHash = Animator.StringToHash(PrefabPatches.PrimarySlotKey);
		primary.Location = primaryOverride != null
			? primaryOverride
			: CreateMount(mk2.transform, "SlotLocation_Primary", DefaultPrimaryBayPos, canRotation);
		primary.HidesOccupant = false;
		primary.OccupantCastsShadows = true;
		primary.Action = InteractableType.Slot1;
		primary._index = 0;

		var primaryCol = EnsureSlotCollider(primary.Location, primary.Collider, "SlotCollider_Primary");
		if (primary.Collider != null && primary.Collider != primaryCol && primary.Collider.gameObject != mk2.gameObject) {
			Object.DestroyImmediate(primary.Collider.gameObject);
		}

		primary.Collider = primaryCol;
		if (slot1Interactable != null) {
			slot1Interactable.StringKey = PrefabPatches.PrimarySlotKey;
			slot1Interactable.StringHash = Animator.StringToHash(PrefabPatches.PrimarySlotKey);
			slot1Interactable.Collider = primaryCol;
			slot1Interactable.Slot = primary;
			primary.Interactable = slot1Interactable;
		}

		while (mk2.Slots.Count > 1) {
			mk2.Slots.RemoveAt(mk2.Slots.Count - 1);
		}

		var secondaryLoc = secondaryOverride != null
			? secondaryOverride
			: CreateMount(mk2.transform, "SlotLocation_Secondary", DefaultSecondaryBayPos, canRotation);
		var secondaryCol = EnsureSlotCollider(secondaryLoc, primaryCol, "SlotCollider_Secondary");

		var secondary = new Slot {
			StringKey = PrefabPatches.SecondarySlotKey,
			StringHash = Animator.StringToHash(PrefabPatches.SecondarySlotKey),
			Type = primary.Type,
			SpecificTypePrefabHashes = (int[])primary.SpecificTypePrefabHashes?.Clone(),
			Location = secondaryLoc,
			Parent = mk2,
			EntityControlMode = primary.EntityControlMode,
			UseInternalAtmosphere = primary.UseInternalAtmosphere,
			RealWorldScale = primary.RealWorldScale,
			ScaleMultiplier = primary.ScaleMultiplier,
			OccupantCastsShadows = true,
			HidesOccupant = false,
			IsHiddenInSeat = primary.IsHiddenInSeat,
			IsInteractable = primary.IsInteractable,
			IsSwappable = primary.IsSwappable,
			AllowDragging = primary.AllowDragging,
			IsLocked = false,
			Action = InteractableType.Slot2,
			Collider = secondaryCol,
			Size = primary.Size,
			_index = 1
		};

		// Register a matching Slot2 Interactable for in-world dragging. ParentInteractableIndex
		// must stay -1 (C# default 0 parents it under Interactables[0]) and Bounds must be cached.
		if (mk2.Interactables != null) {
			var secondaryInteractable = new Interactable {
				StringKey = PrefabPatches.SecondarySlotKey,
				StringHash = Animator.StringToHash(PrefabPatches.SecondarySlotKey),
				Parent = mk2,
				Collider = secondaryCol,
				FakeCollider = slot1Interactable?.FakeCollider,
				ParentInteractableIndex = slot1Interactable?.ParentInteractableIndex ?? -1,
				Action = InteractableType.Slot2,
				ActionName = slot1Interactable?.ActionName ?? string.Empty,
				Layer = slot1Interactable?.Layer ?? 0,
				CanKeyInteract = slot1Interactable?.CanKeyInteract ?? true,
				KeyMap = slot1Interactable?.KeyMap ?? string.Empty,
				JoinInProgressSync = slot1Interactable?.JoinInProgressSync ?? false,
				Bounds = slot1Interactable?.Bounds ?? new Bounds(Vector3.zero, secondaryCol.size),
				OriginalBounds = slot1Interactable?.OriginalBounds ?? new Bounds(Vector3.zero, secondaryCol.size),
				Slot = secondary,
				_index = mk2.Interactables.Count
			};
			secondaryInteractable.CacheBounds();
			secondary.Interactable = secondaryInteractable;
			mk2.Interactables.Add(secondaryInteractable);
		}

		mk2.Slots.Add(secondary);
	}

	private static BoxCollider EnsureSlotCollider(Transform mount, BoxCollider template, string name) {
		var existing = mount.GetComponentInChildren<BoxCollider>(true);
		if (existing != null) {
			return existing;
		}

		var targetLayer = template != null ? template.gameObject.layer : mount.gameObject.layer;
		var go = new GameObject(name) { layer = targetLayer };
		go.transform.SetParent(mount, false);
		var box = go.AddComponent<BoxCollider>();
		box.isTrigger = template != null && template.isTrigger;
		box.center = Vector3.zero;
		box.size = template != null ? template.size : new Vector3(0.10f, 0.24f, 0.10f);
		return box;
	}

	private static Transform CreateMount(Transform parent, string name, Vector3 pos, Quaternion rot) {
		var mount = new GameObject(name).transform;
		mount.SetParent(parent, false);
		mount.localPosition = pos;
		mount.localRotation = rot;
		return mount;
	}

	private static void WireLatchToVanillaTransform(SprayGun mk2, GameObject modelRoot, bool bundleHasLeverMesh) {
		var bundleLever = FindChildByNames(modelRoot.transform, "SprayPaintGunLever");
		if (bundleLever == null || !bundleHasLeverMesh) {
			return;
		}

		var vanillaLever = FindChildByNames(mk2.transform, "SprayPaintGunLever");
		if (vanillaLever == null || bundleLever == vanillaLever) {
			return;
		}

		// Reparent under the vanilla latch Transform so the cloned On/Off Animator drives it.
		bundleLever.SetParent(vanillaLever, true);
	}

	private static Transform FindChildByNames(Transform root, params string[] names) {
		foreach (var name in names) {
			foreach (var t in root.GetComponentsInChildren<Transform>(true)) {
				if (string.Equals(t.name, name, StringComparison.OrdinalIgnoreCase)) {
					return t;
				}
			}
		}

		return null;
	}

	private static void SetLayerRecursively(GameObject go, int layer) {
		go.layer = layer;
		foreach (Transform child in go.transform) {
			SetLayerRecursively(child.gameObject, layer);
		}
	}

	private static Bounds TransformBounds(Transform meshTransform, Transform rootTransform, Bounds localBounds) {
		var matrix = rootTransform.worldToLocalMatrix * meshTransform.localToWorldMatrix;
		var c = localBounds.center;
		var e = localBounds.extents;
		var result = new Bounds(matrix.MultiplyPoint3x4(c), Vector3.zero);

		for (var sx = -1; sx <= 1; sx += 2) {
			for (var sy = -1; sy <= 1; sy += 2) {
				for (var sz = -1; sz <= 1; sz += 2) {
					result.Encapsulate(matrix.MultiplyPoint3x4(c + new Vector3(e.x * sx, e.y * sy, e.z * sz)));
				}
			}
		}

		return result;
	}

	private static Shader ResolveStationeersShader(SprayGun vanillaGun) {
		var templateMat = Prefab.Find<SprayCan>("ItemSprayCanWhite")?.PaintMaterial
			?? vanillaGun.PaintableMaterial
			?? vanillaGun.GetComponentInChildren<MeshRenderer>(true)?.sharedMaterial;
		return templateMat != null ? templateMat.shader : null;
	}

	internal static Material GetPaintedMaterial(ColorSwatch swatch) {
		if (BaseMaterial == null) {
			return swatch?.Normal;
		}

		if (swatch == null || !swatch.IsSet) {
			return BaseMaterial;
		}

		if (PaintedMaterials.TryGetValue(swatch.Index, out var cached) && cached != null) {
			return cached;
		}

		if (!EnsureBaseAtlasPixels()) {
			return BaseMaterial;
		}

		var targetColor = swatch.Color.a > 0.01f
			? (Color32)swatch.Color
			: (swatch.Normal != null ? (Color32)swatch.Normal.color : new Color32(255, 255, 255, 255));
		targetColor.a = 255;

		var pixels = new Color32[_baseAtlasPixels.Length];
		for (var i = 0; i < _baseAtlasPixels.Length; i++) {
			var p = _baseAtlasPixels[i];
			if (Mathf.Abs(p.r - PaintableOrangeRgb.r) <= 40
				&& Mathf.Abs(p.g - PaintableOrangeRgb.g) <= 40
				&& Mathf.Abs(p.b - PaintableOrangeRgb.b) <= 40) {
				pixels[i] = targetColor;
			} else {
				pixels[i] = p;
			}
		}

		var recolouredTex = new Texture2D(_baseAtlasWidth, _baseAtlasHeight, TextureFormat.RGBA32, true) {
			name = $"SprayerMkII_Colours_{swatch.Name}",
			filterMode = BaseMaterial.mainTexture != null ? BaseMaterial.mainTexture.filterMode : FilterMode.Bilinear,
			wrapMode = BaseMaterial.mainTexture != null ? BaseMaterial.mainTexture.wrapMode : TextureWrapMode.Clamp
		};
		recolouredTex.SetPixels32(pixels);
		recolouredTex.Apply(true, true);

		var paintedMat = new Material(BaseMaterial) {
			name = $"SprayerMkII_{swatch.Name}",
			mainTexture = recolouredTex
		};

		PaintedMaterials[swatch.Index] = paintedMat;
		return paintedMat;
	}

	internal static void ApplyPaintedMaterial(Thing thing) {
		if (thing == null || BaseMaterial == null) {
			return;
		}

		var targetMat = GetPaintedMaterial(thing.CustomColor);
		if (targetMat == null) {
			return;
		}

		var bundleModelRoot = thing.transform.Find("MkII_BundleModel");

		if (thing.Renderers != null && thing.Renderers.Count > 0) {
			foreach (var tr in thing.Renderers) {
				if (tr == null) {
					continue;
				}
				var renderer = tr.GetRenderer();
				var isBundleRenderer = bundleModelRoot != null && renderer != null && renderer.transform.IsChildOf(bundleModelRoot);
				var mats = tr.sharedMaterials;
				if (mats == null || mats.Length == 0) {
					continue;
				}

				var changed = false;
				for (var i = 0; i < mats.Length; i++) {
					if (isBundleRenderer || IsMkIIPaintableSlot(mats[i])) {
						if (mats[i] != targetMat) {
							mats[i] = targetMat;
							changed = true;
						}
					}
				}

				if (changed) {
					tr.sharedMaterials = mats;
					tr.RenderStateChanged();
					if (renderer != null) {
						renderer.sharedMaterials = mats;
					}
				}
			}
		} else if (bundleModelRoot != null) {
			foreach (var mr in bundleModelRoot.GetComponentsInChildren<MeshRenderer>(true)) {
				var mats = mr.sharedMaterials;
				var changed = false;
				for (var i = 0; i < mats.Length; i++) {
					if (mats[i] != targetMat) {
						mats[i] = targetMat;
						changed = true;
					}
				}
				if (changed) {
					mr.sharedMaterials = mats;
				}
			}
		}
	}

	private static bool IsMkIIPaintableSlot(Material mat) {
		if (mat == null) {
			return false;
		}
		if (mat == BaseMaterial) {
			return true;
		}
		foreach (var cached in PaintedMaterials.Values) {
			if (mat == cached) {
				return true;
			}
		}
		return false;
	}

	private static bool EnsureBaseAtlasPixels() {
		if (_baseAtlasPixels != null) {
			return true;
		}

		var srcTex = BaseMaterial?.mainTexture;
		if (srcTex == null) {
			return false;
		}

		if (srcTex is Texture2D tex2D && tex2D.isReadable) {
			_baseAtlasWidth = tex2D.width;
			_baseAtlasHeight = tex2D.height;
			_baseAtlasPixels = tex2D.GetPixels32();
			return true;
		}

		var w = srcTex.width;
		var h = srcTex.height;
		var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
		var prevRt = RenderTexture.active;
		try {
			Graphics.Blit(srcTex, rt);
			RenderTexture.active = rt;
			var readable = new Texture2D(w, h, TextureFormat.RGBA32, false);
			readable.ReadPixels(new Rect(0f, 0f, w, h), 0, 0);
			readable.Apply(false);
			_baseAtlasWidth = w;
			_baseAtlasHeight = h;
			_baseAtlasPixels = readable.GetPixels32();
			Object.DestroyImmediate(readable);
			return true;
		} finally {
			RenderTexture.active = prevRt;
			RenderTexture.ReleaseTemporary(rt);
		}
	}
}

[HarmonyPatch(typeof(Thing), nameof(Thing.SetCustomColor), new[] { typeof(bool) })]
internal static class MkIISetCustomColorBoolPatch {
	[HarmonyPostfix]
	private static void Postfix(Thing __instance) {
		if (__instance != null && __instance.PrefabHash == MkIIPrefabFactory.PrefabHash) {
			MkIIModelBuilder.ApplyPaintedMaterial(__instance);
		}
	}
}

[HarmonyPatch(typeof(Thing), nameof(Thing.SetCustomColor), new[] { typeof(int), typeof(bool) })]
internal static class MkIISetCustomColorIntPatch {
	[HarmonyPostfix]
	private static void Postfix(Thing __instance) {
		if (__instance != null && __instance.PrefabHash == MkIIPrefabFactory.PrefabHash) {
			MkIIModelBuilder.ApplyPaintedMaterial(__instance);
		}
	}
}

[HarmonyPatch(typeof(Thing), nameof(Thing.SetCustomColor), new[] { typeof(ColorSwatch) })]
internal static class MkIISetCustomColorSwatchPatch {
	[HarmonyPostfix]
	private static void Postfix(Thing __instance) {
		if (__instance != null && __instance.PrefabHash == MkIIPrefabFactory.PrefabHash) {
			MkIIModelBuilder.ApplyPaintedMaterial(__instance);
		}
	}
}
