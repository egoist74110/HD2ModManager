using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using HD2SourceMigration.Retarget;
using HD2SourceMigration.Segmentation;
using HD2SourceMigration.VpkConversion;
using HD2SourceModel;

namespace HD2SourceMigration.IasConversion;

internal static class IasNativeMesh
{
	internal static UnitMeshModel UseFourWeightStreams(UnitMeshModel donor)
	{
		var format = UnitStreamAbiProfiles.GetCanonicalWriterProfile(donor) == UnitStreamAbiProfileKind.LegacyPre10800438
			? 31u : 35u;
		var visualStreams = donor.Meshes.Where(mesh => mesh.LodIndex >= 0).Select(mesh => (int)mesh.StreamIndex).ToHashSet();
		return donor with
		{
			Streams = donor.Streams.Select(stream =>
			{
				if (!visualStreams.Contains(stream.Index))
					return stream;
				var components = stream.Components.Select(component => component.Type == 7
					? component with { Format = format, FormatName = "vec4_half", Size = 8 }
					: component).ToArray();
				return stream with { Components = components, VertexStride = checked((uint)components.Sum(component => component.Size)) };
			}).ToArray(),
		};
	}

	internal static RetargetedInfluence[] MergeInfluences(IEnumerable<RetargetedInfluence> source)
	{
		var top = source.Where(item => item.Weight > 0 && float.IsFinite(item.Weight))
			.GroupBy(item => item.BoneHash)
			.Select(group => new RetargetedInfluence(group.Key, group.Sum(item => item.Weight)))
			.OrderByDescending(item => item.Weight).ThenBy(item => item.BoneHash).Take(4).ToArray();
		var sum = top.Sum(item => item.Weight);
		if (sum <= 0 || !float.IsFinite(sum))
			throw new InvalidDataException("Vertex has no finite positive native skin weights.");
		return top.Select(item => item with { Weight = item.Weight / sum }).ToArray();
	}

	internal static (IReadOnlyDictionary<int, RetargetedMesh> Meshes, IReadOnlyDictionary<int, ulong> Materials)
		Convert(UnitMeshModel model, IasRigProfile profile, Hd2Rig target, UnitTransformInfo avatar,
			Vec3 headAnchor, Func<string, uint> hash, bool refitArms,
			IReadOnlyDictionary<int, Vec3>? armShifts = null)
	{
		// 给了逐骨平移就不走 ArmRefit：两者都在把手臂搬到潜兵关节上，只能用一个。
		if (armShifts is not null)
			refitArms = false;
		var shiftByHash = armShifts?.ToDictionary(pair => avatar.NameHashes[pair.Key], pair => pair.Value);
		var transform = model.TransformInfo;
		var byPaletteSlot = profile.Nodes.Select((node, index) => (node.Slot, index)).ToDictionary(item => item.Slot, item => item.index);
		var sourceBones = profile.Nodes.Select((node, index) =>
		{
			var stored = Hd2RigReader.FromHd2RowVector(transform.Matrices[node.Slot].Values);
			if (transform.NameHashes[node.Slot] != hash(node.Name) ||
				(stored.Origin - node.World.Origin).Length > 0.0001)
				throw new InvalidDataException($"Rig profile and Unit bind differ at {node.Name} ({node.Slot}).");
			return new Hd2RigBone(index, node.Name, node.Parent, stored);
		}).ToArray();
		var semanticBySlot = new Dictionary<int, int>();
		var armRig = BuildArmFoldContext(sourceBones, target);
		var materials = new List<ulong>();
		var meshes = new Dictionary<int, RetargetedMesh>();
		foreach (var lod in model.RawMeshData.Where(mesh => mesh.LodIndex >= 0 &&
			mesh.Sections.Any(section => section.Triangles.Count > 0)).GroupBy(mesh => mesh.LodIndex))
		{
			var sourceVertices = new List<SourceVertex>();
			var sourceWeights = new List<SourceVertexWeight[]>();
			var surfaceComponents = new List<UnitVertexComponentValue[]>();
			var buckets = new Dictionary<int, List<(int A, int B, int C)>>();
			foreach (var mesh in lod)
			{
				var visibleSections = mesh.Sections.Where(section => section.Triangles.Count > 0).ToArray();
				var materialBindings = CanonicalMaterialBindingResolver.Resolve(model, mesh, mesh);
				// The sample's LOD4 is a shadow proxy with only an unbound SDK default slot.
				if (mesh.LodIndex == 4 && materialBindings.IsValid && materialBindings.ResolvedSectionBindings.Count == 0)
					continue;
				if (!materialBindings.IsValid || materialBindings.ResolvedSectionBindings.Count != visibleSections.Length)
					throw new InvalidDataException($"Unbound IAS visual material in mesh {mesh.MeshInfoIndex}, LOD {mesh.LodIndex}: {string.Join("; ", materialBindings.Diagnostics)}");
				var meshInfo = model.Meshes.Single(info => info.Index == mesh.MeshInfoIndex);
				var toWorld = Hd2RigReader.FromHd2RowVector(transform.Matrices[(int)meshInfo.TransformIndex].Values);
				var normalToWorld = toWorld.Inverse().Transposed();
				var materialMap = CanonicalVertexMaterialResolver.Resolve(mesh);
				if (!materialMap.IsValid)
					throw new InvalidDataException(string.Join("; ", materialMap.Diagnostics));
				var weightsByVertex = materialMap.MaterialByVertex.ToDictionary(pair => pair.Key,
					pair => DecodeWeights(model, mesh, mesh.Vertices[(int)pair.Key], pair.Value));
				FillUnweightedVertices(mesh, materialMap.MaterialByVertex, weightsByVertex);
				var vertices = new Dictionary<uint, int>();
				foreach (var index in materialMap.MaterialByVertex.Keys)
				{
					var vertex = mesh.Vertices[(int)index];
					var position = Vector(Component(vertex, 0).FloatValues);
					var normal = Vector(Component(vertex, 1).FloatValues);
					var uv = Component(vertex, 4).FloatValues;
					vertices.Add(index, sourceVertices.Count);
					var worldPos = toWorld.TransformPoint(position);
					sourceVertices.Add(new(worldPos,
						(normalToWorld.TransformPoint(normal) - normalToWorld.Origin).Normalized(), uv[0], uv[1], default, default, default));
					surfaceComponents.Add(vertex.Components.Where(item => item.Type is 4 or 5).ToArray());
					var weights = weightsByVertex[index];
					sourceWeights.Add(FoldVertexWeights(weights, byPaletteSlot, semanticBySlot, profile, worldPos, armRig));
				}
				foreach (var binding in materialBindings.ResolvedSectionBindings)
				{
					var section = visibleSections[binding.FinalSectionIndex];
					var id = binding.MaterialId;
					var materialIndex = materials.IndexOf(id);
					if (materialIndex < 0)
					{
						materialIndex = materials.Count;
						materials.Add(id);
					}
					if (!buckets.TryGetValue(materialIndex, out var triangles))
						buckets[materialIndex] = triangles = [];
					triangles.AddRange(section.Triangles.Select(face => (vertices[face.A], vertices[face.B], vertices[face.C])));
				}
			}

			if (sourceVertices.Count == 0)
				continue;
			var trianglesForRetarget = buckets.SelectMany(pair => pair.Value.Select(face =>
				new SourceTriangle(face.A, face.B, face.C, pair.Key))).ToArray();
			var geometry = new SourceGeometry(sourceVertices, trianglesForRetarget, []);
			var result = Retargeter.RunNative(sourceBones, geometry, sourceWeights, target, headAnchor, refitArms);
			var group = new MeshGroup("IAS", MeshGroupKind.BodyPart, default, 0, 1, [],
				Enumerable.Range(0, sourceVertices.Count).ToArray());
			var normals = RetargetedMeshBuilder.ComputeFinalNormals(group, geometry, result.Output, buckets);
			var output = new RetargetedVertex[sourceVertices.Count];
			for (var i = 0; i < output.Length; i++)
			{
				var influences = MergeInfluences(sourceWeights[i].Select(item =>
				{
					var name = sourceBones[item.BoneIndex].DisplayName;
					if (!result.Resolved.TryGetValue(item.BoneIndex, out var targetIndex) ||
						target.Bones[targetIndex].Name != name)
						throw new InvalidDataException($"Native avatar is missing weighted IAS semantic bone {name}.");
					return new RetargetedInfluence(avatar.NameHashes[targetIndex], item.Weight);
				}));
				var position = shiftByHash is null ? result.Output[i] : result.Output[i] + IasArmPose.BlendShift(influences, shiftByHash);
				output[i] = new(position, normals[i], sourceVertices[i].U, sourceVertices[i].V, influences)
				{
					NativeSurfaceComponents = surfaceComponents[i],
				};
			}
			meshes.Add(lod.Key, new(output, buckets.Select(pair => new RetargetedSection(pair.Key, pair.Value)).ToArray()));
		}
		if (!meshes.ContainsKey(0))
			throw new InvalidDataException("IAS Unit is missing a visual LOD0.");
		return (meshes, materials.Select((id, index) => (id, index)).ToDictionary(pair => pair.index, pair => pair.id));
	}

	internal static IReadOnlyList<(int Slot, float Weight)> DecodeWeights(UnitMeshModel model, UnitRawMeshData mesh,
		UnitRawVertexRecord vertex, uint material)
	{
		var indices = Component(vertex, 6).UIntValues;
		var component = Component(vertex, 7);
		var weights = component.FloatValues.Length > 0 ? component.FloatValues : component.UIntValues.Select(weight => weight / 255f).ToArray();
		if ((weights.Length != indices.Length && weights.Length != 1) || weights.Length > 4)
			throw new InvalidDataException($"Invalid skin weight arity in mesh {mesh.MeshInfoIndex}.");
		var info = model.BoneInfos.Single(bone => bone.Index == mesh.LodIndex);
		var remap = info.Remaps.Single(item => item.MaterialIndex == material);
		var result = new List<(int Slot, float Weight)>(weights.Length);
		for (var i = 0; i < weights.Length; i++)
		{
			if (weights[i] > 0 && float.IsFinite(weights[i]))
				result.Add((checked((int)info.RealIndices[(int)remap.FakeIndices[(int)indices[i]]]), weights[i]));
		}
		return result;
	}

	private static void FillUnweightedVertices(UnitRawMeshData mesh, IReadOnlyDictionary<uint, uint> materialByVertex,
		Dictionary<uint, IReadOnlyList<(int Slot, float Weight)>> weights)
	{
		var empty = weights.Where(pair => pair.Value.Count == 0).Select(pair => pair.Key).ToHashSet();
		if (empty.Count == 0)
			return;
		var nearest = new Dictionary<uint, (uint Neighbor, double Distance)>();
		var positions = weights.Keys.ToDictionary(index => index,
			index => Vector(Component(mesh.Vertices[(int)index], 0).FloatValues));
		var weightedByMaterial = weights.Keys.Where(index => !empty.Contains(index)).ToLookup(index => materialByVertex[index]);
		foreach (var triangle in mesh.Sections.SelectMany(section => section.Triangles))
		{
			Consider(triangle.A, triangle.B);
			Consider(triangle.A, triangle.C);
			Consider(triangle.B, triangle.A);
			Consider(triangle.B, triangle.C);
			Consider(triangle.C, triangle.A);
			Consider(triangle.C, triangle.B);
		}
		foreach (var index in empty)
		{
			if (!nearest.TryGetValue(index, out var best))
			{
				foreach (var candidate in weightedByMaterial[materialByVertex[index]])
					Consider(index, candidate);
				if (!nearest.TryGetValue(index, out best))
					throw new InvalidDataException($"Unweighted IAS vertex {index} has no weighted vertex in its material in mesh {mesh.MeshInfoIndex}.");
			}
			weights[index] = weights[best.Neighbor];
		}

		void Consider(uint vertex, uint neighbor)
		{
			if (!empty.Contains(vertex) || empty.Contains(neighbor))
				return;
			var difference = positions[vertex] - positions[neighbor];
			var distance = Vec3.Dot(difference, difference);
			if (!nearest.TryGetValue(vertex, out var current) || distance < current.Distance)
				nearest[vertex] = (neighbor, distance);
		}
	}

	internal static UnitVertexComponentValue Component(UnitRawVertexRecord vertex, uint type) =>
		vertex.Components.Single(item => item.Type == type && item.Index == 0);

	private static Vec3 Vector(IReadOnlyList<float> values) => new(values[0], values[1], values[2]);

	internal sealed record ArmSideContext(
		int ClavicleIndex, Vec3 Clavicle,
		int ShoulderIndex, Vec3 Shoulder,
		Vec3 Elbow, Vec3 Hand,
		IReadOnlySet<int> NativeArmIndices);

	internal sealed record TwistFoldEntry(int StartIndex, int EndIndex, float EndRatio);

	internal sealed record ArmFoldContext(
		int ChestIndex,
		IReadOnlyList<ArmSideContext> Sides,
		IReadOnlyDictionary<int, TwistFoldEntry> MisalignedTwists);

	internal static ArmFoldContext? BuildArmFoldContext(IReadOnlyList<Hd2RigBone> sourceBones, Hd2Rig? target = null)
	{
		var byName = new Dictionary<string, int>(StringComparer.Ordinal);
		for (var i = 0; i < sourceBones.Count; i++)
			byName.TryAdd(sourceBones[i].DisplayName, i);
		if (!byName.TryGetValue("chest", out var chestIndex))
			return null;

		var sides = new List<ArmSideContext>(2);
		var twists = new Dictionary<int, TwistFoldEntry>();
		Dictionary<string, int>? targetByName = null;
		if (target is not null)
		{
			targetByName = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach (var b in target.Bones)
				targetByName.TryAdd(b.DisplayName, b.Index);
		}

		foreach (var side in new[] { "l", "r" })
		{
			if (!byName.TryGetValue($"{side}_clavicle", out var clavicleIdx)
				|| !byName.TryGetValue($"{side}_shoulder", out var shoulderIdx)
				|| !byName.TryGetValue($"{side}_elbow", out var elbowIdx)
				|| !byName.TryGetValue($"{side}_hand", out var handIdx))
				continue;
			var armSet = new HashSet<int> { clavicleIdx, shoulderIdx, elbowIdx, handIdx };
			foreach (var suffix in new[] { "shoulder_twist", "hand_twist", "shoulderarmour" })
			{
				if (byName.TryGetValue($"{side}_{suffix}", out var extra))
					armSet.Add(extra);
			}
			sides.Add(new(
				clavicleIdx, sourceBones[clavicleIdx].WorldBind.Origin,
				shoulderIdx, sourceBones[shoulderIdx].WorldBind.Origin,
				sourceBones[elbowIdx].WorldBind.Origin,
				sourceBones[handIdx].WorldBind.Origin,
				armSet));

			if (target is not null && targetByName is not null)
			{
				foreach (var (twistName, startName, endName) in new[]
				{
					($"{side}_shoulder_twist", $"{side}_shoulder", $"{side}_elbow"),
					($"{side}_hand_twist", $"{side}_elbow", $"{side}_hand"),
				})
				{
					if (!byName.TryGetValue(twistName, out var sTwist)
						|| !byName.TryGetValue(startName, out var sStart)
						|| !byName.TryGetValue(endName, out var sEnd)
						|| !targetByName.TryGetValue(twistName, out var tTwist)
						|| !targetByName.TryGetValue(startName, out var tStart)
						|| !targetByName.TryGetValue(endName, out var tEnd))
						continue;
					var sRatio = SegmentRatio(
						sourceBones[sTwist].WorldBind.Origin,
						sourceBones[sStart].WorldBind.Origin,
						sourceBones[sEnd].WorldBind.Origin);
					var tRatio = SegmentRatio(
						target.Bones[tTwist].WorldBind.Origin,
						target.Bones[tStart].WorldBind.Origin,
						target.Bones[tEnd].WorldBind.Origin);
					var distDiff = Math.Abs(
						(sourceBones[sTwist].WorldBind.Origin - sourceBones[sStart].WorldBind.Origin).Length
						- (target.Bones[tTwist].WorldBind.Origin - target.Bones[tStart].WorldBind.Origin).Length);
					if (Math.Abs(sRatio - tRatio) > 0.25 || distDiff > 0.05)
					{
						var endRatio = sRatio > 0.75 ? 0.5f : (float)sRatio;
						twists[sTwist] = new(sStart, sEnd, endRatio);
						armSet.Remove(sTwist);
					}
				}
			}
		}
		return sides.Count > 0 || twists.Count > 0 ? new(chestIndex, sides, twists) : null;
	}

	private static double SegmentRatio(Vec3 p, Vec3 a, Vec3 b)
	{
		var ab = b - a;
		var lenSq = Vec3.Dot(ab, ab);
		return lenSq < 1e-12 ? 0.0 : Clamp01(Vec3.Dot(p - a, ab) / lenSq);
	}

	internal static SourceVertexWeight[] FoldVertexWeights(
		IReadOnlyList<(int Slot, float Weight)> weights,
		IReadOnlyDictionary<int, int> byPaletteSlot,
		IDictionary<int, int> semanticBySlot,
		IasRigProfile profile,
		Vec3 worldPos,
		ArmFoldContext? armContext)
	{
		var resolved = new List<(int Semantic, bool IsCustom, float Weight)>(weights.Count);
		var customChestWeight = 0f;
		foreach (var item in weights)
		{
			if (!byPaletteSlot.TryGetValue(item.Slot, out var nodeIndex))
				throw new InvalidDataException($"Weighted palette slot {item.Slot} is absent from the rig.");
			if (!semanticBySlot.TryGetValue(item.Slot, out var semantic))
			{
				semantic = profile.ResolveSemanticNode(nodeIndex);
				semanticBySlot.Add(item.Slot, semantic);
			}
			var isCustom = (profile.Nodes[nodeIndex].Flags & 256) != 0;
			if (!isCustom && armContext is not null && armContext.MisalignedTwists.TryGetValue(semantic, out var twistFold))
			{
				var endW = item.Weight * twistFold.EndRatio;
				var startW = item.Weight - endW;
				if (startW > 1e-6f)
					resolved.Add((twistFold.StartIndex, false, startW));
				if (endW > 1e-6f)
					resolved.Add((twistFold.EndIndex, false, endW));
			}
			else if (isCustom && armContext is not null && semantic == armContext.ChestIndex)
				customChestWeight += item.Weight;
			else
				resolved.Add((semantic, isCustom, item.Weight));
		}

		if (customChestWeight > 0f && armContext is not null)
		{
			var (bestSide, armFactor) = ComputeArmFoldFactor(worldPos, armContext);
			if (armFactor > 0f && bestSide is not null)
			{
				var stayChest = customChestWeight * (1f - armFactor);
				var moveArm = customChestWeight * armFactor;
				if (stayChest > 1e-6f)
					resolved.Add((armContext.ChestIndex, true, stayChest));

				var armTotal = 0f;
				foreach (var entry in resolved)
				{
					if (!entry.IsCustom && bestSide.NativeArmIndices.Contains(entry.Semantic))
						armTotal += entry.Weight;
				}
				if (armTotal > 1e-4f)
				{
					var count = resolved.Count;
					for (var i = 0; i < count; i++)
					{
						var entry = resolved[i];
						if (!entry.IsCustom && bestSide.NativeArmIndices.Contains(entry.Semantic))
							resolved.Add((entry.Semantic, false, moveArm * (entry.Weight / armTotal)));
					}
				}
				else
				{
					resolved.Add((bestSide.ShoulderIndex, false, moveArm));
				}
			}
			else
			{
				resolved.Add((armContext.ChestIndex, true, customChestWeight));
			}
		}

		return resolved
			.GroupBy(item => item.Semantic)
			.Select(group => new SourceVertexWeight(group.Key, group.Sum(item => item.Weight)))
			.Where(item => item.Weight > 0f)
			.ToArray();
	}

	private static (ArmSideContext? Side, float Factor) ComputeArmFoldFactor(Vec3 p, ArmFoldContext context)
	{
		ArmSideContext? bestSide = null;
		var bestDist = double.MaxValue;
		foreach (var side in context.Sides)
		{
			var d = Math.Min(
				DistanceToSegment(p, side.Shoulder, side.Elbow),
				DistanceToSegment(p, side.Elbow, side.Hand));
			if (d < bestDist)
			{
				bestDist = d;
				bestSide = side;
			}
		}
		if (bestSide is null)
			return (null, 0f);

		var clavicleAbsX = Math.Abs(bestSide.Clavicle.X);
		var shoulderAbsX = Math.Abs(bestSide.Shoulder.X);
		var lat = Clamp01((Math.Abs(p.X) - clavicleAbsX) / Math.Max(shoulderAbsX + 0.08 - clavicleAbsX, 0.02));
		var armProx = Clamp01(1.0 - ((bestDist - 0.12) / 0.14));
		var zGate = Clamp01((p.Z - (bestSide.Hand.Z - 0.08)) / 0.12);
		var t = lat * armProx * zGate;
		var smooth = (float)(t * t * (3.0 - (2.0 * t)));
		return (bestSide, smooth);
	}

	private static double DistanceToSegment(Vec3 p, Vec3 a, Vec3 b)
	{
		var ab = b - a;
		var lenSq = Vec3.Dot(ab, ab);
		if (lenSq < 1e-12)
			return (p - a).Length;
		var t = Clamp01(Vec3.Dot(p - a, ab) / lenSq);
		return (p - (a + (ab * t))).Length;
	}

	private static double Clamp01(double value) => value < 0.0 ? 0.0 : value > 1.0 ? 1.0 : value;
}
