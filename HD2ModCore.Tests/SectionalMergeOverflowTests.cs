using System.Buffers.Binary;
using System.Globalization;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModCore.Infrastructure.KitInstance;
using HD2ModCore.Infrastructure.Sectional;
using AdaptationAssetKey = HD2ModAdaptation.PatchReconstruction.AssetKey;

namespace HD2ModCore.Tests;

// Purpose: Verifies 128 MiB D3D12 merge overflow attribution, conflict classification, one-click group disable + wardrobe persistence, and unmerged fallback.
public sealed class SectionalMergeOverflowTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "hd2-merge-overflow-" + Guid.NewGuid().ToString("N"));

	public SectionalMergeOverflowTests() => Directory.CreateDirectory(_root);

	[Fact]
	public void Merger_TranslatesStreamBufferLimitDiagnostic_IntoStructuredOverflowException()
	{
		var diagnostics = new[]
		{
			new CanonicalPlanDiagnostic(
				CanonicalUnitRebuilder.StreamBufferLimitDiagnosticCode,
				"Stream=0; Buffer=VertexBuffer; Bytes=136656064; Limit=134217728; Vertices=2138376; Stride=64; Stream 0 vertex buffer reaches limit.")
		};

		var overflow = SectionalUnitMerger.TryCreateOverflowException(
			["39851901bad30003", "39851901bad30001", "39851901bad30006", "39851901bad30007"],
			diagnostics);

		Assert.NotNull(overflow);
		Assert.Equal(136_656_064UL, overflow!.ActualBytes);
		Assert.Equal(CanonicalUnitRebuilder.MaxD3D12SingleBufferBytes, overflow.LimitBytes);
		Assert.Equal(64u, overflow.Stride);
		Assert.Equal(0, overflow.StreamIndex);
		Assert.Equal("VertexBuffer", overflow.BufferKind);
	}

	[Fact]
	public void Analyzer_IdentifiesExplicitExclusiveConflict_AndPreUnchecksExtras()
	{
		var components = new SectionGroupDocument(
			SectionGroupDocument.CurrentSchema,
			[
				new SectionGroup("服装A", "A", [], [new SectionGroupTarget("39851901bad30003", [0])])
				{
					Id = "ga",
					ExclusiveGroup = "outfit",
				},
				new SectionGroup("服装B", "B", [], [new SectionGroupTarget("39851901bad30003", [1])])
				{
					Id = "gb",
					ExclusiveGroup = "outfit",
				},
			],
			[]);
		var indicesByUnit = new Dictionary<string, IReadOnlyList<ulong>>(StringComparer.OrdinalIgnoreCase)
		{
			["39851901bad30003"] = [300_000UL, 300_000UL],
		};

		var context = SectionalMergeOverflowAnalyzer.Analyze(
			modDisplayName: "TestMod",
			mergedSlotDisplayName: "身体+手臂合体槽",
			sourceUnitIds: ["39851901bad30003"],
			actualBufferBytes: 150UL * 1024 * 1024,
			limitBytes: CanonicalUnitRebuilder.MaxD3D12SingleBufferBytes,
			components: components,
			sectionIndicesByUnit: indicesByUnit,
			disabledSections: null,
			canFallbackToUnmerged: true);

		Assert.Equal(2, context.Groups.Count);
		Assert.All(context.Groups, group => Assert.Equal(SectionalOverflowConflictKind.ExplicitExclusive, group.ConflictKind));
		Assert.True(context.Groups[0].RecommendedKeepEnabled);
		Assert.False(context.Groups[1].RecommendedKeepEnabled);

		var kept = context.Groups.Where(group => group.RecommendedKeepEnabled).Select(group => group.GroupId).ToHashSet(StringComparer.Ordinal);
		var projected = SectionalMergeOverflowAnalyzer.ProjectRemainingBytes(context, kept);
		Assert.True(projected < CanonicalUnitRebuilder.MaxD3D12SingleBufferBytes, $"Projected bytes {projected} should be < 128 MiB.");
	}

	[Fact]
	public void Analyzer_IdentifiesSuspectedFullBodyVariants_ForAiriShape_AndShortensDisplayNames()
	{
		const string modName = "Airi kitty nitty and royal bunny 2.3-10415-v1-1-1770357928";
		var units = Enumerable.Range(1, 7).Select(i => $"39851901bad3000{i}").ToArray();
		var noSkirtMembers = units.Select(unit => new SectionGroupTarget(unit, [0])).ToArray();
		var withSkirtMembers = units.Select(unit => new SectionGroupTarget(unit, [1])).ToArray();
		var components = new SectionGroupDocument(
			SectionGroupDocument.CurrentSchema,
			[
				new SectionGroup(modName + "-2.3-本体无裙子", modName + "-2.3-本体无裙子", [], noSkirtMembers)
				{
					Id = "gf7a38e978336",
					Source = SectionGroupSource.DonorUnit,
				},
				new SectionGroup(modName + "-2.3-本体带裙子", modName + "-2.3-本体带裙子", [], withSkirtMembers)
				{
					Id = "g62ea3cdc10d4",
					Source = SectionGroupSource.DonorUnit,
				},
			],
			[]);

		var indicesByUnit = new Dictionary<string, IReadOnlyList<ulong>>(StringComparer.OrdinalIgnoreCase)
		{
			["39851901bad30001"] = [531_000UL, 531_000UL], // Helmet
			["39851901bad30002"] = [475_000UL, 475_000UL], // Torso
			["39851901bad30006"] = [60_000UL, 60_000UL],   // LeftArm
			["39851901bad30007"] = [60_000UL, 60_000UL],   // RightArm
		};

		var context = SectionalMergeOverflowAnalyzer.Analyze(
			modDisplayName: modName,
			mergedSlotDisplayName: "身体+手臂合体槽",
			sourceUnitIds: ["39851901bad30002", "39851901bad30001", "39851901bad30006", "39851901bad30007"],
			actualBufferBytes: 136_656_064UL,
			limitBytes: CanonicalUnitRebuilder.MaxD3D12SingleBufferBytes,
			components: components,
			sectionIndicesByUnit: indicesByUnit,
			disabledSections: null,
			canFallbackToUnmerged: true);

		Assert.Equal(2, context.Groups.Count);
		Assert.Equal("2.3-本体无裙子", context.Groups[0].DisplayName);
		Assert.Equal("2.3-本体带裙子", context.Groups[1].DisplayName);
		Assert.All(context.Groups, group => Assert.Equal(SectionalOverflowConflictKind.SuspectedVariant, group.ConflictKind));
		Assert.True(context.Groups[0].RecommendedKeepEnabled);
		Assert.False(context.Groups[1].RecommendedKeepEnabled);

		var kept = context.Groups.Where(group => group.RecommendedKeepEnabled).Select(group => group.GroupId).ToHashSet(StringComparer.Ordinal);
		var projected = SectionalMergeOverflowAnalyzer.ProjectRemainingBytes(context, kept);
		Assert.True(projected < CanonicalUnitRebuilder.MaxD3D12SingleBufferBytes);

		var updatedDisabled = SectionalMergeOverflowAnalyzer.ApplyDisabledGroups(
			null,
			components,
			["g62ea3cdc10d4"]);
		Assert.Equal(7, updatedDisabled.Count);
		Assert.Equal([1], updatedDisabled["39851901bad30002"]);
	}

	[Fact]
	public async Task CompileAsync_WhenOneClickDisableChosen_BakesDisabledSectionsRetriesMergeAndPersistsWardrobeJson()
	{
		var (modDir, storagePaths, gameDataDir) = await CreateSyntheticSevenSlotSectionalModAsync();
		SectionalMergeOverflowContext? captured = null;
		var context = new VpkHostContext(gameDataDir, storagePaths)
		{
			LowPollutionMode = true,
			OnMergeOverflow = (overflow, _) =>
			{
				captured = overflow;
				var toDisable = overflow.Groups
					.Where(group => !group.RecommendedKeepEnabled)
					.Select(group => group.GroupId)
					.ToArray();
				return ValueTask.FromResult(new SectionalMergeOverflowDecision(
					SectionalMergeOverflowAction.DisableSelectedGroups,
					toDisable));
			},
			MergeUnitOverride = SimulateOverflowWhenBothVariantsEnabledAsync,
		};

		var outcome = await ModCompileService.CompileAsync("test-mod", modDir, context, headMergeIntoBody: true);

		Assert.True(outcome.Ok, outcome.Error);
		Assert.NotNull(captured);
		Assert.True(captured!.CanFallbackToUnmerged);
		Assert.Equal(2, captured.Groups.Count);
		Assert.NotNull(outcome.UpdatedDisabledSections);
		Assert.Contains("39851901bad30002", outcome.UpdatedDisabledSections!.Keys);
		Assert.True(File.Exists(Path.Combine(ModNodePayloadLayout.GetOverwriteDirectory(modDir), LowPollutionLayoutFile.FileName)));

		var persistedWardrobe = await VpkModWardrobeService.ReadAsync(modDir);
		Assert.True(persistedWardrobe.State.IsDisabled("39851901bad30002", 1));
		Assert.False(persistedWardrobe.State.IsDisabled("39851901bad30002", 0));
	}

	[Fact]
	public async Task CompileAsync_WhenFallbackToUnmergedChosen_WritesOwnModelAndUnsupportedMarkerWithoutChangingWardrobe()
	{
		var (modDir, storagePaths, gameDataDir) = await CreateSyntheticSevenSlotSectionalModAsync();
		var context = new VpkHostContext(gameDataDir, storagePaths)
		{
			LowPollutionMode = true,
			OnMergeOverflow = (_, _) => ValueTask.FromResult(new SectionalMergeOverflowDecision(
				SectionalMergeOverflowAction.FallbackToUnmerged,
				[])),
			MergeUnitOverride = SimulateOverflowWhenBothVariantsEnabledAsync,
		};

		var outcome = await ModCompileService.CompileAsync("test-mod", modDir, context, headMergeIntoBody: true);

		Assert.True(outcome.Ok, outcome.Error);
		Assert.Null(outcome.UpdatedDisabledSections);
		var overwrite = ModNodePayloadLayout.GetOverwriteDirectory(modDir);
		Assert.False(File.Exists(Path.Combine(overwrite, LowPollutionLayoutFile.FileName)));
		Assert.True(File.Exists(Path.Combine(overwrite, LowPollutionFreshness.UnsupportedMarkerFileName)));

		var wardrobe = await VpkModWardrobeService.ReadAsync(modDir);
		Assert.Empty(wardrobe.State.Disabled);
		Assert.Equal(
			LowPollutionPrepStatus.UpToDate,
			LowPollutionFreshness.Assess("custom-airi", modDir, true, wardrobe with { HeadMergeIntoBody = true }).Status);
	}

	[Fact]
	public async Task DualCache_SwitchesCompleteArtifactsAndRecompilesOnlyChangedModels()
	{
		var a = await CreateSyntheticSevenSlotSectionalModAsync("custom-dual-a");
		var b = await CreateSyntheticSevenSlotSectionalModAsync("custom-dual-b");
		const string firstKey = "custom-dual-a";
		const string secondKey = "custom-dual-b";
		const string patchName = "9ba626afa44a3aa3.patch_0";
		var wardrobe = new VpkWardrobeFile(true,
			new VpkWardrobeState(new Dictionary<string, IReadOnlyCollection<int>> { ["39851901bad30002"] = [1] }),
			HeadMergeIntoBody: true);
		await VpkModWardrobeService.WriteAsync(a.ModDirectory, wardrobe);
		var context = new VpkHostContext(a.GameDataDirectory, a.Paths)
		{
			MergeUnitOverride = SimulateOverflowWhenBothVariantsEnabledAsync,
		};

		async Task<LowPollutionRecompileResult> Apply(bool enabled, bool npc = false)
		{
			var mode = context with { LowPollutionMode = enabled };
			var npcByModel = LowPollutionFreshness.NpcItemIdsByModel(enabled && npc
				? [new RuntimeArmorRegistryMapping(KitNpcRecords.FemaleCaptainItemId, null, firstKey)]
				: []);
			VpkHostContext ContextOf(LowPollutionPrepItem item) => mode with
			{
				NpcItemIds = npcByModel.TryGetValue(item.ModelKey, out var mapped) ? [.. mapped] : [],
			};
			var plan = await LowPollutionFreshness.PlanAsync(a.Paths.ModsDirectory, [firstKey, secondKey], enabled,
				npcByModel: npcByModel, cacheContext: mode);
			var run = await LowPollutionRecompiler.RunAsync(plan, async (item, token) =>
			{
				var compiled = await ModCompileService.CompileAsync(item.ModelKey, item.ModDirectory, ContextOf(item), cancellationToken: token);
				return (compiled.Ok, compiled.Error ?? string.Empty);
			}, progress: null, tryRestore: (item, token) => LowPollutionFreshness.TryActivateCachedAsync(item, ContextOf(item), token));
			Assert.Empty(run.Failed);
			return run;
		}

		var aOverwrite = ModNodePayloadLayout.GetOverwriteDirectory(a.ModDirectory);
		var aPayload = ModNodePayloadLayout.GetVpkPatchDirectory(a.ModDirectory);
		var compressedSlot = LowPollutionArtifactCache.Slot(a.ModDirectory, true);
		var ordinarySlot = LowPollutionArtifactCache.Slot(a.ModDirectory, false);
		var parser = CoreServices.CreatePatchFileNameParser();
		var id = ModNodeId.New();
		var relative = Path.GetFileName(a.ModDirectory);
		var node = new ModNode(id, relative, new ModNodeMetadata(relative, null, DateTimeOffset.UtcNow, null), [], []);
		var profile = new Profile(ProfileId.New(), "切换验证", DateTimeOffset.UtcNow, null, [new ProfileEntry(id, 0)]);
		var snapshot = new LibrarySnapshot(1, DateTimeOffset.UtcNow, new Dictionary<ModNodeId, ModNode> { [id] = node }, [profile]);

		Assert.Equal(2, (await Apply(true, npc: true)).RecompileCount);
		Assert.True(File.Exists(Path.Combine(aOverwrite, LowPollutionLayoutFile.FileName)));
		Assert.Empty(ProfileApplyService.ExcludeCompressedModels(profile, snapshot, a.Paths.ModsDirectory, []).Entries);
		var npcLayout = await File.ReadAllBytesAsync(Path.Combine(aOverwrite, LowPollutionLayoutFile.FileName));
		var compressedUnit = await File.ReadAllBytesAsync(Path.Combine(aOverwrite, patchName));
		Assert.Equal(2, (await Apply(false)).RecompileCount);
		Assert.False(File.Exists(Path.Combine(aOverwrite, LowPollutionLayoutFile.FileName)));
		Assert.True(File.Exists(Path.Combine(compressedSlot, LowPollutionLayoutFile.FileName)));
		Assert.Equal(profile.Entries, ProfileApplyService.ExcludeCompressedModels(profile, snapshot, a.Paths.ModsDirectory, []).Entries);
		var ordinaryUnit = await File.ReadAllBytesAsync(Path.Combine(aOverwrite, patchName));
		Assert.NotEqual(ordinaryUnit, compressedUnit);
		var deployable = ModDeployableFiles.Resolve(a.ModDirectory, parser);
		Assert.All(deployable.FilesByName.Values, path => Assert.Equal(aOverwrite, Path.GetDirectoryName(path)));
		Assert.Equal(ordinaryUnit, await File.ReadAllBytesAsync(deployable.FilesByName[patchName]));
		Assert.DoesNotContain(LowPollutionLayoutFile.FileName, deployable.FilesByName.Keys);
		var imported = await new ObjectTreeImporter(parser).ImportFolderAsync(a.ModDirectory);
		Assert.DoesNotContain(imported.Nodes.Values, item => item.RelativePath.Contains(LowPollutionArtifactCache.DirectoryName, StringComparison.Ordinal));

		foreach (var enabled in new[] { true, false, true, false, true })
		{
			var switched = await Apply(enabled, npc: true);
			Assert.Equal(0, switched.RecompileCount);
			Assert.Equal(2, switched.Restored.Count);
			Assert.Equal(enabled ? compressedUnit : ordinaryUnit, await File.ReadAllBytesAsync(Path.Combine(aOverwrite, patchName)));
			Assert.True(File.Exists(Path.Combine(aOverwrite, WardrobeSectionMapFile.FileName)));
		}
		Assert.Equal(npcLayout, await File.ReadAllBytesAsync(Path.Combine(aOverwrite, LowPollutionLayoutFile.FileName)));
		Assert.True(File.Exists(Path.Combine(ordinarySlot, SectionGroupFile.FileName)));
		Assert.True(File.Exists(Path.Combine(ordinarySlot, VpkCustomModelFile.FileName)));
		Assert.Equal(2, (await Apply(true, npc: true)).UpToDate.Count);

		var beforeSidecarChange = await Apply(false);
		Assert.Equal(0, beforeSidecarChange.RecompileCount);
		var sourceGpu = Path.Combine(aPayload, patchName + ".gpu_resources");
		await using (var stream = new FileStream(sourceGpu, FileMode.Append))
			stream.WriteByte(0x5a);
		Assert.Equal(firstKey, Assert.Single((await Apply(true, npc: true)).Recompiled).ModelKey);

		var sections = Path.Combine(aPayload, WardrobeSectionMapFile.FileName);
		var sourceMap = await WardrobeSectionMapFile.ReadAsync(sections);
		await WardrobeSectionMapFile.WriteAsync(sections, sourceMap with { Patch = sourceMap.Patch with { Path = "moved-source.patch_0" } });
		Assert.Equal(2, (await Apply(true, npc: true)).UpToDate.Count);
		await using (var stream = new FileStream(Path.Combine(aPayload, patchName), FileMode.Append))
			stream.WriteByte(0x69);
		Assert.Equal(firstKey, Assert.Single((await Apply(true, npc: true)).Recompiled).ModelKey);

		await VpkModWardrobeService.WriteAsync(a.ModDirectory, wardrobe with
		{
			State = new VpkWardrobeState(new Dictionary<string, IReadOnlyCollection<int>>
			{
				["39851901bad30002"] = [1],
				["39851901bad30003"] = [1],
			}),
		});
		Assert.Equal(firstKey, Assert.Single((await Apply(true, npc: true)).Recompiled).ModelKey);
		Assert.Equal(firstKey, Assert.Single((await Apply(true, npc: false)).Recompiled).ModelKey);

		Assert.Equal(firstKey, Assert.Single((await Apply(false)).Recompiled).ModelKey);
		var materialName = "9ba626afa44a3aa3.patch_1";
		await WriteSyntheticPatchArchiveAsync(Path.Combine(aPayload, materialName), [new(new AdaptationAssetKey(0xeac0b497876ade78, 1), [0x12], [], [])]);
		Assert.Null(ModSettingsFile.TryWrite(a.ModDirectory, new ModSettingsFile.Document(ModSettingsFile.CurrentSchema, [materialName])));
		var materialized = OverwriteMaterializer.Materialize(a.ModDirectory, OverwriteSelection.Default);
		Assert.True(materialized.Succeeded, materialized.Error);
		Assert.True(File.Exists(Path.Combine(aOverwrite, SectionGroupFile.FileName)));
		Assert.True(File.Exists(Path.Combine(aOverwrite, materialName)));
		Assert.False(Directory.Exists(compressedSlot));
		Assert.Equal(2, (await Apply(false)).UpToDate.Count);
		Assert.Equal(firstKey, Assert.Single((await Apply(true)).Recompiled).ModelKey);
		Assert.True(File.Exists(Path.Combine(aOverwrite, materialName)));
		Assert.True(File.Exists(Path.Combine(ordinarySlot, materialName)));
		Assert.Equal(0, (await Apply(false)).RecompileCount);

		var deletion = await new ModStorageDeletionService(a.Paths).DeleteAsync(relative);
		Assert.Equal(ModStorageDeletionState.DeletedImmediately, deletion.State);
		Assert.False(Directory.Exists(a.ModDirectory));
		Assert.False(Directory.Exists(compressedSlot));
		Assert.True(Directory.Exists(b.ModDirectory));
	}

	[Fact]
	public async Task DualCache_GameInputFingerprintReadsAvatarAndNativeUnitPayloads()
	{
		var fixture = await CreateSyntheticSevenSlotSectionalModAsync();
		var avatar = new byte[0x60];
		BinaryPrimitives.WriteUInt32LittleEndian(avatar.AsSpan(0x34), 0x40);
		var avatarPath = Path.Combine(fixture.GameDataDirectory, CanonicalAvatarRigReader.AvatarArchiveName);
		WriteGameUnit(avatarPath, CanonicalAvatarRigReader.AvatarUnitFileId, avatar, []);
		var npc = KitNpcRecords.All.Single(record => record.RequiresNativeShell);
		var shellPath = Path.Combine(fixture.GameDataDirectory, npc.RootArchiveId);
		var (shellToc, shellGpu) = BuildMinimalReadableTwoSectionUnitPayload();
		WriteGameUnit(shellPath, npc.RootUnitId, shellToc, shellGpu);

		var payload = ModNodePayloadLayout.GetVpkPatchDirectory(fixture.ModDirectory);
		var map = await WardrobeSectionMapFile.ReadAsync(Path.Combine(payload, WardrobeSectionMapFile.FileName));
		var context = new VpkHostContext(fixture.GameDataDirectory, fixture.Paths) { NpcItemIds = [npc.ItemId] };
		async ValueTask<string> ReadGameKey()
		{
			var inputs = await LowPollutionArtifactCache.CaptureAsync(fixture.ModDirectory, payload,
				Path.Combine(payload, "9ba626afa44a3aa3.patch_0"), map, disabled: null, headMerge: true,
				lowPollution: true, context, OverwriteSelection.Default, default);
			Assert.NotNull(inputs);
			return inputs.Game;
		}

		var original = await ReadGameKey();
		avatar[0x44] = 1;
		WriteGameUnit(avatarPath, CanonicalAvatarRigReader.AvatarUnitFileId, avatar, []);
		var changedAvatar = await ReadGameKey();
		Assert.NotEqual(original, changedAvatar);
		WriteGameUnit(shellPath, npc.RootUnitId, shellToc, shellGpu, unknown3: 1);
		Assert.NotEqual(changedAvatar, await ReadGameKey());
	}

	private static void WriteGameUnit(string path, ulong unitId, byte[] toc, byte[] gpu, uint unknown3 = 0)
	{
		const int entryOffset = 60 + 32;
		const int dataOffset = entryOffset + 80;
		var archive = new byte[dataOffset + toc.Length];
		BinaryPrimitives.WriteUInt32LittleEndian(archive, 4026531857);
		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(4), 1);
		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(8), 1);
		BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(68), PatchUnitMeshReader.UnitTypeId);
		BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(76), 1);
		BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(entryOffset), unitId);
		BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(entryOffset + 8), PatchUnitMeshReader.UnitTypeId);
		BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(entryOffset + 16), dataOffset);
		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(entryOffset + 56), (uint)toc.Length);
		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(entryOffset + 64), (uint)gpu.Length);
		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(entryOffset + 72), unknown3);
		toc.CopyTo(archive, dataOffset);
		File.WriteAllBytes(path, archive);
		if (gpu.Length != 0) File.WriteAllBytes(path + ".gpu_resources", gpu);
	}

	[Fact]
	public void PreparedOverwrite_RestoresBothDirectoriesWhenSavingOutgoingArtifactFails()
	{
		var active = Path.Combine(_root, "Overwrite");
		var incoming = Path.Combine(_root, "incoming");
		var blocked = Path.Combine(_root, "occupied");
		foreach (var directory in new[] { active, incoming, blocked }) Directory.CreateDirectory(directory);
		ManagedOverwriteDirectory.Commit(active, HD2ModCore.Decoration.ManagedOverwriteMarkers.VpkRekey);
		ManagedOverwriteDirectory.Commit(incoming, HD2ModCore.Decoration.ManagedOverwriteMarkers.VpkRekey);
		File.WriteAllText(Path.Combine(active, "ordinary"), "old");
		File.WriteAllText(Path.Combine(incoming, "compressed"), "new");
		Assert.ThrowsAny<IOException>(() => ManagedOverwriteDirectory.ReplacePrepared(active, incoming, blocked));
		Assert.Equal("old", File.ReadAllText(Path.Combine(active, "ordinary")));
		Assert.Equal("new", File.ReadAllText(Path.Combine(incoming, "compressed")));
		Assert.Empty(Directory.EnumerateDirectories(_root, "Overwrite.backup-*"));
	}

	[Fact]
	public async Task LowPollutionRecompile_MaterialRefreshKeepsBothKindsOfFreshResults()
	{
		var compressed = await CreateSyntheticSevenSlotSectionalModAsync("custom-compressed");
		var unsupported = await CreateSyntheticSevenSlotSectionalModAsync("custom-unsupported");
		var enabled = new Dictionary<string, IReadOnlyCollection<int>> { ["39851901bad30002"] = [1] };
		await VpkModWardrobeService.WriteAsync(compressed.ModDirectory,
			new VpkWardrobeFile(true, new VpkWardrobeState(enabled), HeadMergeIntoBody: true));
		var context = new VpkHostContext(compressed.GameDataDirectory, compressed.Paths)
		{
			LowPollutionMode = true,
			MergeUnitOverride = SimulateOverflowWhenBothVariantsEnabledAsync,
		};
		var mods = Path.GetDirectoryName(compressed.ModDirectory)!;
		string[] keys = ["custom-compressed", "custom-unsupported"];

		async Task<LowPollutionRecompileResult> Apply(bool lowPollution = true, bool mapNpc = false)
		{
			var npcByModel = LowPollutionFreshness.NpcItemIdsByModel(mapNpc
				? [new RuntimeArmorRegistryMapping(KitNpcRecords.FemaleCaptainItemId, null, keys[0])]
				: []);
			var plan = await LowPollutionFreshness.PlanAsync(mods, keys, lowPollution, npcByModel: npcByModel);
			return await LowPollutionRecompiler.RunAsync(plan, async (item, token) =>
			{
				var compileContext = context with
				{
					LowPollutionMode = lowPollution,
					NpcItemIds = npcByModel.TryGetValue(item.ModelKey, out var npcs) ? [.. npcs.Order()] : [],
				};
				var result = await ModCompileService.CompileAsync(item.ModelKey, item.ModDirectory,
					compileContext, cancellationToken: token);
				return (result.Ok, result.Error ?? string.Empty);
			}, progress: null);
		}

		var first = await Apply();
		Assert.Empty(first.Failed);
		Assert.Equal(2, first.Recompiled.Count);
		var unchanged = await Apply();
		Assert.Empty(unchanged.Recompiled);
		Assert.Equal(2, unchanged.UpToDate.Count);

		Assert.True(File.Exists(Path.Combine(compressed.ModDirectory, "Overwrite", LowPollutionLayoutFile.FileName)));
		Assert.True(File.Exists(Path.Combine(unsupported.ModDirectory, "Overwrite", LowPollutionFreshness.UnsupportedMarkerFileName)));
		foreach (var directory in new[] { compressed.ModDirectory, unsupported.ModDirectory })
		{
			var structure = Path.Combine(directory, "Overwrite", WardrobeSectionMapFile.FileName);
			var previous = await File.ReadAllBytesAsync(structure);
			var refreshed = OverwriteMaterializer.Materialize(directory, OverwriteSelection.Empty);
			Assert.True(refreshed.Succeeded, refreshed.Error);
			Assert.Equal(previous, await File.ReadAllBytesAsync(structure));
		}

		var afterMaterials = await Apply();
		Assert.Empty(afterMaterials.Recompiled);
		Assert.Equal(2, afterMaterials.UpToDate.Count);

		var sections = Path.Combine(compressed.ModDirectory, ModNodePayloadLayout.VpkPatchDirectoryName, WardrobeSectionMapFile.FileName);
		var document = await WardrobeSectionMapFile.ReadAsync(sections, default);
		var changed = document.Units.Select(unit => unit.Slot == "helmet" ? unit with { BodyType = "Slim" } : unit).ToArray();
		await WardrobeSectionMapFile.WriteAsync(sections, document with { Units = changed }, default);
		var afterSourceChange = await Apply();
		Assert.Empty(afterSourceChange.Failed);
		Assert.Equal("custom-compressed", Assert.Single(afterSourceChange.Recompiled).ModelKey);
		Assert.Equal("custom-unsupported", Assert.Single(afterSourceChange.UpToDate).ModelKey);

		var afterMappingChange = await Apply(mapNpc: true);
		Assert.Empty(afterMappingChange.Failed);
		Assert.Equal("custom-compressed", Assert.Single(afterMappingChange.Recompiled).ModelKey);
		Assert.Equal(2, (await Apply(mapNpc: true)).UpToDate.Count);

		var disabled = await Apply(lowPollution: false);
		Assert.Empty(disabled.Failed);
		Assert.Equal(2, disabled.Recompiled.Count);
		Assert.Equal(2, (await Apply(lowPollution: false)).UpToDate.Count);
	}

	[Fact]
	public async Task CompileAsync_WhenOverflowCanceled_AbortsWithoutWritingOverwriteOrWardrobe()
	{
		var (modDir, storagePaths, gameDataDir) = await CreateSyntheticSevenSlotSectionalModAsync();
		var context = new VpkHostContext(gameDataDir, storagePaths)
		{
			LowPollutionMode = true,
			OnMergeOverflow = (_, _) => ValueTask.FromResult(new SectionalMergeOverflowDecision(
				SectionalMergeOverflowAction.Cancel,
				[])),
			MergeUnitOverride = SimulateOverflowWhenBothVariantsEnabledAsync,
		};

		var outcome = await ModCompileService.CompileAsync("test-mod", modDir, context, headMergeIntoBody: true);

		Assert.False(outcome.Ok);
		Assert.False(Directory.Exists(ModNodePayloadLayout.GetOverwriteDirectory(modDir)));
		var wardrobe = await VpkModWardrobeService.ReadAsync(modDir);
		Assert.Empty(wardrobe.State.Disabled);
	}

	[Fact]
	public async Task CompileAsync_WhenHeadlessWithoutCallback_FallsBackToUnmergedWithWarningNamingVariants()
	{
		var (modDir, storagePaths, gameDataDir) = await CreateSyntheticSevenSlotSectionalModAsync();
		var context = new VpkHostContext(gameDataDir, storagePaths)
		{
			LowPollutionMode = true,
			MergeUnitOverride = SimulateOverflowWhenBothVariantsEnabledAsync,
		};

		var outcome = await ModCompileService.CompileAsync("test-mod", modDir, context, headMergeIntoBody: true);

		Assert.True(outcome.Ok, outcome.Error);
		Assert.Contains(outcome.Notes, note => note.Contains("128", StringComparison.Ordinal) && note.Contains("本体带裙子", StringComparison.Ordinal));
	}

	private static async Task<SectionalMergedUnit> SimulateOverflowWhenBothVariantsEnabledAsync(
		string prePatchPath,
		SectionalUnitMergeSpec spec,
		CancellationToken cancellationToken)
	{
		var map = (await WardrobeSectionMapFile.BuildAsync(prePatchPath, cancellationToken)).Document;
		// Check whether Torso (39851901bad30002) still has BOTH section 0 and section 1 enabled in prePatchPath!
		var bytes = await File.ReadAllBytesAsync(prePatchPath, cancellationToken);
		var slices = WardrobeSectionBaker.ScanUnitSlices(bytes, prePatchPath);
		if (spec.SourceFileIds.Contains("39851901bad30002", StringComparer.OrdinalIgnoreCase)
			&& slices.TryGetValue("39851901bad30002", out var slice))
		{
			var torsoMap = map.Units.Single(u => u.FileId == "39851901bad30002");
			var enabledSections = torsoMap.Sections.Count(section =>
			{
				var rel = int.Parse(section.Toggles[0].NumIndicesOffset.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
				return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(slice.Offset + rel, 4)) != 0;
			});
			if (enabledSections >= 2)
			{
				throw new SectionalMergeBufferOverflowException(
					spec.SourceFileIds,
					streamIndex: 0,
					bufferKind: "VertexBuffer",
					actualBytes: 136_656_064UL,
					limitBytes: CanonicalUnitRebuilder.MaxD3D12SingleBufferBytes,
					stride: 56u,
					"Stream 0 vertex buffer exceeds 128 MiB.");
			}
		}

		// Return valid readable unit bytes from the first member in prePatchPath so downstream WardrobeSectionMapFile.BuildAsync succeeds!
		var scanned = await new HD2ModAdaptation.PatchReconstruction.PatchTocScanner().ScanEntriesAsync(prePatchPath, cancellationToken);
		var hostId = ulong.Parse(spec.SourceFileIds[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
		var hostEntry = scanned.Single(entry => entry.AssetKey.FileId == hostId);
		var payload = await new HD2ModAdaptation.PatchReconstruction.PatchEntryPayloadReader().ReadPayloadAsync(hostEntry, cancellationToken);
		return new SectionalMergedUnit(
			payload.TocData,
			payload.GpuResourceData,
			payload.StreamData,
			hostEntry.Unknown1,
			hostEntry.Unknown2,
			hostEntry.Unknown3,
			hostEntry.Unknown4,
			[]);
	}

	private async Task<(string ModDirectory, StoragePaths Paths, string GameDataDirectory)> CreateSyntheticSevenSlotSectionalModAsync(
		string modelKey = "custom-airi")
	{
		var modsRoot = Path.Combine(_root, "mods");
		var modDir = Path.Combine(modsRoot, modelKey == "custom-airi" ? "AiriTestMod" : modelKey);
		var payloadDir = Path.Combine(modDir, ModNodePayloadLayout.VpkPatchDirectoryName);
		var gameDataDir = Path.Combine(_root, "gamedata");
		Directory.CreateDirectory(payloadDir);
		Directory.CreateDirectory(gameDataDir);

		var slotDefs = new (string UnitId, string Slot, int RegistrySlot)[]
		{
			("39851901bad30001", "helmet", 0),
			("39851901bad30002", "torso", 2),
			("39851901bad30003", "hips", 3),
			("39851901bad30004", "left_leg", 4),
			("39851901bad30005", "right_leg", 5),
			("39851901bad30006", "left_arm", 6),
			("39851901bad30007", "right_arm", 7),
		};

		var additions = new List<PatchArchiveAdditionalEntry>();
		foreach (var (unitId, _, _) in slotDefs)
		{
			var (toc, gpu) = BuildMinimalReadableTwoSectionUnitPayload();
			additions.Add(new PatchArchiveAdditionalEntry(
				new AdaptationAssetKey(PatchUnitMeshReader.UnitTypeId, ulong.Parse(unitId, NumberStyles.HexNumber, CultureInfo.InvariantCulture)),
				toc,
				[],
				gpu));
		}

		var patchPath = Path.Combine(payloadDir, "9ba626afa44a3aa3.patch_0");
		await WriteSyntheticPatchArchiveAsync(patchPath, additions);

		var built = await WardrobeSectionMapFile.BuildAsync(patchPath);
		var identities = slotDefs.ToDictionary(
			item => item.UnitId,
			item => new WardrobeSectionMapUnitIdentity(item.Slot, "undergarment", "Any", "Light"),
			StringComparer.OrdinalIgnoreCase);
		var map = WardrobeSectionMapFile.ApplyUnitIdentities(built.Document, identities);
		await WardrobeSectionMapFile.WriteAsync(Path.Combine(payloadDir, WardrobeSectionMapFile.FileName), map);

		var components = new SectionGroupDocument(
			SectionGroupDocument.CurrentSchema,
			[
				new SectionGroup("AiriTestMod-本体无裙子", "AiriTestMod-本体无裙子", [],
					slotDefs.Select(item => new SectionGroupTarget(item.UnitId, [0])).ToArray())
				{
					Id = "g_noskirt",
					Source = SectionGroupSource.DonorUnit,
				},
				new SectionGroup("AiriTestMod-本体带裙子", "AiriTestMod-本体带裙子", [],
					slotDefs.Select(item => new SectionGroupTarget(item.UnitId, [1])).ToArray())
				{
					Id = "g_skirt",
					Source = SectionGroupSource.DonorUnit,
				},
			],
			[]);
		await SectionGroupFile.WriteAsync(payloadDir, components);

		var piecesJson = string.Join(",", slotDefs.Select(item =>
			$"{{\"slot\":{item.RegistrySlot},\"pieceType\":0,\"body\":3,\"unit\":\"{item.UnitId}\"}}"));
		await File.WriteAllTextAsync(
			Path.Combine(payloadDir, VpkCustomModelFile.FileName),
			$"{{\"schema\":\"hd2mod.custom-model/1\",\"key\":\"{modelKey}\",\"name\":\"AiriTestMod\",\"pieces\":[{piecesJson}]}}");

		return (modDir, new StoragePaths(_root, modsRoot), gameDataDir);
	}

	private static (byte[] TocData, byte[] GpuData) BuildMinimalReadableTwoSectionUnitPayload()
	{
		// Build a valid inline Unit (version 10800438) with 1 LOD0 mesh and 2 material sections (3 indices each) via CanonicalUnitRebuilder!
		var baseToc = new byte[2048];
		BinaryPrimitives.WriteUInt32LittleEndian(baseToc.AsSpan(0x2c, 4), 10800438u);
		// StreamInfo table at 0x80: count=1, offsets=[16], record at 0x90 (size 432 -> ends 0x240)
		BinaryPrimitives.WriteUInt32LittleEndian(baseToc.AsSpan(0x5c, 4), 0x80u);
		BinaryPrimitives.WriteUInt32LittleEndian(baseToc.AsSpan(0x80, 4), 1u);
		BinaryPrimitives.WriteUInt32LittleEndian(baseToc.AsSpan(0x84, 4), 16u);

		var stream = new UnitStreamInfo(0, 0x90u, 1, 1, 0, 0, 12, 0, 6, 0, 0, 0, 0, 0,
			[new UnitStreamComponentInfo(0, "position", 2, "vec3_float", 0, 0, 12)]);
		var mesh = new UnitMeshInfo(
			0, 0x300u, 100u, 0, 0, 0, 2, 128, 2, 136,
			UnitMeshSemanticInfo.Empty(0, 0),
			[10u, 20u],
			[new(0x388u, 0, 10u, 0, 3, 0, 3, 0), new(0x3a0u, 1, 20u, 0, 3, 3, 3, 1)]);
		var target = new UnitMeshModel(
			10800438u, 0, 0, 0, 0, 0, 0x80u, 0, 0x300u, 0x500u,
			UnitCustomizationInfo.Empty,
			[],
			[stream],
			[mesh],
			[new UnitMaterialBinding(10u, 0x1001UL), new UnitMaterialBinding(20u, 0x1002UL)],
			[],
			[]);

		var v0 = new UnitRawVertexRecord(0, new byte[12], []);
		var v1 = new UnitRawVertexRecord(1, new byte[12], []);
		var v2 = new UnitRawVertexRecord(2, new byte[12], []);
		var tri = new UnitTriangleIndices(0, 1, 2);
		var raw = new UnitRawMeshData(
			0, 100u, 0, 0,
			[new UnitRawMeshSectionData(0, 10u, [tri]), new UnitRawMeshSectionData(1, 20u, [tri])],
			[tri, tri],
			[v0, v1, v2]);

		var rebuilt = new CanonicalUnitRebuilder().TryRebuild(target, baseToc, [raw]);
		Assert.True(rebuilt.IsValid, string.Join("; ", rebuilt.Diagnostics.Select(d => d.Message)));
		return (rebuilt.Output!.TocData, rebuilt.Output.GpuData);
	}

	private static async Task WriteSyntheticPatchArchiveAsync(string patchTocPath, IReadOnlyList<PatchArchiveAdditionalEntry> entries)
	{
		var session = new CanonicalPatchSession();
		foreach (var item in entries)
		{
			session.AddEntry(new CanonicalPatchSessionEntry(
				item.AssetKey,
				CanonicalPatchEntryOwnership.TargetOutput,
				item.TocData,
				item.GpuResourceData,
				item.StreamData,
				item.Unknown1,
				item.Unknown2,
				item.Unknown3,
				item.Unknown4));
		}

		Assert.True(session.Finalize(CanonicalDependencyClosureValidation.Valid).IsValid);
		await new CanonicalPatchWriter().WriteAsync(
			session,
			Path.GetDirectoryName(patchTocPath)!,
			Path.GetFileName(patchTocPath),
			null,
			overwriteExisting: true);
	}

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_root))
			{
				foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
					File.SetAttributes(file, FileAttributes.Normal);
				Directory.Delete(_root, recursive: true);
			}
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}
}
