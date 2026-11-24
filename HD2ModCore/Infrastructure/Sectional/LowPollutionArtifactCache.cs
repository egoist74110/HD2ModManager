using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure.KitInstance;
using AdaptAssetKey = HD2ModAdaptation.PatchReconstruction.AssetKey;
using AdaptPackageResolver = HD2ModAdaptation.PatchReconstruction.GameDataPackageResolver;
using AdaptPackageToc = HD2ModAdaptation.PatchReconstruction.GameDataPackageToc;

namespace HD2ModCore.Infrastructure.Sectional;

internal static class LowPollutionArtifactCache
{
	internal const string DirectoryName = ".hd2mod-low-pollution-cache";
	internal const string StampName = ".hd2mod-own-model-artifact.json";
	private const string OwnerName = ".hd2mod-generated-cache";
	private const int FormatVersion = 1;
	private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

	internal sealed record Inputs(string Source, string Components, bool HeadMerge, uint[] Npcs, string Game, string Materials)
	{
		internal string GeometryKey => Digest(new { Source, Components, HeadMerge, Npcs, Game });

		internal bool SameSharedInputs(Inputs other)
			=> Source == other.Source && Components == other.Components && HeadMerge == other.HeadMerge && Materials == other.Materials;
	}

	internal sealed record Stamp(int Format, int Planner, bool LowPollution, bool Compressed, Inputs Inputs, string Output);

	internal static SemaphoreSlim Gate(string modDirectory)
		=> Gates.GetOrAdd(Path.TrimEndingDirectorySeparator(Path.GetFullPath(modDirectory)), static _ => new SemaphoreSlim(1, 1));

	internal static string Slot(string modDirectory, bool lowPollution)
		=> Path.Combine(modDirectory, DirectoryName, lowPollution ? "compressed" : "ordinary");

	internal static bool OwnsSlot(string slot)
	{
		var root = Path.GetDirectoryName(slot)!;
		return File.Exists(Path.Combine(root, OwnerName))
			&& (File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0;
	}

	internal static bool HasCompressedOutput(string overwrite)
		=> File.Exists(Path.Combine(overwrite, LowPollutionLayoutFile.FileName)) || Read(overwrite) is { Compressed: true };

	internal static bool Participates(string modDirectory)
	{
		var active = ModNodePayloadLayout.GetOverwriteDirectory(modDirectory);
		return File.Exists(Path.Combine(active, StampName)) || HasCompressedOutput(active)
			|| File.Exists(Path.Combine(active, LowPollutionFreshness.UnsupportedMarkerFileName))
			|| Directory.Exists(Path.Combine(modDirectory, DirectoryName));
	}

	internal static async ValueTask<Inputs?> CaptureAsync(
		string modDirectory, string payload, string sourcePatch, WardrobeSectionMapDocument sourceMap,
		IReadOnlyDictionary<string, IReadOnlyCollection<int>>? disabled, bool headMerge,
		bool lowPollution, VpkHostContext context, OverwriteSelection selection, CancellationToken cancellationToken)
	{
		try
		{
			var structure = new { sourceMap.Schema, sourceMap.SectionRecordSize, sourceMap.NumIndicesOffsetInSection, sourceMap.Offsets, sourceMap.Units };
			var source = Digest(new
			{
				Structure = structure,
				Unit = PatchDigest(sourcePatch, cancellationToken),
				Components = FileDigest(Path.Combine(payload, SectionGroupFile.FileName), cancellationToken),
				Registry = FileDigest(Path.Combine(payload, VpkCustomModelFile.FileName), cancellationToken)
			});
			var components = ComponentKey(disabled);
			uint[] npcs = [];
			var game = string.Empty;
			if (lowPollution)
			{
				var plan = SectionalCompressionPlanner.Plan(
					[.. sourceMap.Units.Select(unit => new SectionalCompressionSource(unit.FileId, ArmorStructure.TryParse(unit.Slot), unit.BodyType))],
					headMerge);
				var hasNpcCarrier = plan.Supported && headMerge && plan.Groups.Any(group =>
					group.Role is SectionalCompressionRole.BodyArms or SectionalCompressionRole.HeadBody);
				if (hasNpcCarrier)
					npcs = KitNpcRecords.All.Where(npc => context.NpcItemIds.Contains(npc.ItemId)).Select(npc => npc.ItemId).Order().ToArray();
				var needsAvatar = plan.Supported && plan.NeedsMerge;
				if (needsAvatar || npcs.Contains(KitNpcRecords.SeafSoldierItemId))
					game = await GameKeyAsync(context, needsAvatar, npcs, cancellationToken).ConfigureAwait(false);
			}
			return new Inputs(source, components, headMerge, npcs, game, MaterialsKey(modDirectory, selection, cancellationToken));
		}
		catch (Exception exception) when (IsCacheReadFailure(exception))
		{
			// 游戏文件或来源读不全时，不允许用已缓存的产物冒充当前版本。
			return null;
		}
	}

	internal static string ComponentKey(IReadOnlyDictionary<string, IReadOnlyCollection<int>>? disabled)
		=> Digest((disabled ?? new Dictionary<string, IReadOnlyCollection<int>>())
			.Where(pair => pair.Value.Count != 0)
			.OrderBy(pair => pair.Key, StringComparer.Ordinal)
			.Select(pair => new { Unit = pair.Key, Sections = pair.Value.Distinct().Order() }));

	internal static Stamp? Read(string directory)
	{
		try
		{
			var path = Path.Combine(directory, StampName);
			if (!File.Exists(path) || !ManagedOverwriteDirectory.IsManaged(directory)) return null;
			if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return null;
			var stamp = JsonSerializer.Deserialize<Stamp>(File.ReadAllText(path));
			return stamp is { Format: FormatVersion, Planner: LowPollutionFreshness.PlannerVersion, Inputs.Npcs: not null } ? stamp : null;
		}
		catch (Exception exception) when (IsCacheReadFailure(exception))
		{
			return null;
		}
	}

	internal static bool Matches(string directory, bool lowPollution, Inputs? inputs, CancellationToken cancellationToken)
		=> Mismatch(directory, lowPollution, inputs, cancellationToken) is null;

	internal static string? Mismatch(string directory, bool lowPollution, Inputs? inputs, CancellationToken cancellationToken)
	{
		var stamp = Read(directory);
		if (stamp is null) return "生效层没有当前格式的缓存指纹";
		if (stamp.LowPollution != lowPollution) return lowPollution ? "需要切换到压缩形态" : "关闭低污染模式，恢复普通形态";
		if (inputs is null) return "无法读取来源或游戏输入";
		if (stamp.Inputs.Source != inputs.Source) return "来源结构、patch 或侧车已有变化";
		if (stamp.Inputs.Components != inputs.Components) return "组件勾选已有变化";
		if (stamp.Inputs.HeadMerge != inputs.HeadMerge) return "头身合并设置已有变化";
		if (!stamp.Inputs.Npcs.SequenceEqual(inputs.Npcs)) return "NPC 映射集合已有变化";
		if (stamp.Inputs.Game != inputs.Game) return "游戏玩家骨架或原版 Unit 已有变化";
		if (stamp.Inputs.Materials != inputs.Materials) return "材质输入已有变化";
		return HasIntactOutput(directory, stamp, cancellationToken) ? null : "产物文件有变化或缺失";
	}

	internal static void Seal(string directory, bool lowPollution, Inputs inputs, CancellationToken cancellationToken)
		=> Write(directory, new Stamp(FormatVersion, LowPollutionFreshness.PlannerVersion, lowPollution,
			HasCompressedOutput(directory), inputs, OutputKey(directory, cancellationToken)));

	internal static void RefreshMaterials(string modDirectory, string current, string prepared, IReadOnlyList<string> materials)
	{
		var materialsKey = MaterialsKey(materials, default);
		if (Read(current) is { } stamp && HasIntactOutput(current, stamp, default))
			Write(prepared, stamp with { Inputs = stamp.Inputs with { Materials = materialsKey }, Output = OutputKey(prepared, default) });

		// 显式材质选择可以先于设置落盘，所以物化时直接清掉材质不同的候补层。
		foreach (var mode in new[] { false, true })
		{
			var slot = Slot(modDirectory, mode);
			if (Read(slot) is { } cached && cached.Inputs.Materials != materialsKey)
				DiscardSlot(slot);
		}
	}

	internal static void Publish(string modDirectory, string prepared, bool lowPollution, Inputs? inputs, CancellationToken cancellationToken)
	{
		var active = ModNodePayloadLayout.GetOverwriteDirectory(modDirectory);
		var oppositeSlot = Slot(modDirectory, !lowPollution);
		var outgoing = Read(active);
		string? retain = null;
		if (inputs is not null && outgoing is not null && outgoing.LowPollution != lowPollution
			&& outgoing.Inputs.SameSharedInputs(inputs)
			&& HasIntactOutput(active, outgoing, cancellationToken))
		{
			retain = oppositeSlot;
			EnsureCacheRoot(modDirectory);
		}
		if (retain is not null || (Read(oppositeSlot) is { } opposite
			&& (inputs is null || !opposite.Inputs.SameSharedInputs(inputs))))
			DiscardSlot(oppositeSlot);
		var incomingSlot = Slot(modDirectory, lowPollution);
		if (!string.Equals(prepared, incomingSlot, StringComparison.OrdinalIgnoreCase))
			DiscardSlot(incomingSlot);
		cancellationToken.ThrowIfCancellationRequested();
		ManagedOverwriteDirectory.ReplacePrepared(active, prepared, retain, backupContainsCopies: outgoing is not null);
	}

	private static void EnsureCacheRoot(string modDirectory)
	{
		var root = Path.Combine(modDirectory, DirectoryName);
		var owner = Path.Combine(root, OwnerName);
		if (Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
			throw new InvalidOperationException("低污染缓存根目录是重解析点，已拒绝覆盖。");
		if (File.Exists(owner)) return;
		if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
			throw new InvalidOperationException("低污染缓存根目录缺少托管标记，已拒绝覆盖。");
		Directory.CreateDirectory(root);
		File.WriteAllText(owner, "generated");
	}

	private static void DiscardSlot(string slot)
	{
		if (!Directory.Exists(slot)) return;
		if (!OwnsSlot(slot))
			throw new InvalidOperationException("低污染缓存目录缺少托管标记，已拒绝覆盖。");
		var discarded = slot + ".backup-" + Guid.NewGuid().ToString("N");
		Directory.Move(slot, discarded);
		ManagedOverwriteDirectory.DiscardPrepared(discarded);
	}

	private static bool HasIntactOutput(string directory, Stamp stamp, CancellationToken cancellationToken)
	{
		try
		{
			return !Directory.EnumerateDirectories(directory).Any()
				&& !Directory.EnumerateFiles(directory).Any(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
				&& stamp.Output == OutputKey(directory, cancellationToken);
		}
		catch (Exception exception) when (IsCacheReadFailure(exception)) { return false; }
	}

	private static void Write(string directory, Stamp stamp)
		=> File.WriteAllText(Path.Combine(directory, StampName), JsonSerializer.Serialize(stamp));

	private static string OutputKey(string directory, CancellationToken cancellationToken)
		=> Digest(Directory.EnumerateFiles(directory).Where(path => Path.GetFileName(path) != StampName)
			.OrderBy(path => path, StringComparer.Ordinal)
			.Select(path => new { Name = Path.GetFileName(path), Hash = FileDigest(path, cancellationToken) }));

	private static string MaterialsKey(string modDirectory, OverwriteSelection selection, CancellationToken cancellationToken)
		=> MaterialsKey(OverwriteMaterializer.ResolveMaterialLayers(modDirectory, selection.EnabledMaterialPatchFileNames).Paths, cancellationToken);

	private static string MaterialsKey(IReadOnlyList<string> paths, CancellationToken cancellationToken)
		=> Digest(paths.Select(path => PatchDigest(path, cancellationToken)));

	private static string PatchDigest(string path, CancellationToken cancellationToken)
		=> Digest(new
		{
			Name = Path.GetFileName(path),
			Hashes = OverwritePatchInstaller.SidecarSuffixes.Select(suffix => FileDigest(path + suffix, cancellationToken))
		});

	private static string? FileDigest(string path, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!File.Exists(path)) return null;
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
		return Convert.ToHexString(SHA256.HashData(stream));
	}

	private static string Digest<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

	internal static bool IsCacheReadFailure(Exception exception)
		=> exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
			or InvalidOperationException or KeyNotFoundException or FormatException or System.Security.SecurityException;

	private static async ValueTask<string> GameKeyAsync(VpkHostContext context, bool needsAvatar, uint[] npcs, CancellationToken cancellationToken)
	{
		var useTestMerger = context.MergeUnitOverride is not null;
		var native = KitNpcRecords.All.FirstOrDefault(npc => npc.RequiresNativeShell && npcs.Contains(npc.ItemId));
		if ((needsAvatar && !useTestMerger || native is not null) && string.IsNullOrWhiteSpace(context.GameDataFolder))
			throw new InvalidDataException("缺少游戏数据目录。");

		var resolver = new GameInputRecorder(new AdaptPackageResolver(context.GameDataFolder ?? context.Paths.AppRootDirectory));
		if (needsAvatar && !useTestMerger)
			await new CanonicalAvatarRigReader(resolver).ReadTransformInfoAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
		if (native is not null)
		{
			var shell = await new GameDataUnitMeshReader(resolver).ReadAsync(native.RootArchiveId,
				new AdaptAssetKey(PatchUnitMeshReader.UnitTypeId, native.RootUnitId), cancellationToken: cancellationToken).ConfigureAwait(false);
			var entry = shell.Payload.Entry;
			resolver.Parts.Add(Digest(new { entry.Unknown1, entry.Unknown2, entry.Unknown3, entry.Unknown4 }));
		}
		return Digest(new { TestMerger = needsAvatar && useTestMerger, resolver.Parts });
	}

	// 使用编译器相同的读取器，记录实际读取的 Unit 与依赖字节，兼容散包、DSAR 与 bundle。
	private sealed class GameInputRecorder(IGameDataPackageResolver resolver) : IGameDataPackageResolver
	{
		internal List<string> Parts { get; } = [];

		public ValueTask<AdaptPackageToc?> GetPackageTocAsync(string packageName, CancellationToken cancellationToken = default)
			=> resolver.GetPackageTocAsync(packageName, cancellationToken);

		public ValueTask<IReadOnlyList<string>> GetPackageNamesAsync(CancellationToken cancellationToken = default)
			=> resolver.GetPackageNamesAsync(cancellationToken);

		public async ValueTask<byte[]?> GetPackageResourceAsync(string packageName, ulong offset, uint size, CancellationToken cancellationToken = default)
		{
			var bytes = await resolver.GetPackageResourceAsync(packageName, offset, size, cancellationToken).ConfigureAwait(false);
			Parts.Add(Digest(new { packageName, size, Hash = bytes is null ? null : Convert.ToHexString(SHA256.HashData(bytes)) }));
			return bytes;
		}
	}
}
