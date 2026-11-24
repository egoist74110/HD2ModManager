using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
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
			Func<string, uint> hash, IasPhysicsMigration? physics = null, bool alignArms = true)
	{
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
		var resolved = sourceBones.Where(bone => target.Find(bone.DisplayName) is not null)
			.ToDictionary(bone => bone.Index, bone => target.Find(bone.DisplayName)!.Index);
		physics?.Plan(sourceBones, target, resolved);
		var armAlignment = alignArms ? new IasArmBindAlignment(profile, sourceBones, target, physics) : null;
		var materials = new List<ulong>();
		var meshes = new Dictionary<int, RetargetedMesh>();
		foreach (var lod in model.RawMeshData.Where(mesh => mesh.LodIndex >= 0 &&
			mesh.Sections.Any(section => section.Triangles.Count > 0)).GroupBy(mesh => mesh.LodIndex))
		{
			var sourceVertices = new List<SourceVertex>();
			var sourceWeights = new List<SourceVertexWeight[]>();
			var rawWeights = physics is null && armAlignment is null ? null : new List<IReadOnlyList<(int Slot, float Weight)>>();
			var surfaceComponents = new List<UnitVertexComponentValue[]>();
			var buckets = new Dictionary<int, List<(int A, int B, int C)>>();
			foreach (var mesh in lod)
			{
				var visibleSections = mesh.Sections.Where(section => section.Triangles.Count > 0).ToArray();
				var materialBindings = CanonicalMaterialBindingResolver.Resolve(model, mesh, mesh);
				// SDK 的 LOD4 阴影代理可只有一个未绑定的默认槽位。
				if (mesh.LodIndex == 4 && materialBindings.IsValid && materialBindings.ResolvedSectionBindings.Count == 0)
					continue;
				if (!materialBindings.IsValid || materialBindings.ResolvedSectionBindings.Count != visibleSections.Length)
					throw new InvalidDataException($"Unbound IAS visual material in mesh {mesh.MeshInfoIndex}, LOD {mesh.LodIndex}: {string.Join("; ", materialBindings.Diagnostics)}");
				var meshInfo = FindUnique(model.Meshes, info => info.Index == mesh.MeshInfoIndex, "网格索引");
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
					rawWeights?.Add(weights);
					sourceWeights.Add(FoldVertexWeights(weights, byPaletteSlot, semanticBySlot, profile));
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
			var positions = sourceVertices.Select(vertex => vertex.Position).ToArray();
			var group = new MeshGroup("IAS", MeshGroupKind.BodyPart, default, 0, 1, [],
				Enumerable.Range(0, sourceVertices.Count).ToArray());
			var normals = RetargetedMeshBuilder.ComputeFinalNormals(group, geometry, positions, buckets);
			var output = new RetargetedVertex[sourceVertices.Count];
			for (var i = 0; i < output.Length; i++)
			{
				RetargetedInfluence NativeInfluence(SourceVertexWeight item)
				{
					var name = sourceBones[item.BoneIndex].DisplayName;
					if (!resolved.TryGetValue(item.BoneIndex, out var targetIndex) ||
						target.Bones[targetIndex].Name != name)
						throw new InvalidDataException($"Native avatar is missing weighted IAS semantic bone {name}.");
					return new RetargetedInfluence(avatar.NameHashes[targetIndex], item.Weight);
				}
				var influences = MergeInfluences(sourceWeights[i].Select(NativeInfluence));
				if (physics is not null && rawWeights is not null && rawWeights[i].Any(item => physics.WeightTarget(item.Slot).HasValue))
				{
					var remaining = rawWeights[i].Where(item => !physics.WeightTarget(item.Slot).HasValue).ToArray();
					var physical = rawWeights[i].Where(item => physics.WeightTarget(item.Slot).HasValue)
						.Select(item => new RetargetedInfluence(physics.WeightTarget(item.Slot)!.Value, item.Weight));
					influences = MergeInfluences(physical.Concat(FoldVertexWeights(remaining, byPaletteSlot,
						semanticBySlot, profile).Select(NativeInfluence)));
				}
				// 用折叠前的调色板权重，避免把自定义链的权重当作手臂骨的绑定差值。
				var (position, normal) = armAlignment?.Transform(positions[i], normals[i], rawWeights![i])
					?? (positions[i], normals[i]);
				output[i] = new(position, normal, sourceVertices[i].U, sourceVertices[i].V, influences)
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
		var info = FindUnique(model.BoneInfos, bone => bone.Index == mesh.LodIndex, "LOD 骨骼表");
		var remap = FindUnique(info.Remaps, item => item.MaterialIndex == material, "材质骨骼映射");
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
		FindUnique(vertex.Components, item => item.Type == type && item.Index == 0, "顶点流字段");

	private static T FindUnique<T>(IEnumerable<T> values, Func<T, bool> matches, string field) where T : class
	{
		T? result = null;
		foreach (var value in values)
		{
			if (!matches(value))
				continue;
			if (result is not null)
				throw new InvalidDataException($"IAS Unit 的{field}重复。");
			result = value;
		}
		return result ?? throw new InvalidDataException($"IAS Unit 缺少{field}。");
	}

	private static Vec3 Vector(IReadOnlyList<float> values) => new(values[0], values[1], values[2]);

	internal static SourceVertexWeight[] FoldVertexWeights(
		IReadOnlyList<(int Slot, float Weight)> weights,
		IReadOnlyDictionary<int, int> byPaletteSlot,
		IDictionary<int, int> semanticBySlot,
		IasRigProfile profile)
	{
		var resolved = new List<SourceVertexWeight>(weights.Count);
		foreach (var item in weights)
		{
			if (!byPaletteSlot.TryGetValue(item.Slot, out var nodeIndex))
				throw new InvalidDataException($"Weighted palette slot {item.Slot} is absent from the rig.");
			if (!semanticBySlot.TryGetValue(item.Slot, out var semantic))
			{
				semantic = profile.ResolveSemanticNode(nodeIndex);
				semanticBySlot.Add(item.Slot, semantic);
			}
			resolved.Add(new(semantic, item.Weight));
		}
		return resolved.GroupBy(item => item.BoneIndex)
			.Select(group => new SourceVertexWeight(group.Key, group.Sum(item => item.Weight))).ToArray();
	}
}
