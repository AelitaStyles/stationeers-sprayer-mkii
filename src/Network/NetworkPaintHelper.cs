// Copyright (c) 2026 Aelita Styles
// Licensed under the MIT License. See LICENSE in the project root for details.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Networks;
using UnityEngine;

namespace SprayerMkII;

/// <summary>
/// Collects all paintable segments in a network (pipes, cables, chutes, and optional
/// LibConstruct/modded networks) and assigns each segment a stable stripe index.
///
/// Ordering uses BFS hop distance from the clicked segment along grid/connection edges
/// so stripe patterns flow evenly down straight runs and branches. Modded LibConstruct
/// pseudo-networks are queried via cached reflection to avoid a hard assembly dependency.
/// </summary>
internal static class NetworkPaintHelper {
	internal readonly struct SegmentEntry {
		public readonly Thing Segment;
		public readonly int PatternIndex;

		public SegmentEntry(Thing segment, int patternIndex) {
			Segment = segment;
			PatternIndex = patternIndex;
		}
	}

	private sealed class PseudoNetworkInfo {
		public readonly PropertyInfo NetworkProp;
		public readonly PropertyInfo ConnectionsProp;
		public PropertyInfo MembersProp;

		public PseudoNetworkInfo(PropertyInfo networkProp, PropertyInfo connectionsProp) {
			NetworkProp = networkProp;
			ConnectionsProp = connectionsProp;
		}
	}

	private const string PseudoNetworkInterfaceName = "LibConstruct.IPseudoNetworkMember`1";
	private static readonly Dictionary<Type, PseudoNetworkInfo> PseudoNetworkCache = new();

	internal static bool TryGetNetworkSegments(Thing startThing, out List<SegmentEntry> ordered) {
		ordered = null;

		if (startThing is Pipe startPipe) {
			// Vents, radiators, and standalone tanks also inherit from Pipe;
			// limit pipe network painting to pipes and inline tanks.
			if (!IsNetworkPipeSegment(startPipe) || startPipe.StructureNetwork == null) {
				return false;
			}

			var members = CollectFromStructureNetwork<Pipe>(startPipe.StructureNetwork, IsNetworkPipeSegment);
			ordered = OrderNetwork(
				startPipe,
				members,
				node => GetSmallCellNeighbors(node, n => n.StructureNetwork == startPipe.StructureNetwork && IsPaintable(n)),
				IsNetworkPipeSegment
			);
			return ordered.Count > 0;
		}

		if (startThing is Cable startCable && startCable.CableNetwork != null) {
			var members = new List<Cable>();
			if (startCable.CableNetwork.CableList != null) {
				foreach (var cable in startCable.CableNetwork.CableList) {
					if (IsPaintable(cable)) {
						members.Add(cable);
					}
				}
			}

			ordered = OrderNetwork(
				startCable,
				members,
				node => GetSmallCellNeighbors(node, n => n.CableNetwork == startCable.CableNetwork && IsPaintable(n))
			);
			return ordered.Count > 0;
		}

		if (startThing is Chute startChute && startChute.StructureNetwork != null) {
			var members = CollectFromStructureNetwork<Chute>(startChute.StructureNetwork);
			ordered = OrderNetwork(
				startChute,
				members,
				node => GetSmallCellNeighbors(node, n => n.StructureNetwork == startChute.StructureNetwork && IsPaintable(n))
			);
			return ordered.Count > 0;
		}

		// Optional mod support: non-device SmallGrid segments in a LibConstruct
		// pseudo-network (such as ReVolt cable trays) or custom StructureNetwork.
		if (startThing is SmallGrid startGrid && startThing is not Device) {
			if (TryGetPseudoNetworkSegments(startGrid, out ordered)) {
				return ordered.Count > 0;
			}

			if (startThing is INetworkedStructure netStruct && netStruct.StructureNetwork != null) {
				var members = CollectFromStructureNetwork<SmallGrid>(netStruct.StructureNetwork, g => g is not Device);
				ordered = OrderByConnections(startGrid, members, g => g.OpenEnds);
				return ordered.Count > 0;
			}
		}

		return false;
	}

	private static bool IsNetworkPipeSegment(Pipe pipe) {
		return pipe is Piping || pipe is InLineTank;
	}

	private static bool IsPaintable(Thing thing) {
		return thing != null && !thing.HasColorState && thing.PaintableMaterial != null;
	}

	private static List<T> CollectFromStructureNetwork<T>(StructureNetwork network, Func<T, bool> filter = null)
		where T : SmallGrid {
		var list = new List<T>();
		if (network.StructureList == null) {
			return list;
		}

		foreach (var member in network.StructureList) {
			if (member is T typed && IsPaintable(typed) && (filter == null || filter(typed))) {
				list.Add(typed);
			}
		}

		return list;
	}

	private static bool TryGetPseudoNetworkSegments(SmallGrid startGrid, out List<SegmentEntry> ordered) {
		ordered = null;

		var info = GetPseudoNetworkInfo(startGrid.GetType());
		var network = info?.NetworkProp.GetValue(startGrid);
		if (network == null) {
			return false;
		}

		info.MembersProp ??= network.GetType().GetProperty("Members", BindingFlags.Public | BindingFlags.Instance);
		if (info.MembersProp?.GetValue(network) is not IEnumerable rawMembers) {
			return false;
		}

		var members = new List<SmallGrid>();
		var seen = new HashSet<SmallGrid>();
		foreach (var item in rawMembers) {
			if (item is SmallGrid grid && grid is not Device && IsPaintable(grid) && seen.Add(grid)) {
				members.Add(grid);
			}
		}

		if (members.Count == 0) {
			return false;
		}

		ordered = OrderByConnections(startGrid, members, grid => {
			var gridInfo = GetPseudoNetworkInfo(grid.GetType()) ?? info;
			var connProp = gridInfo.ConnectionsProp;
			if (connProp?.DeclaringType?.IsInstanceOfType(grid) == true
				&& connProp.GetValue(grid) is IEnumerable<Connection> conns) {
				return conns;
			}

			return grid.OpenEnds;
		});
		return true;
	}

	private static PseudoNetworkInfo GetPseudoNetworkInfo(Type type) {
		if (PseudoNetworkCache.TryGetValue(type, out var cached)) {
			return cached;
		}

		PseudoNetworkInfo found = null;
		foreach (var iface in type.GetInterfaces()) {
			if (iface.IsGenericType && iface.GetGenericTypeDefinition().FullName == PseudoNetworkInterfaceName) {
				var netProp = iface.GetProperty("Network", BindingFlags.Public | BindingFlags.Instance);
				var connProp = iface.GetProperty("Connections", BindingFlags.Public | BindingFlags.Instance);
				if (netProp != null) {
					found = new PseudoNetworkInfo(netProp, connProp);
					break;
				}
			}
		}

		PseudoNetworkCache[type] = found;
		return found;
	}

	private static List<T> GetSmallCellNeighbors<T>(T current, Func<T, bool> canTraverse) where T : SmallGrid {
		var neighbors = new List<T>(4);
		if (current.OpenEnds == null) {
			return neighbors;
		}

		foreach (var end in current.OpenEnds) {
			if (end == null) {
				continue;
			}

			end.Initialize();
			TryAddCellNeighbor(end.FacingGrid);
			if (end.LocalGrid != end.FacingGrid) {
				TryAddCellNeighbor(end.LocalGrid);
			}

			void TryAddCellNeighbor(Grid3 grid) {
				var neighbor = SmallCell.Get<T>(grid);
				if (neighbor != null
					&& neighbor != current
					&& neighbor.IsConnected(end)
					&& canTraverse(neighbor)
					&& !neighbors.Contains(neighbor)) {
					neighbors.Add(neighbor);
				}
			}
		}

		return neighbors;
	}

	private static List<SegmentEntry> OrderByConnections(
		SmallGrid startGrid,
		List<SmallGrid> allMembers,
		Func<SmallGrid, IEnumerable<Connection>> getConnections
	) {
		var connsByMember = new Dictionary<SmallGrid, List<Connection>>(allMembers.Count);
		var membersByGrid = new Dictionary<Grid3, List<SmallGrid>>();

		foreach (var member in allMembers) {
			var conns = new List<Connection>();
			var raw = getConnections(member);
			if (raw != null) {
				foreach (var conn in raw) {
					if (conn == null) {
						continue;
					}

					conn.Initialize();
					conns.Add(conn);
					IndexAtGrid(conn.LocalGrid, member);
				}
			}

			if (conns.Count == 0 && member.SmallCell != null) {
				IndexAtGrid(member.SmallCell.SmallGrid, member);
			}

			connsByMember[member] = conns;
		}

		return OrderNetwork(startGrid, allMembers, current => {
			var neighbors = new List<SmallGrid>(4);
			if (!connsByMember.TryGetValue(current, out var conns)) {
				return neighbors;
			}

			foreach (var end in conns) {
				CollectLinkedAt(end.FacingGrid);
				if (end.LocalGrid != end.FacingGrid) {
					CollectLinkedAt(end.LocalGrid);
				}

				void CollectLinkedAt(Grid3 targetGrid) {
					if (!membersByGrid.TryGetValue(targetGrid, out var candidates)) {
						return;
					}

					foreach (var candidate in candidates) {
						if (candidate == current || neighbors.Contains(candidate)) {
							continue;
						}

						if (IsLinked(end, candidate)) {
							neighbors.Add(candidate);
						}
					}
				}
			}

			return neighbors;
		});

		void IndexAtGrid(Grid3 grid, SmallGrid member) {
			if (!membersByGrid.TryGetValue(grid, out var list)) {
				list = new List<SmallGrid>(2);
				membersByGrid[grid] = list;
			}

			if (!list.Contains(member)) {
				list.Add(member);
			}
		}

		bool IsLinked(Connection end, SmallGrid candidate) {
			if (candidate.IsConnected(end)) {
				return true;
			}

			if (connsByMember.TryGetValue(candidate, out var otherConns)) {
				foreach (var other in otherConns) {
					if (other.LocalGrid == end.FacingGrid && other.FacingGrid == end.LocalGrid) {
						return true;
					}
				}
			}

			return false;
		}
	}

	// Two-pass BFS per connected component:
	// 1. Traverse from the clicked segment so painting radiates outward if a can runs dry.
	// 2. Traverse from a deterministic endpoint to assign stripe indices, keeping patterns consistent across refills.
	private static List<SegmentEntry> OrderNetwork<T>(
		T startSegment,
		List<T> allMembers,
		Func<T, List<T>> getNeighbors,
		Func<T, bool> shouldPaint = null
	) where T : SmallGrid {
		var result = new List<SegmentEntry>(Math.Max(allMembers.Count, 1));
		var visited = new HashSet<T>();

		if (IsPaintable(startSegment) && visited.Add(startSegment)) {
			ProcessComponent(startSegment, visited, getNeighbors, shouldPaint, result);
		}

		// Include segments in the same network separated by an inline device.
		foreach (var member in allMembers) {
			if (member != null && visited.Add(member)) {
				ProcessComponent(member, visited, getNeighbors, shouldPaint, result);
			}
		}

		return result;
	}

	private static void ProcessComponent<T>(
		T seed,
		HashSet<T> visited,
		Func<T, List<T>> getNeighbors,
		Func<T, bool> shouldPaint,
		List<SegmentEntry> result
	) where T : SmallGrid {
		var paintOrder = new List<T>();
		var adjacency = new Dictionary<T, List<T>>();
		var queue = new Queue<T>();
		queue.Enqueue(seed);

		while (queue.Count > 0) {
			var current = queue.Dequeue();
			paintOrder.Add(current);

			var neighbors = getNeighbors(current);
			adjacency[current] = neighbors;

			foreach (var neighbor in neighbors) {
				if (visited.Add(neighbor)) {
					queue.Enqueue(neighbor);
				}
			}
		}

		var root = PickPatternRoot(paintOrder, adjacency, shouldPaint);
		var patternIndex = new Dictionary<T, int>(paintOrder.Count) { [root] = 0 };
		var distQueue = new Queue<(T Node, int Index)>();
		distQueue.Enqueue((root, 0));

		while (distQueue.Count > 0) {
			var (current, index) = distQueue.Dequeue();
			var nextIndex = shouldPaint == null || shouldPaint(current) ? index + 1 : index;

			if (!adjacency.TryGetValue(current, out var neighbors)) {
				continue;
			}

			foreach (var neighbor in neighbors) {
				if (!patternIndex.ContainsKey(neighbor)) {
					patternIndex[neighbor] = nextIndex;
					distQueue.Enqueue((neighbor, nextIndex));
				}
			}
		}

		foreach (var node in paintOrder) {
			if (shouldPaint == null || shouldPaint(node)) {
				result.Add(new SegmentEntry(node, patternIndex.TryGetValue(node, out var idx) ? idx : 0));
			}
		}
	}

	// Prefer a degree-1 endpoint (lowest world position) so linear runs start at an end;
	// fall back to the lowest-position node for closed loops.
	private static T PickPatternRoot<T>(
		List<T> component,
		Dictionary<T, List<T>> adjacency,
		Func<T, bool> shouldPaint
	) where T : SmallGrid {
		T bestEndpoint = null;
		T bestAny = null;

		foreach (var node in component) {
			if (shouldPaint != null && !shouldPaint(node)) {
				continue;
			}

			if (bestAny == null || ComparePosition(node, bestAny) < 0) {
				bestAny = node;
			}

			var degree = adjacency.TryGetValue(node, out var neighbors) ? neighbors.Count : 0;
			if (degree <= 1 && (bestEndpoint == null || ComparePosition(node, bestEndpoint) < 0)) {
				bestEndpoint = node;
			}
		}

		return bestEndpoint ?? bestAny ?? component[0];
	}

	private static int ComparePosition(Thing a, Thing b) {
		var pa = a.Position;
		var pb = b.Position;

		var cmp = Mathf.RoundToInt(pa.x * 100f).CompareTo(Mathf.RoundToInt(pb.x * 100f));
		if (cmp != 0) return cmp;

		cmp = Mathf.RoundToInt(pa.y * 100f).CompareTo(Mathf.RoundToInt(pb.y * 100f));
		if (cmp != 0) return cmp;

		cmp = Mathf.RoundToInt(pa.z * 100f).CompareTo(Mathf.RoundToInt(pb.z * 100f));
		if (cmp != 0) return cmp;

		return a.ReferenceId.CompareTo(b.ReferenceId);
	}
}
