using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;

namespace HD2SourceMigration.IasPackaging.BodyRetarget;

public sealed record IasBodyFit(string? OriginalArchive, string? Author, string[] Archives,
	IReadOnlyDictionary<string, string?> Fits, string? Error);

public sealed record IasBodyRetargetOutput(string MainZip, string CompanionZip, string[] Archives, string? Author,
	string BodyPatch, string[] BodyKeys);

public sealed class IasBodyRetargetService(string version, string generator, string sdk, string metadata, string gameData)
{
	private const ulong Unit = PatchUnitMeshReader.UnitTypeId;

	private sealed record Donor(string Unit, string Archive, string Sha, int Nodes, string? Point);
	private sealed record Plan(string? OriginalArchive, string? Author, string[]? Archives, Dictionary<string, string?>? Fits,
		string? Error, string? Patch, string[]? OldPoints, Dictionary<string, Donor[]>? Stock, Donor[]? Donors,
		Donor[]? SourceStock = null);

	public async Task<IasBodyFit> AnalyzeAsync(string source, IReadOnlyList<string> targets, string work, CancellationToken ct)
	{
		var plan = await ReadPlanAsync(source, targets, null, work, ct).ConfigureAwait(false);
		var fits = plan.Fits ?? new(StringComparer.OrdinalIgnoreCase);
		if (plan.Error is null)
		{
			var stock = new StockReader(gameData);
			try
			{
				await ReadBodyAsync(source, plan, ct).ConfigureAwait(false);
				await stock.ValidateAsync(plan.SourceStock!, ct).ConfigureAwait(false);
				await stock.ValidateClosureAsync(source, plan.Archives!, [], ct).ConfigureAwait(false);
			}
			catch (Exception error) when (Expected(error))
			{
				return new(plan.OriginalArchive, plan.Author, plan.Archives ?? [], fits, error.Message);
			}
			foreach (var (target, donors) in plan.Stock ?? [])
			{
				try { await stock.ValidateAsync(donors, ct).ConfigureAwait(false); }
				catch (Exception error) when (Expected(error)) { fits[target] = error.Message; }
			}
		}
		return new(plan.OriginalArchive, plan.Author, plan.Archives ?? [], fits, plan.Error);
	}

	public async Task<IasBodyRetargetOutput> RetargetAsync(string source, string target, string work, CancellationToken ct)
	{
		var plan = await ReadPlanAsync(source, [target], target, work, ct).ConfigureAwait(false);
		if (plan.Error is not null) throw new InvalidDataException(plan.Error);
		if (plan.Fits?.GetValueOrDefault(target) is { } reason) throw new InvalidDataException(reason);
		if (plan.Donors is null || plan.OriginalArchive is null || plan.Archives is null)
			throw new InvalidDataException("目标应为不同的身体护甲。");

		var (patch, original, oldPoints) = await ReadBodyAsync(source, plan, ct).ConfigureAwait(false);
		var stock = new StockReader(gameData);
		await stock.ValidateAsync(plan.SourceStock!, ct).ConfigureAwait(false);
		await stock.ValidateAsync(plan.Donors, ct).ConfigureAwait(false);
		var reader = new GameDataUnitMeshReader(stock.Resolver);
		var additions = new List<PatchArchiveAdditionalEntry>();
		foreach (var donor in plan.Donors.Where(row => row.Point is not null))
		{
			var model = await reader.ReadAsync(donor.Archive, new(Unit, Hex(donor.Unit)),
				allowGlobalDependencySearch: true, cancellationToken: ct).ConfigureAwait(false);
			var point = IasAssets.BuildIasPointUnit(model.Model, model.Payload.TocData);
			RequirePoint(point.TocData, point.GpuData, donor.Unit);
			additions.Add(new(new(Unit, Hex(donor.Point!)), point.TocData, [], point.GpuData));
		}
		await stock.ValidateClosureAsync(source, [target, .. plan.Archives], additions, ct).ConfigureAwait(false);
		var authored = original.Where(entry => !oldPoints.Contains(entry)).ToArray();
		var changed = await new PatchArchiveWriter().WriteAsync(patch, Path.Combine(work, "patch"), [], additions,
			oldPoints, preserveOriginalStream: false, cancellationToken: ct).ConfigureAwait(false);
		await VerifyPatchAsync(changed.TocFilePath, authored, additions, ct).ConfigureAwait(false);

		var name = target + Path.GetExtension(patch);
		var checkedPatch = Path.Combine(work, "patch", name);
		File.Move(changed.TocFilePath, checkedPatch);
		MoveSidecar(changed.StreamFilePath, checkedPatch + ".stream");
		MoveSidecar(changed.GpuResourceFilePath, checkedPatch + ".gpu_resources");
		var main = Path.Combine(source, "main");
		var output = Path.Combine(work, "IAS_MainMod.zip");
		var code = Path.GetRelativePath(main, patch);
		var excluded = new HashSet<string>([code, code + ".stream", code + ".gpu_resources", "manifest.json", ".hd2modmanager-mod.json"],
			StringComparer.OrdinalIgnoreCase);
		var files = Directory.EnumerateFiles(main, "*", new EnumerationOptions
		{
			RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint
		}).Select(path => (Name: Path.GetRelativePath(main, path), Path: path))
			.Where(file => !excluded.Contains(file.Name)).ToList();
		var newName = Path.Combine(Path.GetDirectoryName(code)!, name);
		files.AddRange([(newName, checkedPatch), (newName + ".stream", checkedPatch + ".stream"),
			(newName + ".gpu_resources", checkedPatch + ".gpu_resources"), ("manifest.json", Path.Combine(work, "manifest.json"))]);
		if (files.Select(file => file.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
			throw new InvalidDataException("新旧主包路径重叠。");
		await using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
		using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
			foreach (var file in files.OrderBy(file => file.Name, StringComparer.Ordinal))
			{
				var entry = zip.CreateEntry(file.Name.Replace('\\', '/'), CompressionLevel.Fastest);
				entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
				await using var data = File.OpenRead(file.Path);
				await using var packed = entry.Open();
				await data.CopyToAsync(packed, ct).ConfigureAwait(false);
			}
		return new(output, Path.Combine(work, "IAS_Companion.hd2irpack.zip"),
			plan.Archives.Where(archive => archive != plan.OriginalArchive).Append(target).Order(StringComparer.Ordinal).ToArray(),
			plan.Author, checkedPatch, authored.Select(entry => entry.AssetKey).Concat(additions.Select(entry => entry.AssetKey))
				.Select(key => $"{key.TypeId:x16}/{key.FileId:x16}").ToArray());

		static void MoveSidecar(string path, string destination)
		{
			if (File.Exists(path)) File.Move(path, destination);
			else File.WriteAllBytes(destination, []);
		}
	}

	private static async Task<(string Patch, IReadOnlyList<PatchTocEntry> Entries, PatchTocEntry[] Points)> ReadBodyAsync(
		string source, Plan plan, CancellationToken ct)
	{
		var path = Path.Combine(source, "main", plan.Patch!);
		var entries = await new PatchTocScanner().ScanEntriesAsync(path, ct).ConfigureAwait(false);
		if (entries.Any(entry => entry.AssetKey.TypeId != Unit || entry.UsesEmptyStreamCompatibilityOverride))
			throw new InvalidDataException("身体 patch 不是受支持的隔离 Unit 格式。");
		var remove = plan.OldPoints!.Select(Hex).ToHashSet();
		var points = entries.Where(entry => remove.Contains(entry.AssetKey.FileId)).ToArray();
		if (points.Length != remove.Count) throw new InvalidDataException("原隐藏 Unit 闭包不符。");
		var reader = new PatchEntryPayloadReader();
		foreach (var entry in entries)
		{
			var data = await reader.ReadPayloadAsync(entry, ct).ConfigureAwait(false);
			if (data.TocData.Length < 24 || BinaryPrimitives.ReadUInt64LittleEndian(data.TocData.AsSpan(16)) != 0)
				throw new InvalidDataException("身体 Unit 引用了不支持的 Composite。");
			if (remove.Contains(entry.AssetKey.FileId))
			{
				if (data.StreamData.Length != 0) throw new InvalidDataException("原隐藏 Unit 带有未知外部载荷。");
				RequirePoint(data.TocData, data.GpuResourceData, $"{entry.AssetKey.FileId:x16}");
			}
		}
		return (path, entries, points);
	}

	private static void RequirePoint(byte[] toc, byte[] gpu, string id)
	{
		var model = new UnitMeshReader().Read(toc, gpu);
		if (model.RawMeshData.Count == 0 || model.RawMeshData.Count != model.Meshes.Count || model.RawMeshData.Any(mesh =>
			mesh.Vertices.Count != 1 || mesh.Triangles.Any(t => t.A != t.B || t.A != t.C)))
			throw new InvalidDataException($"无法证明 Unit {id} 是清理点网格。");
	}

	private static async Task VerifyPatchAsync(string patch, PatchTocEntry[] authored,
		IReadOnlyList<PatchArchiveAdditionalEntry> additions, CancellationToken ct)
	{
		var entries = await new PatchTocScanner().ScanEntriesAsync(patch, ct).ConfigureAwait(false);
		if (entries.Count != authored.Length + additions.Count || entries.Where((entry, index) =>
			entry.EntryIndex != index + 1 || entry.GpuResourceSize != 0 && entry.GpuResourceOffset % 64 != 0).Any())
			throw new InvalidDataException("身体 patch 条目序号或 GPU 对齐回读不符。");
		var toc = await File.ReadAllBytesAsync(patch, ct).ConfigureAwait(false);
		if (BinaryPrimitives.ReadUInt32LittleEndian(toc.AsSpan(4)) != 1 ||
			BinaryPrimitives.ReadUInt64LittleEndian(toc.AsSpan(80)) != Unit ||
			BinaryPrimitives.ReadUInt64LittleEndian(toc.AsSpan(88)) != (ulong)entries.Count)
			throw new InvalidDataException("身体 patch 类型表计数回读不符。");
		var keyed = entries.ToDictionary(entry => entry.AssetKey);
		var reader = new PatchEntryPayloadReader();
		foreach (var entry in authored)
		{
			var before = await reader.ReadPayloadAsync(entry, ct).ConfigureAwait(false);
			await CompareAsync(entry.AssetKey, before.TocData, before.GpuResourceData, before.StreamData).ConfigureAwait(false);
		}
		foreach (var entry in additions)
			await CompareAsync(entry.AssetKey, entry.TocData, entry.GpuResourceData, entry.StreamData).ConfigureAwait(false);

		async Task CompareAsync(AssetKey key, byte[] tocData, byte[] gpu, byte[] stream)
		{
			if (!keyed.TryGetValue(key, out var entry)) throw new InvalidDataException("身体 patch 回读缺少 Unit。");
			var after = await reader.ReadPayloadAsync(entry, ct).ConfigureAwait(false);
			if (!tocData.AsSpan().SequenceEqual(after.TocData) || !gpu.AsSpan().SequenceEqual(after.GpuResourceData) ||
				!stream.AsSpan().SequenceEqual(after.StreamData))
				throw new InvalidDataException($"Unit {key.FileId:x16} 的 TOC、GPU 或 stream 回读不符。");
		}
	}

	private async Task<Plan> ReadPlanAsync(string source, IReadOnlyList<string> targets, string? build, string work, CancellationToken ct)
	{
		if (version != "1.6.4") return new(null, null, null, null, $"IAS {version} 的配套格式尚未验证。", null, null, null, null);
		var script = Path.Combine(AppContext.BaseDirectory, "data", "resources", "ias", "ias_retarget.py");
		var python = Path.Combine(generator, "python", "python.exe");
		if (!File.Exists(script) || !File.Exists(python))
			return new(null, null, null, null, "缺少 IAS Generator Python 或换护甲脚本。", null, null, null, null);
		Directory.CreateDirectory(work);
		var request = Path.Combine(work, "request.json");
		await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new
		{
			Source = source, Sdk = sdk, Metadata = metadata, Generator = generator, Targets = targets, Build = build
		}), ct).ConfigureAwait(false);
		var start = new ProcessStartInfo(python)
		{
			WorkingDirectory = work, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
			RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
		};
		foreach (var name in new[] { "TEMP", "TMP", "APPDATA", "LOCALAPPDATA" }) start.Environment[name] = work;
		foreach (var arg in new[] { "-B", "-X", "utf8", script, request }) start.ArgumentList.Add(arg);
		using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 IAS Generator Python。");
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
		timeout.CancelAfter(TimeSpan.FromMinutes(2));
		var stdout = process.StandardOutput.ReadToEndAsync();
		var stderr = process.StandardError.ReadToEndAsync();
		try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
		catch (OperationCanceledException)
		{
			try { process.Kill(entireProcessTree: true); }
			catch (InvalidOperationException) when (process.HasExited) { }
			await process.WaitForExitAsync().ConfigureAwait(false);
			await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
			ct.ThrowIfCancellationRequested();
			throw new InvalidDataException("IAS 配套分析超时。");
		}
		await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
		if (process.ExitCode != 0) throw new InvalidDataException($"IAS 配套分析失败（{process.ExitCode}）。{await stderr.ConfigureAwait(false)}");
		return JsonSerializer.Deserialize<Plan>(await File.ReadAllTextAsync(Path.Combine(work, "result.json"), ct).ConfigureAwait(false))
			?? throw new InvalidDataException("IAS 配套分析未返回结果。");
	}

	private sealed class StockReader(string gameData)
	{
		private const ulong Material = MaterialDependencyResolver.MaterialTypeId;
		private const ulong Texture = MaterialDependencyResolver.TextureTypeId;
		internal GameDataPackageResolver Resolver { get; } = new(gameData);
		private readonly Dictionary<string, Dictionary<AssetKey, PatchTocEntry>> _archives = new(StringComparer.Ordinal);
		private readonly Dictionary<(string Archive, string Unit), (string Hash, uint Nodes)> _units = new();

		internal async Task ValidateAsync(IEnumerable<Donor> donors, CancellationToken ct)
		{
			foreach (var donor in donors)
			{
				if (!_units.TryGetValue((donor.Archive, donor.Unit), out var verified))
				{
					if (!_archives.TryGetValue(donor.Archive, out var entries))
					{
						var archive = await Resolver.GetPackageTocAsync(donor.Archive, ct).ConfigureAwait(false)
							?? throw new InvalidDataException($"当前游戏缺少原版 Archive {donor.Archive}。");
						entries = new PatchTocScanner().ScanEntries(archive.Data, donor.Archive, archive.UsesSlimEntryOffset)
							.Where(entry => entry.AssetKey.TypeId == Unit).ToDictionary(entry => entry.AssetKey);
						_archives.Add(donor.Archive, entries);
					}
					if (!entries.TryGetValue(new(Unit, Hex(donor.Unit)), out var entry))
						throw new InvalidDataException($"当前游戏缺少原版 Unit {donor.Unit}。");
					var bytes = await Resolver.GetPackageResourceAsync(donor.Archive, entry.TocDataOffset, entry.TocDataSize, ct)
						.ConfigureAwait(false);
					if (bytes is null || bytes.Length < 56) throw new InvalidDataException($"原版 Unit {donor.Unit} 的载荷无效。");
					var offset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x34));
					if (offset < 0x38 || offset > bytes.Length - 16)
						throw new InvalidDataException($"原版 Unit {donor.Unit} 缺少节点表。");
					verified = (Convert.ToHexString(SHA256.HashData(bytes)),
						BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)offset)));
					_units.Add((donor.Archive, donor.Unit), verified);
				}
				if (verified.Nodes != donor.Nodes || !verified.Hash.Equals(donor.Sha, StringComparison.OrdinalIgnoreCase))
					throw new InvalidDataException($"当前游戏 build 的原版 Unit {donor.Unit} 与 IAS 节点数或 TOC SHA 不符。");
			}
		}

		internal async Task ValidateClosureAsync(string source, IReadOnlyList<string> archives,
			IReadOnlyList<PatchArchiveAdditionalEntry> points, CancellationToken ct)
		{
			var scanner = new PatchTocScanner();
			var entries = new Dictionary<AssetKey, PatchTocEntry>();
			var refs = new HashSet<AssetKey>();
			foreach (var path in Directory.EnumerateFiles(Path.Combine(source, "main"), "*", new EnumerationOptions
			{
				RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint
			}).Where(path => Regex.IsMatch(Path.GetFileName(path), "^[0-9a-f]{16}\\.patch_[0-9]+$", RegexOptions.CultureInvariant)))
			{
				var toc = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
				foreach (var entry in scanner.ScanEntries(toc, path, usesSlimEntryOffset: true))
				{
					if (!entries.TryAdd(entry.AssetKey, entry))
						throw new InvalidDataException($"两件套有重叠资源键 {entry.AssetKey.FileId:x16}，无法确认闭包。");
					AddReferences(entry.AssetKey.TypeId, toc.AsSpan(checked((int)entry.TocDataOffset), checked((int)entry.TocDataSize)), refs);
				}
			}
			foreach (var point in points) AddReferences(Unit, point.TocData, refs);
			refs.ExceptWith(entries.Keys);
			if (refs.Count == 0) return;

			var preferred = archives.Append("9ba626afa44a3aa3").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
			var candidates = preferred.Concat((await Resolver.GetPackageNamesAsync(ct).ConfigureAwait(false))
				.Where(name => name.Length == 16)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
			var materialTextures = new HashSet<AssetKey>();
			await FindAsync(refs, materialTextures).ConfigureAwait(false);
			materialTextures.ExceptWith(entries.Keys);
			await FindAsync(materialTextures, []).ConfigureAwait(false);

			async Task FindAsync(HashSet<AssetKey> pending, HashSet<AssetKey> more)
			{
				foreach (var archive in candidates)
				{
					if (pending.Count == 0) return;
					ct.ThrowIfCancellationRequested();
					GameDataPackageToc? stock;
					try { stock = await Resolver.GetPackageTocAsync(archive, ct).ConfigureAwait(false); }
					catch (Exception error) when (Expected(error) && !preferred.Contains(archive)) { continue; }
					if (stock is null) continue;
					foreach (var entry in scanner.ScanEntries(stock.Data, archive, stock.UsesSlimEntryOffset))
					{
						if (!pending.Contains(entry.AssetKey)) continue;
						var bytes = await Resolver.GetPackageResourceAsync(archive, entry.TocDataOffset, entry.TocDataSize, ct)
							.ConfigureAwait(false);
						if (bytes is not { Length: > 0 }) continue;
						pending.Remove(entry.AssetKey);
						if (entry.AssetKey.TypeId == Material) AddReferences(Material, bytes, more);
					}
				}
				if (pending.Count != 0)
					throw new InvalidDataException($"游戏和两件套缺少资源闭包：{string.Join("、", pending.Take(3).Select(key => $"{key.TypeId:x16}/{key.FileId:x16}"))}。");
			}
		}

		private static void AddReferences(ulong type, ReadOnlySpan<byte> bytes, HashSet<AssetKey> refs)
		{
			if (type == Material)
			{
				foreach (var id in new StingrayMaterialReferenceReader().ReadTextureIds(bytes))
					if (id != 0) refs.Add(new(Texture, id));
				return;
			}
			if (type != Unit) return;
			if (bytes.Length < 0x74 || BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]) != 0)
				throw new InvalidDataException("Unit 载荷截断或引用了不支持的 Composite。");
			var bone = BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
			if (bone != 0) refs.Add(new(PatchUnitMeshReader.BoneTypeId, bone));
			var table = BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x70..]);
			if (table < 0x74 || table > bytes.Length - 4) throw new InvalidDataException("Unit 材质表超出载荷。");
			var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(int)table..]);
			if (table + 4UL + count * 12UL > (ulong)bytes.Length) throw new InvalidDataException("Unit 材质表截断。");
			for (var index = 0; index < count; index++)
			{
				var id = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(int)(table + 4 + count * 4 + index * 8)..]);
				if (id == 0) throw new InvalidDataException("Unit 包含空材质身份。");
				refs.Add(new(Material, id));
			}
		}
	}

	private static ulong Hex(string id) => ulong.Parse(id, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
	private static bool Expected(Exception error) => error is IOException or InvalidDataException or InvalidOperationException or ArgumentException;
}
