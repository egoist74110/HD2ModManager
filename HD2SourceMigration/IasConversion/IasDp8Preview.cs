using System.Security.Cryptography;
using System.Text.Json;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;

namespace HD2SourceMigration.IasConversion;

public static class IasDp8Preview
{
	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	public static async Task ExportAsync(string modDirectory, string gameData, string outputDirectory)
	{
		var mod = Path.GetFullPath(modDirectory);
		var output = Path.GetFullPath(outputDirectory);
		if (!Directory.Exists(mod))
			throw new DirectoryNotFoundException(mod);

		var scanner = new PatchTocScanner();
		var patches = new PatchUnitMeshReader();
		var models = new List<object>();
		var resolver = new GameDataPackageResolver(gameData);
		var avatar = await new CanonicalAvatarRigReader(resolver).ReadTransformInfoAsync().ConfigureAwait(false);
		foreach (var folder in new[] { "", "coat/on", "coat/off", "shoes/on", "shoes/off" })
		{
			foreach (var archive in folder.Length == 0 ? new[] { IasDp8Routes.TargetBody, IasDp8Routes.TargetHelmet } : [IasDp8Routes.TargetBody])
			{
				var path = Path.Combine(mod, folder, archive + ".patch_0");
				var entries = await scanner.ScanEntriesAsync(path).ConfigureAwait(false);
				using var toc = File.OpenRead(path);
				var patchSha256 = Convert.ToHexString(await SHA256.HashDataAsync(toc).ConfigureAwait(false));
				foreach (var entry in entries)
				{
					if (entry.GpuResourceSize > 0 && entry.GpuResourceOffset % 64 != 0)
						throw new InvalidDataException($"Unaligned GPU entry in {path}.");
					var unit = await patches.ReadCanonicalSourceAsync(entry, entries).ConfigureAwait(false);
					var slot = IasDp8Routes.Slots.Single(item => item.Target == entry.AssetKey.FileId);
					var lod0 = unit.Model.RawMeshData.Where(mesh => mesh.LodIndex == 0).Select(mesh => ExportMesh(unit.Model, mesh, avatar)).ToArray();
					models.Add(new
					{
						slot.Part, slot.Body, Option = folder,
						FileId = slot.Target.ToString("x16"),
						Patch = Path.GetRelativePath(mod, path), patchSha256,
						TransformCount = unit.Model.TransformInfo.Matrices.Count,
						Meshes = lod0,
					});
				}
			}
		}

		Directory.CreateDirectory(output);
		var bones = avatar.Matrices.Select((matrix, index) => new { matrix.Values, Parent = avatar.Entries[index].ParentIndex, Hash = avatar.NameHashes[index] });
		await File.WriteAllTextAsync(Path.Combine(output, "patch-preview.json"),
			JsonSerializer.Serialize(new { Bones = bones, Units = models }, Json)).ConfigureAwait(false);

		var vanillaReader = new GameDataUnitMeshReader(resolver);
		var vanillaUnits = new List<object>();
		foreach (var slot in IasDp8Routes.Slots.Where(item => item.Body != "Stocky"))
		{
			var archive = slot.Helmet ? IasDp8Routes.TargetHelmet : IasDp8Routes.TargetBody;
			var key = new AssetKey(PatchUnitMeshReader.UnitTypeId, slot.Target);
			var donor = await vanillaReader.ReadAsync(archive, key).ConfigureAwait(false);
			var lod0 = donor.Model.RawMeshData
				.Where(mesh => mesh.LodIndex == 0
					&& mesh.Sections.Any(section => section.Triangles.Count > 0)
					&& CanonicalVertexMaterialResolver.Resolve(mesh).IsValid)
				.Select(mesh => ExportMesh(donor.Model, mesh, avatar, allowParentFallback: true))
				.ToArray();
			vanillaUnits.Add(new
			{
				slot.Part, slot.Body, Option = "",
				FileId = slot.Target.ToString("x16"),
				Patch = archive + ".vanilla", patchSha256 = "",
				TransformCount = donor.Model.TransformInfo.Matrices.Count,
				Meshes = lod0,
			});
		}
		await File.WriteAllTextAsync(Path.Combine(output, "vanilla-preview.json"),
			JsonSerializer.Serialize(new { Bones = bones, Units = vanillaUnits }, Json)).ConfigureAwait(false);
	}

	private static object ExportMesh(UnitMeshModel model, UnitRawMeshData mesh, UnitTransformInfo avatar, bool allowParentFallback = false)
	{
		var map = CanonicalVertexMaterialResolver.Resolve(mesh);
		if (!map.IsValid)
			throw new InvalidDataException(string.Join("; ", map.Diagnostics));
		var info = model.Meshes.Single(item => item.Index == mesh.MeshInfoIndex);
		var world = Hd2RigReader.FromHd2RowVector(model.TransformInfo.Matrices[(int)info.TransformIndex].Values);
		var normalWorld = world.Inverse().Transposed();
		var order = map.MaterialByVertex.Keys.ToArray();
		var compact = order.Select((index, next) => (index, next)).ToDictionary(item => item.index, item => item.next);
		var isHidden = order.Length <= 3;
		var positions = new double[order.Length][];
		var normals = new double[order.Length][];
		var uv = new float[order.Length][];
		var weights = new object[order.Length];
		var avatarHashes = avatar.NameHashes.ToHashSet();
		for (var i = 0; i < order.Length; i++)
		{
			var vertex = mesh.Vertices[(int)order[i]];
			var p = IasNativeMesh.Component(vertex, 0).FloatValues;
			var v = world.TransformPoint(new(p[0], p[1], p[2]));
			positions[i] = [v.X, v.Y, v.Z];
			if (isHidden)
				continue;
			var n = IasNativeMesh.Component(vertex, 1).FloatValues;
			var direction = (normalWorld.TransformPoint(new(n[0], n[1], n[2])) - normalWorld.Origin).Normalized();
			normals[i] = [direction.X, direction.Y, direction.Z];
			uv[i] = IasNativeMesh.Component(vertex, 4).FloatValues;
			var influences = IasNativeMesh.DecodeWeights(model, mesh, vertex, map.MaterialByVertex[order[i]]);
			var resolvedWeights = influences.Select(item =>
			{
				var slotIndex = item.Slot;
				var hash = model.TransformNameHashes[slotIndex];
				if (allowParentFallback)
				{
					while (!avatarHashes.Contains(hash))
					{
						var parent = model.TransformInfo.Entries[slotIndex].ParentIndex;
						if (parent == slotIndex || parent >= model.TransformInfo.Entries.Count)
							break;
						slotIndex = parent;
						hash = model.TransformNameHashes[slotIndex];
					}
				}
				if (!avatarHashes.Contains(hash))
					throw new InvalidDataException($"Patch vertex references non-avatar transform {hash:x8}.");
				return (Hash: hash, item.Weight);
			}).GroupBy(item => item.Hash).Select(group => new { Hash = group.Key, Weight = group.Sum(item => item.Weight) }).ToArray();
			var sum = resolvedWeights.Sum(item => item.Weight);
			if (sum <= 0f || (!allowParentFallback && Math.Abs(sum - 1f) > 0.002f))
				throw new InvalidDataException($"Encoded skin weights sum to {sum} at vertex {vertex.Index}.");
			weights[i] = allowParentFallback && Math.Abs(sum - 1f) > 0.0001f
				? resolvedWeights.Select(item => new { item.Hash, Weight = item.Weight / sum }).ToArray()
				: resolvedWeights;
		}
		var sections = mesh.Sections.Select(section => new
		{
			Material = model.Materials.FirstOrDefault(binding => binding.SectionId == section.MaterialSlotId)?.MaterialId.ToString("x16"),
			Faces = section.Triangles.Select(face => new[] { compact[face.A], compact[face.B], compact[face.C] }),
		});
		return new { MeshIndex = mesh.MeshInfoIndex, isHidden, positions, normals, uv, weights, sections };
	}

}
