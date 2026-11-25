using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Ias;
using HD2ModCore.Infrastructure;
using Xunit;

namespace HD2ModCore.Tests;

public sealed class IasDeploymentTests
{
#if WINDOWS
	[Theory]
	[InlineData(0, IasPackageDetector.MissingCompanionHint)]
	[InlineData(1, IasPackageDetector.MissingMainHint)]
	public async Task IasPageReportsWhichHalfIsMissingWithoutAddingARole(int part, string hint)
	{
		using var fixture = await Fixture.CreateAsync();
		var module = new HD2ModManager.Services.Ias.IasModuleService(() => fixture.Paths, () => fixture.Game, fixture.Http);
		var result = await module.AddAsync([fixture.Pair[part]], new Progress<HD2ModManager.Services.Ias.IasPageProgress>(), default);

		Assert.False(result.Success);
		Assert.Equal(hint, result.Message);
		Assert.Empty(fixture.Service.GetRoles());
		Assert.Single((await fixture.Service.LoadModsAsync(default)).Nodes);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task IasPageAddsPairedArchivesWithoutAHalfDropHint(bool combined)
	{
		using var fixture = await Fixture.CreateAsync();
		var inputs = fixture.Pair;
		if (combined)
		{
			var bundle = Path.Combine(fixture.Root, "pair.zip");
			using var zip = ZipFile.Open(bundle, ZipArchiveMode.Create);
			foreach (var path in inputs) zip.CreateEntryFromFile(path, Path.GetFileName(path));
			inputs = [bundle];
		}
		var module = new HD2ModManager.Services.Ias.IasModuleService(() => fixture.Paths, () => fixture.Game, fixture.Http);
		var result = await module.AddDroppedAsync(fixture.Service, fixture.Paths, inputs,
			new Progress<HD2ModManager.Services.Ias.IasPageProgress>(), default, null);

		Assert.True(result.Success, result.Message);
		Assert.Empty(result.Warnings);
		Assert.Single(fixture.Service.GetRoles());
	}

	[Theory]
	[InlineData("ordinary.zip")]
	[InlineData("main")]
	public async Task IasPageKeepsOrdinaryPatchConversionForInputsWithoutAnExplicitMainName(string name)
	{
		using var fixture = await Fixture.CreateAsync();
		// 空的缓存 SDK 让普通转换在读取目标时失败，不下载依赖或启动生成器。
		Directory.CreateDirectory(Path.Combine(fixture.Ias, "dependencies", "aqsdk-86b7252"));
		var path = Path.Combine(fixture.Root, name);
		if (name.EndsWith(".zip", StringComparison.Ordinal))
			File.Copy(fixture.Pair[0], path);
		else
			ZipFile.ExtractToDirectory(fixture.Pair[0], path);
		var module = new HD2ModManager.Services.Ias.IasModuleService(() => fixture.Paths, () => fixture.Game, fixture.Http);
		var result = await module.AddAsync([path], new Progress<HD2ModManager.Services.Ias.IasPageProgress>(), default);

		Assert.False(result.Success);
		Assert.StartsWith("添加「", result.Message);
		Assert.Contains("」失败：", result.Message);
		Assert.Empty(fixture.Service.GetRoles());
	}
#endif

	private const string ReShadeConflict = "游戏 bin/dxgi.dll 无法识别为 ReShade，可能属于其他 Mod。请先核对这个文件的来源；本次安装未覆盖任何文件。";
	private const string Dp8Helmet = "59d238094a164c88";

	[Fact]
	public async Task DirectPairChecksArchiveBeforeImport()
	{
		using var fixture = await Fixture.CreateAsync();
		fixture.SetArchiveOwner("sdkgroup.existing", enabled: true, Dp8Helmet);
		fixture.Worker.CandidateArchives = [Dp8Helmet];

		var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			fixture.Service.AddAsync(fixture.Pair, IasRoleSource.IasPackage, default));

		Assert.Equal($"与已启用角色『sdkgroup.existing』占用同一装备（{Dp8Helmet}），请先停用其中一个。", error.Message);
		Assert.Equal(new[] { "companion-archives", "archive-occupancy" }, fixture.Worker.Requests.Select(request => request.Command));
		Assert.False(Directory.Exists(fixture.Snapshot));
	}

	[Fact]
	public async Task DisabledOwnerAllowsAddButCannotBeEnabledOverNewOwner()
	{
		using var fixture = await Fixture.CreateAsync();
		fixture.Worker.RoleArchives["sdkgroup.role"] = [Dp8Helmet];
		var role = await fixture.Service.AddAsync(fixture.Pair, IasRoleSource.IasPackage, default);
		await fixture.Service.SetCharacterEnabledAsync(role.Id, false, default);
		Assert.Empty(await fixture.Service.GetArchiveOccupancyAsync(default));
		await fixture.Service.PreflightArchivesAsync([Dp8Helmet], default);
		fixture.SetArchiveOwner("sdkgroup.existing", enabled: true, Dp8Helmet);
		fixture.Worker.Requests.Clear();

		var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			fixture.Service.SetCharacterEnabledAsync(role.Id, true, default));

		Assert.Contains($"『sdkgroup.existing』占用同一装备（{Dp8Helmet}）", error.Message);
		Assert.Equal("archive-occupancy", Assert.Single(fixture.Worker.Requests).Command);
		Assert.False(Assert.Single(await fixture.Service.GetRolesWithStateAsync(default)).Enabled);
	}

	[Fact]
	public async Task ArchiveAcquiredAfterPreflightIsRejectedBeforeRegenerate()
	{
		using var fixture = await Fixture.CreateAsync();
		fixture.SetArchiveOwner("sdkgroup.existing", enabled: false, Dp8Helmet);
		fixture.Worker.CandidateArchives = [Dp8Helmet];
		fixture.Worker.RoleArchives["sdkgroup.role"] = [Dp8Helmet];
		await fixture.Service.PreflightArchivesAsync([Dp8Helmet], default);

		var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.AddAsync(fixture.Pair,
			IasRoleSource.PatchMod, default, (_, _) =>
			{
				fixture.SetArchiveOwner("sdkgroup.existing", enabled: true, Dp8Helmet);
				return Task.CompletedTask;
			}));

		Assert.Contains($"『sdkgroup.existing』占用同一装备（{Dp8Helmet}）", error.Message);
		Assert.Contains(fixture.Worker.Requests, request => request.Command == "import");
		Assert.DoesNotContain(fixture.Worker.Requests, request => request.Command == "regenerate");
		Assert.Empty(fixture.Service.GetRoles());
		Assert.Equal("sdkgroup.existing", Assert.Single(await fixture.Service.GetArchiveOccupancyAsync(default)).Id);
	}

	[Fact]
	public async Task OfficialArchiveFailureTranslatesOwnerAndEquipment()
	{
		using var fixture = await Fixture.CreateAsync();
		Directory.CreateDirectory(Path.GetDirectoryName(fixture.Paths.DbPath)!);
		using var readPool = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={fixture.Paths.DbPath};Mode=ReadOnly;Cache=Shared");
		using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={fixture.Paths.DbPath};Pooling=False"))
		{
			db.Open();
			using var command = db.CreateCommand();
			command.CommandText = $"""
				CREATE TABLE game_data_authority_kits (item_id, archive_id, display_name, kit_kind, set_id, dlc_id);
				INSERT INTO game_data_authority_kits VALUES (1, '{Dp8Helmet}', 'DP-8 头盔', 1, 0, 0);
				""";
			command.ExecuteNonQuery();
		}
		fixture.SetArchiveOwner("sdkgroup.existing", enabled: true, Dp8Helmet);
		fixture.Worker.RegenerateError = $"IAS worker regenerate 失败（退出码 1）：启用的 Mod 占用同一装备 Archive，请取消勾选其中一个：{Dp8Helmet}\n诊断日志：sandbox";

		try
		{
			var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
				fixture.Service.AddAsync(fixture.Pair, IasRoleSource.IasPackage, default));
			Assert.Equal($"与已启用角色『sdkgroup.existing』占用同一装备（DP-8 头盔 / {Dp8Helmet}），请先停用其中一个。", error.Message);
			Assert.Contains("诊断日志：sandbox", error.InnerException!.Message);
		}
		finally { Microsoft.Data.Sqlite.SqliteConnection.ClearPool(readPool); }
	}

#if WINDOWS
	[Fact]
	public async Task PatchUnitTargetIsCheckedBeforeCallingPackager()
	{
		using var fixture = await Fixture.CreateAsync();
		fixture.SetArchiveOwner("sdkgroup.existing", enabled: true, Dp8Helmet);
		var sdk = Path.Combine(fixture.Root, "sdk");
		var bindings = Directory.CreateDirectory(Path.Combine(sdk, "hd2_system")).FullName;
		File.WriteAllText(Path.Combine(bindings, "resource_binding_targets.json"), $$"""
			{"targets": {"{{Dp8Helmet}}": {"domain":"HELMET","equipment":[{"unit_ids":["a"]}]} } }
			""");
		var patch = Directory.CreateDirectory(Path.Combine(fixture.Root, "patch-input")).FullName;
		// 补丁文件名与实际 Unit 目标不同，预检应以转换器选出的 Archive 为准。
		using (var output = File.Create(Path.Combine(patch, "0000000000000002.patch_0")))
		using (var writer = new BinaryWriter(output))
		{
			writer.Write(0xF0000011u);
			writer.Write(1u);
			writer.Write(1u);
			writer.Write(new byte[60]);
			writer.Write(0ul);
			writer.Write(0xe0a48d0be9a7453ful);
			writer.Write(1ul);
			writer.Write(0ul);
			writer.Write(10ul);
			writer.Write(0xe0a48d0be9a7453ful);
			writer.Write(new byte[64]);
		}
		var calls = 0;
		var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			HD2ModManager.Services.Ias.IasModuleService.ConvertPatchWithPreflightAsync(fixture.Service, Directory.GetFiles(patch), sdk, () =>
			{
				calls++;
				throw new InvalidOperationException("不应进入打包器");
			}, default));

		Assert.Contains(Dp8Helmet, error.Message);
		Assert.Equal(0, calls);
	}
#endif

	[Fact]
	public async Task GraphicsRequiresIasAndReShadeAndReplacesTheManagedDataAfterEachChange()
	{
		using var fixture = await Fixture.CreateAsync();
		await fixture.Service.SetEnabledInActiveProfileAsync(false, default);
		var disabled = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SetReShadeAsync(true, default));
		Assert.Equal("请先打开 IAS。", disabled.Message);
		Assert.Empty(fixture.Worker.Requests);
		await fixture.Service.SetEnabledInActiveProfileAsync(true, default);
		var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SetDlssAsync(IasDlssMode.FrameGeneration, default));
		Assert.Equal("请先打开 ReShade 滤镜。", missing.Message);
		fixture.Worker.Requests.Clear();
		var deployments = fixture.DeployCalls;

		Assert.False(await fixture.Service.SetReShadeAsync(true, default));
		await fixture.Service.SetDlssAsync(IasDlssMode.FrameGeneration, default);
		await fixture.Service.SetDlssAsync(IasDlssMode.NeuralRendering, default);
		await fixture.Service.SetDlssAsync(IasDlssMode.Off, default);
		await fixture.Service.SetDlssAsync(IasDlssMode.FrameGeneration, default);
		Assert.True(await fixture.Service.SetReShadeAsync(false, default));
		Assert.False(await fixture.Service.SetReShadeAsync(false, default));

		Assert.Equal(new[] { "reshade-install", "regenerate", "dlss-install", "regenerate", "dlss-install", "regenerate",
			"dlss-remove", "regenerate", "dlss-install", "regenerate", "reshade-remove", "regenerate" },
			fixture.Worker.Requests.Where(request => request.Command != "graphics-status").Select(request => request.Command));
		Assert.Equal(new[] { "fg", "nr", "fg" }, fixture.Worker.Requests.Where(request => request.Command == "dlss-install")
			.Select(request => request.Argument));
		Assert.Equal(new IasGraphicsState(false, IasDlssMode.Off), await fixture.Service.GetGraphicsStateAsync(default));
		Assert.Equal(fixture.ManagedId, fixture.Service.ManagedModId);
		Assert.Equal(fixture.ManagedId, Assert.Single(await ActiveEntriesAsync(fixture)));
		Assert.Equal(deployments + 6, fixture.DeployCalls);
		Assert.False(File.Exists(Path.Combine(fixture.Game, "bin", "dxgi.dll")));
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task GraphicsRollsBackRuntimeManagedModAndCapeSettings(bool failInWorker)
	{
		using var fixture = await Fixture.CreateAsync();
		if (failInWorker)
			File.WriteAllText(Path.Combine(fixture.Game, "bin", "ShaderToggler.ini"), "existing cape settings");
		var before = Inventory(fixture.Runtime, fixture.IasLibrary, fixture.Paths.ModsDirectory, fixture.Game);
		fixture.Worker.FailRegenerate = failInWorker;
		fixture.FailProfileDeploy = !failInWorker;

		await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SetReShadeAsync(true, default));
		Assert.Equal(before, Inventory(fixture.Runtime, fixture.IasLibrary, fixture.Paths.ModsDirectory, fixture.Game));
		Assert.Equal(new IasGraphicsState(false, IasDlssMode.Off), await fixture.Service.GetGraphicsStateAsync(default));
	}

	[Fact]
	public async Task GraphicsRollsBackCascadeRemovalWhenRegenerationFails()
	{
		using var fixture = await Fixture.CreateAsync();
		await fixture.Service.SetReShadeAsync(true, default);
		await fixture.Service.SetDlssAsync(IasDlssMode.FrameGeneration, default);
		var before = Inventory(fixture.Runtime, fixture.IasLibrary, fixture.Paths.ModsDirectory, fixture.Game);
		fixture.Worker.FailRegenerate = true;
		await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SetReShadeAsync(false, default));

		Assert.Equal(before, Inventory(fixture.Runtime, fixture.IasLibrary, fixture.Paths.ModsDirectory, fixture.Game));
		Assert.Equal(new IasGraphicsState(true, IasDlssMode.FrameGeneration), await fixture.Service.GetGraphicsStateAsync(default));
	}

	[Fact]
	public async Task ReShadeConflictPreservesTheOfficialMessageAndGameDll()
	{
		using var fixture = await Fixture.CreateAsync();
		var dxgi = Path.Combine(fixture.Game, "bin", "dxgi.dll");
		File.WriteAllText(dxgi, "foreign ReShade");
		fixture.Worker.GraphicsError = ReShadeConflict;
		var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SetReShadeAsync(true, default));
		Assert.Equal(ReShadeConflict, error.Message);
		Assert.Equal("foreign ReShade", File.ReadAllText(dxgi));
		Assert.DoesNotContain(fixture.Worker.Requests, request => request.Command == "regenerate");
		Assert.Equal(0, fixture.DeployCalls);
	}

	[Fact]
	public async Task GraphicsRiskAcknowledgementSurvivesAServiceRestart()
	{
		using var fixture = await Fixture.CreateAsync();
		Assert.False(fixture.Service.GraphicsRiskAcknowledged);
		await fixture.Service.AcknowledgeGraphicsRiskAsync(default);
		var reopened = new IasDeploymentService(fixture.Paths, fixture.Game, fixture.Http, fixture.Worker, () => false);
		Assert.True(reopened.GraphicsRiskAcknowledged);
	}

	[Fact]
	public async Task ChangeRegeneratesOnceAndReplacesTheManagedModInPlace()
	{
		using var fixture = await Fixture.CreateAsync();
		var role = await fixture.Service.AddAsync(fixture.Pair, IasRoleSource.IasPackage, default);
		Assert.Equal("sdkgroup.role", role.Id);
		Assert.Equal(new[] { "companion-archives", "archive-occupancy", "import", "archive-occupancy", "regenerate", "status" },
			fixture.Worker.Requests.Select(request => request.Command));
		var initial = await fixture.Service.LoadModsAsync(default);
		Assert.Equal(2, initial.Nodes.Count);
		Assert.Equal(fixture.ManagedId, fixture.Service.ManagedModId);
		Assert.Equal(2, IasDeploymentService.Active(initial)!.Entries.Count);
		var managed = initial.Nodes[new(fixture.ManagedId)];
		Assert.Equal(fixture.ManagedRelativePath, managed.RelativePath);
		Assert.Contains("IAS 系统", managed.Metadata.Notes);
		Assert.Contains("IAS 角色", initial.Nodes[new(Assert.Single(role.ModIds))].Metadata.Notes);
		Assert.True(File.Exists(Path.Combine(fixture.Service.Sources(role.Id), "main", "a7c4c9d700000000.patch_0")));
		Assert.False(Directory.Exists(fixture.Snapshot));

		await fixture.Service.RemoveAsync(role.Id, default);

		Assert.Equal(new[] { "remove", "regenerate" }, fixture.Worker.Requests.Skip(6).Select(request => request.Command));
		var after = await fixture.Service.LoadModsAsync(default);
		Assert.Single(after.Nodes);
		Assert.Equal(fixture.ManagedId, Assert.Single(IasDeploymentService.Active(after)!.Entries).NodeId.Value);
		Assert.Equal(fixture.ManagedRelativePath, after.Nodes[new(fixture.ManagedId)].RelativePath);
		Assert.Empty(fixture.Service.GetRoles());
		Assert.False(Directory.Exists(fixture.Service.Sources(role.Id)));
	}

	[Fact]
	public async Task UninstallKeepingRolesTakesRoleModsOutOfTheProfile()
	{
		using var fixture = await Fixture.CreateAsync();
		var role = await fixture.Service.AddAsync(fixture.Pair, IasRoleSource.IasPackage, default);
		Assert.Contains(Assert.Single(role.ModIds), await ActiveEntriesAsync(fixture));
		var worker = Path.Combine(fixture.Paths.AppRootDirectory, "ias", "worker");
		Directory.CreateDirectory(worker);
		await File.WriteAllTextAsync(Path.Combine(worker, "WinRT.Runtime.dll"), "模拟按需下载的运行库");

		await fixture.Service.UninstallAsync(removeRoles: false, default);

		Assert.False(Directory.Exists(worker));
		Assert.Empty(await ActiveEntriesAsync(fixture));
		var nodes = (await fixture.Service.LoadModsAsync(default)).Nodes;
		Assert.Contains(nodes.Keys, id => id.Value == Assert.Single(role.ModIds));
		Assert.Single(fixture.Service.GetRoles());
	}

	[Fact]
	public async Task TotalToggleRestoresOnlyEnabledRolesAndKeepsFiles()
	{
		using var fixture = await Fixture.CreateAsync();
		var role = await fixture.Service.AddAsync(fixture.Pair, IasRoleSource.IasPackage, default);
		var before = Inventory(fixture.Runtime, fixture.IasLibrary);

		await fixture.Service.SetEnabledInActiveProfileAsync(false, default);
		Assert.Empty(await ActiveEntriesAsync(fixture));
		Assert.Equal(2, (await fixture.Service.LoadModsAsync(default)).Nodes.Count);
		Assert.Equal(before, Inventory(fixture.Runtime, fixture.IasLibrary));
		await fixture.Service.SetEnabledInActiveProfileAsync(true, default);
		Assert.Equal(new[] { fixture.ManagedId, Assert.Single(role.ModIds) }, await ActiveEntriesAsync(fixture));

		await fixture.Service.SetCharacterEnabledAsync(role.Id, false, default);
		await fixture.Service.SetEnabledInActiveProfileAsync(false, default);
		await fixture.Service.SetEnabledInActiveProfileAsync(true, default);
		Assert.Equal(fixture.ManagedId, Assert.Single(await ActiveEntriesAsync(fixture)));
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task RoleToggleRegeneratesManagedDataAndOnlyDeploysWhenIasIsOn(bool inProfile)
	{
		using var fixture = await Fixture.CreateAsync();
		var role = await fixture.Service.AddAsync(fixture.Pair, IasRoleSource.IasPackage, default);
		if (!inProfile) await fixture.Service.SetEnabledInActiveProfileAsync(false, default);
		var deployments = fixture.DeployCalls;
		var workerCalls = fixture.Worker.Requests.Count;

		await fixture.Service.SetCharacterEnabledAsync(role.Id, false, default);

		Assert.Equal(new[] { "set-enabled", "status", "regenerate" },
			fixture.Worker.Requests.Skip(workerCalls).Select(request => request.Command));
		Assert.False(Assert.Single(await fixture.Service.GetRolesWithStateAsync(default)).Enabled);
		Assert.True(Directory.Exists(fixture.Service.Sources(role.Id)));
		Assert.Equal(fixture.ManagedId, fixture.Service.ManagedModId);
		Assert.Equal("generated:0", File.ReadAllText(Path.Combine(fixture.Paths.ModsDirectory, fixture.ManagedRelativePath, "9ba626afa44a3aa3.patch_0")));
		Assert.Equal(inProfile ? new[] { fixture.ManagedId } : [], await ActiveEntriesAsync(fixture));
		Assert.Equal(deployments + (inProfile ? 1 : 0), fixture.DeployCalls);

		await fixture.Service.SetCharacterEnabledAsync(role.Id, true, default);
		Assert.True(Assert.Single(await fixture.Service.GetRolesWithStateAsync(default)).Enabled);
		Assert.Equal(inProfile ? new[] { fixture.ManagedId, Assert.Single(role.ModIds) } : [], await ActiveEntriesAsync(fixture));
		Assert.Equal(deployments + (inProfile ? 2 : 0), fixture.DeployCalls);
	}

	[Theory]
	[InlineData("total")]
	[InlineData("worker")]
	[InlineData("deployment")]
	public async Task ToggleFailureRestoresOfficialFlagsAndExactLibraryBytes(string failure)
	{
		using var fixture = await Fixture.CreateAsync();
		var role = await fixture.Service.AddAsync(fixture.Pair, IasRoleSource.IasPackage, default);
		var before = Inventory(fixture.Runtime, fixture.IasLibrary, fixture.Paths.ModsDirectory);
		fixture.Worker.FailRegenerate = failure == "worker";
		fixture.FailProfileDeploy = failure != "worker";

		await Assert.ThrowsAsync<InvalidOperationException>(() => failure == "total"
			? fixture.Service.SetEnabledInActiveProfileAsync(false, default)
			: fixture.Service.SetCharacterEnabledAsync(role.Id, false, default));

		Assert.Equal(before, Inventory(fixture.Runtime, fixture.IasLibrary, fixture.Paths.ModsDirectory));
		Assert.True(Assert.Single(await fixture.Service.GetRolesWithStateAsync(default)).Enabled);
		Assert.Equal(new[] { fixture.ManagedId, Assert.Single(role.ModIds) }, await ActiveEntriesAsync(fixture));
	}

	private static async Task<Guid[]> ActiveEntriesAsync(Fixture fixture) =>
		IasDeploymentService.Active(await fixture.Service.LoadModsAsync(default))!.Entries.Select(entry => entry.NodeId.Value).ToArray();

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task RollbackRestoresDeletedRuntimeFileAndExactLibraryBytes(bool failInWorker)
	{
		using var fixture = await Fixture.CreateAsync();
		var before = Inventory(fixture.Runtime, fixture.IasLibrary, fixture.Paths.ModsDirectory);
		fixture.Worker.FailRegenerate = failInWorker;
		fixture.FailProfileDeploy = !failInWorker;

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			fixture.Service.AddAsync(fixture.Pair, IasRoleSource.IasPackage, default));

		Assert.Equal(before, Inventory(fixture.Runtime, fixture.IasLibrary, fixture.Paths.ModsDirectory));
		Assert.False(Directory.Exists(fixture.Snapshot));
		Assert.Single((await fixture.Service.LoadModsAsync(default)).Nodes);
		Assert.Empty(fixture.Service.GetRoles());
		Assert.All(fixture.Worker.Requests, request =>
		{
			Assert.Equal(fixture.Game, request.GameRoot);
			Assert.Equal(fixture.IasLibrary, request.Library);
		});
		fixture.Worker.FailRegenerate = false;
		fixture.FailProfileDeploy = false;
		await fixture.Service.AddAsync(fixture.Pair, IasRoleSource.IasPackage, default);
		Assert.False(Directory.Exists(fixture.Snapshot));
		Assert.Single(fixture.Service.GetRoles());
	}

	[Fact]
	public async Task RunningGameBlocksEveryWriteBeforeSnapshotOrWorker()
	{
		using var fixture = await Fixture.CreateAsync();
		fixture.GameRunning = true;
		Func<Task>[] operations =
		[
			() => fixture.Service.InstallAsync(null, null, default),
			() => fixture.Service.UninstallAsync(true, default),
			() => fixture.Service.AddAsync(fixture.Pair, IasRoleSource.IasPackage, default),
			() => fixture.Service.RemoveAsync("role", default),
			() => fixture.Service.SetEnabledInActiveProfileAsync(false, default),
			() => fixture.Service.SetCharacterEnabledAsync("role", false, default),
			() => fixture.Service.SetReShadeAsync(true, default),
			() => fixture.Service.SetDlssAsync(IasDlssMode.Off, default),
			() => fixture.Service.AcknowledgeGraphicsRiskAsync(default),
			() => fixture.Service.ToStandardAsync("role", (_, _, _) => throw new InvalidOperationException(), default),
			() => fixture.Service.RecoverAsync(default)
		];
		foreach (var operation in operations)
			Assert.Equal(IasGameRunningException.Notice, (await Assert.ThrowsAsync<IasGameRunningException>(operation)).Message);

		Assert.False(Directory.Exists(fixture.Snapshot));
		Assert.False(fixture.Service.GraphicsRiskAcknowledged);
		Assert.Empty(fixture.Worker.Requests);
		Assert.Equal(0, fixture.DeployCalls);
	}

	[Fact]
	public async Task RollbackSkipsIdenticalFileWithAReadOnlyShare()
	{
		using var fixture = await Fixture.CreateAsync();
		var dll = Path.Combine(fixture.Runtime, "hd2ias_companion_launcher.dll");
		File.WriteAllText(dll, "mapped");
		using var readLock = File.Open(dll, FileMode.Open, FileAccess.Read, FileShare.Read);
		var before = Inventory(fixture.Runtime, fixture.IasLibrary, fixture.Paths.ModsDirectory);
		fixture.Worker.FailRegenerate = true;

		var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			fixture.Service.AddAsync(fixture.Pair, IasRoleSource.IasPackage, default));

		Assert.Equal("模拟更新运行目录后失败。", error.Message);
		Assert.Equal(before, Inventory(fixture.Runtime, fixture.IasLibrary, fixture.Paths.ModsDirectory));
		Assert.False(Directory.Exists(fixture.Snapshot));
	}

	[Fact]
	public async Task RollbackReportsLockedChangesAndNextWriteRecoversThroughANewService()
	{
		if (!OperatingSystem.IsWindows()) return;
		using var fixture = await Fixture.CreateAsync();
		var dll = Path.Combine(fixture.Runtime, "hd2ias_companion_launcher.dll");
		var second = Path.Combine(fixture.Runtime, "another.dll");
		File.WriteAllText(dll, "old");
		File.WriteAllText(second, "old");
		var before = Inventory(fixture.Runtime, fixture.IasLibrary, fixture.Paths.ModsDirectory);
		fixture.Worker.FailRegenerate = true;

		var locks = new List<FileStream>();
		try
		{
			fixture.Worker.BeforeRegenerateFailure = () =>
			{
				foreach (var path in new[] { dll, second })
				{
					File.WriteAllText(path, "new");
					locks.Add(File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read));
				}
			};
			var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
				fixture.Service.AddAsync(fixture.Pair, IasRoleSource.IasPackage, default));
			Assert.Contains("IAS 恢复失败，快照已保留", error.Message);
			Assert.Contains(dll, error.Message);
			Assert.Contains(second, error.Message);
			Assert.Contains("诊断日志", error.Message);
			var log = Assert.Single(Directory.GetFiles(Path.Combine(fixture.Ias, "logs")));
			Assert.Contains(dll, File.ReadAllText(log));
			Assert.Contains(second, File.ReadAllText(log));
			Assert.True(Directory.Exists(fixture.Snapshot));
			Assert.False(File.Exists(Path.Combine(fixture.Runtime, "new")));
			Assert.True(File.Exists(Path.Combine(fixture.Runtime, "permanent")));
			Assert.Empty(fixture.Service.GetRoles());
			fixture.GameRunning = true;
			await Assert.ThrowsAsync<IasGameRunningException>(() => fixture.Service.RecoverAsync(default));
			Assert.True(Directory.Exists(fixture.Snapshot));
		}
		finally
		{
			foreach (var handle in locks) handle.Dispose();
		}

		var reopened = new IasDeploymentService(fixture.Paths, fixture.Game, fixture.Http, fixture.Worker, () => false);
		await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.RemoveAsync("不存在", default));
		Assert.Equal(before, Inventory(fixture.Runtime, fixture.IasLibrary, fixture.Paths.ModsDirectory));
		Assert.False(Directory.Exists(fixture.Snapshot));
	}

	[Fact]
	public async Task LegacyRuntimeSnapshotIsRestoredBeforeTheNextWrite()
	{
		using var fixture = await Fixture.CreateAsync();
		var missing = Path.Combine(fixture.Runtime, "permanent");
		var copy = Path.Combine(fixture.Snapshot, "Runtime", "fake-game", "permanent");
		Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
		File.Move(missing, copy);
		await fixture.Service.AcknowledgeGraphicsRiskAsync(default);
		Assert.Equal("恢复永久删除内容", File.ReadAllText(missing));
		Assert.True(fixture.Service.GraphicsRiskAcknowledged);
		Assert.False(Directory.Exists(fixture.Snapshot));
	}

	[Fact]
	public async Task LegacySnapshotWithUnknownTargetsKeepsCopiesAndReportsBothFailures()
	{
		using var fixture = await Fixture.CreateAsync();
		var oldNode = Path.Combine(fixture.Snapshot, "manager", "nodes", "2", "patch");
		var oldShader = Path.Combine(fixture.Snapshot, "game", "ShaderToggler.ini");
		Directory.CreateDirectory(Path.GetDirectoryName(oldNode)!);
		Directory.CreateDirectory(Path.GetDirectoryName(oldShader)!);
		File.WriteAllText(oldNode, "patch");
		File.WriteAllText(oldShader, "shader");

		var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RecoverAsync(default));

		Assert.Contains("管理器 Mod 节点原路径", error.Message);
		Assert.Contains("原游戏目录", error.Message);
		Assert.Contains("诊断日志", error.Message);
		Assert.True(File.Exists(oldNode));
		Assert.True(File.Exists(oldShader));
		Assert.False(File.Exists(Path.Combine(fixture.Game, "bin", "ShaderToggler.ini")));
		Assert.Empty(fixture.Worker.Requests);
	}

	[Fact]
	public async Task ClassifiesZipDirectoriesWithoutInspectingPayloadsAndGroupsOneRole()
	{
		using var fixture = await Fixture.CreateAsync();
		Assert.Equal(IasInputKind.PatchMod, IasPackageDetector.Classify(fixture.Pair[0]));
		Assert.Equal(IasInputKind.IasPackage, IasPackageDetector.Classify(fixture.Pair[1]));
		var vpk = Path.Combine(fixture.Root, "sample.vpk");
		File.WriteAllBytes(vpk, []);
		Assert.Equal(IasInputKind.L4d2Vpk, IasPackageDetector.Classify(vpk));
		Assert.Equal(IasInputKind.Unsupported, IasPackageDetector.Classify(Path.Combine(fixture.Root, "missing.zip")));

		var combined = Path.Combine(fixture.Root, "combined.zip");
		using (var archive = ZipFile.Open(combined, ZipArchiveMode.Create))
			foreach (var part in fixture.Pair) archive.CreateEntryFromFile(part, Path.GetFileName(part));
		Assert.Equal(IasInputKind.IasPackage, IasPackageDetector.Classify(combined));
#if WINDOWS
		var module = new HD2ModManager.Services.Ias.IasModuleService(() => fixture.Paths, () => fixture.Game, fixture.Http);
		var classification = module.Classify(fixture.Pair);
		Assert.Equal(1, classification.Supported);
		Assert.Equal(0, classification.Unsupported);
#endif
		using var workspace = new IasDropWorkspace();
		var prepared = await workspace.InspectAsync(combined, default);
		await fixture.Service.AddPreparedAsync([prepared], default);
		Assert.Single(fixture.Service.GetRoles());
	}

	[Fact]
	public async Task ManifestRoleOptionSurvivesWindowRedeploymentAndLaterOptionWrites()
	{
		using var fixture = await Fixture.CreateAsync(shareOptionActivations: true);
		var windowOptions = fixture.SharedOptions!;
		fixture.Worker.UseEmptyDataPatch = true;
		await using var reader = CoreServices.CreateModInformationReader();
		await using var center = CoreServices.CreateModInformationCenter(fixture.Paths, reader);
		var executor = new RecordingExecutor();
		ProfileApplyService CreateApply(OptionActivationStore options) => new(center, CoreServices.CreateApplyPlanner(),
			executor, paths: fixture.Paths, optionActivations: options);
		var windowApply = CreateApply(windowOptions);
		var main = WriteManifestRole(fixture.Root);
		var preview = Path.Combine(fixture.Root, "preflight");
		IasFileTree.Extract(main, preview, default);
		var plan = await CoreServices.CreateModImportPlanner().PlanFolderAsync(preview);
		Assert.Single(plan.Items, item => item.Role == ImportPlanItemRole.PlaceholderHost);
		Assert.DoesNotContain(plan.Items, item => item.Role == ImportPlanItemRole.Host);
		Assert.Contains(plan.Items, item => item.Role == ImportPlanItemRole.Option);

		var role = await fixture.Service.AddAsync([main, fixture.Pair[1]], IasRoleSource.IasPackage, default);

		var snapshot = await fixture.Service.LoadModsAsync(default);
		var host = snapshot.Nodes[new(Assert.Single(role.ModIds))];
		Assert.True(host.Metadata.IsLogicalContainer);
		Assert.Contains(IasDeploymentService.Active(snapshot)!.Entries, entry => entry.NodeId == host.Id);
		var option = Assert.Single(OptionRelationshipGraph.Create(snapshot).GetDirectOptions(host.Id));
		Assert.True(File.Exists(Path.Combine(fixture.Paths.ModsDirectory, option.RelativePath, ManifestRolePatch)));

		var optionsPath = Path.Combine(fixture.Paths.ModsDirectory, "option-activations.json");
		var profile = IasDeploymentService.Active(snapshot)!;
		var gameData = Path.Combine(fixture.Game, "data");
		var freshOptions = CoreServices.CreateOptionActivationStore(fixture.Paths);
		Assert.True(freshOptions.CreateSnapshot().IsEnabled(option.Id.Value.ToString("N"), host.Id.Value.ToString("N")));
		var freshResult = await CreateApply(freshOptions).ApplyAsync(profile, snapshot, fixture.Paths.ModsDirectory, gameData);
		Assert.True(freshResult.Success, string.Join("；", freshResult.Issues.Select(issue => $"{issue.Code}: {issue.Message}")));
		Assert.Contains(executor.Plan!.Operations, operation => operation.NodeId == option.Id && Path.GetFileName(operation.SourcePath) == ManifestRolePatch);

		var result = await windowApply.ApplyAsync(profile, snapshot, fixture.Paths.ModsDirectory, gameData);
		Assert.True(result.Success, string.Join("；", result.Issues.Select(issue => $"{issue.Code}: {issue.Message}")));
		Assert.Contains(executor.Plan!.Operations, operation => operation.NodeId == option.Id && Path.GetFileName(operation.SourcePath) == ManifestRolePatch);

		// 模拟窗口保存另一个选项时重写整份激活文件。
		var otherOption = Guid.NewGuid().ToString("N");
		var otherHost = Guid.NewGuid().ToString("N");
		await windowOptions.SetEnabledAsync(otherOption, otherHost, false);
		var saved = await OptionActivationStore.TryLoadReadOnlySnapshotAsync(optionsPath);
		Assert.True(saved.IsEnabled(option.Id.Value.ToString("N"), host.Id.Value.ToString("N")));
		Assert.True(saved.HasState(otherOption, otherHost));
	}

	[Fact]
	public async Task StandardConversionAndRoleRemovalKeepWindowOptionsInSyncIncludingRollback()
	{
		using var fixture = await Fixture.CreateAsync(shareOptionActivations: true);
		var options = fixture.SharedOptions!;
		var role = await fixture.Service.AddAsync([WriteManifestRole(fixture.Root), fixture.Pair[1]], IasRoleSource.IasPackage, default);
		var roleHost = new ModNodeId(Assert.Single(role.ModIds));
		var roleOption = Assert.Single(OptionRelationshipGraph.Create(await fixture.Service.LoadModsAsync(default)).GetDirectOptions(roleHost));
		var optionKey = roleOption.Id.Value.ToString("N");
		var hostKey = roleHost.Value.ToString("N");
		var optionsPath = Path.Combine(fixture.Paths.ModsDirectory, "option-activations.json");
		var beforeToggles = File.ReadAllBytes(optionsPath);
		await fixture.Service.SetCharacterEnabledAsync(role.Id, false, default);
		await fixture.Service.SetCharacterEnabledAsync(role.Id, true, default);
		await fixture.Service.SetEnabledInActiveProfileAsync(false, default);
		await fixture.Service.SetEnabledInActiveProfileAsync(true, default);
		Assert.Equal(beforeToggles, File.ReadAllBytes(optionsPath));
		Assert.True(options.CreateSnapshot().IsEnabled(optionKey, hostKey));

		var deployments = fixture.DeployCalls;
		var standardId = await fixture.Service.ToStandardAsync(role.Id, (_, output, token) =>
		{
			IasFileTree.Extract(WriteManifestRole(fixture.Root, "Standard.zip", Guid.NewGuid()), output, token);
			return Task.FromResult(output);
		}, default);
		Assert.Equal(deployments, fixture.DeployCalls);
		var standardOption = Assert.Single(OptionRelationshipGraph.Create(await fixture.Service.LoadModsAsync(default)).GetDirectOptions(new(standardId)));
		var standardOptionKey = standardOption.Id.Value.ToString("N");
		var standardHostKey = standardId.ToString("N");
		Assert.True(options.CreateSnapshot().IsEnabled(standardOptionKey, standardHostKey));

		var before = File.ReadAllBytes(optionsPath);
		fixture.FailProfileDeploy = true;
		await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RemoveAsync(role.Id, default));
		Assert.Equal(before, File.ReadAllBytes(optionsPath));
		Assert.True(options.CreateSnapshot().IsEnabled(optionKey, hostKey));
		Assert.True(options.CreateSnapshot().IsEnabled(standardOptionKey, standardHostKey));

		fixture.FailProfileDeploy = false;
		await fixture.Service.RemoveAsync(role.Id, default);
		Assert.False(options.HasState(optionKey, hostKey));
		await options.SetEnabledAsync(standardOptionKey, standardHostKey, false);
		var saved = await OptionActivationStore.TryLoadReadOnlySnapshotAsync(optionsPath);
		Assert.False(saved.HasState(optionKey, hostKey));
		Assert.True(saved.HasState(standardOptionKey, standardHostKey));
		Assert.False(saved.IsEnabled(standardOptionKey, standardHostKey));
	}

	[Fact]
	public async Task StandardConversionTimingWithTwelveSecondPreparation()
	{
		using var fixture = await Fixture.CreateAsync();
		var role = await fixture.Service.AddAsync(fixture.Pair, IasRoleSource.IasPackage, default);
		using (IasTiming.Begin("转为普通 Mod"))
			await fixture.Service.ToStandardAsync(role.Id, async (_, output, token) =>
			{
				await Task.Delay(TimeSpan.FromSeconds(12), token);
				IasFileTree.Extract(WriteManifestRole(fixture.Root, "Standard.zip", Guid.NewGuid()), output, token);
				return output;
			}, default);
		var log = Path.Combine(fixture.Paths.AppRootDirectory, "logs", "ias-timing.log");
		if (File.Exists(log))
			foreach (var line in File.ReadLines(log).Where(line => line.Contains("\t转为普通 Mod\t")))
				Console.WriteLine(line);
	}

	[Theory]
	[InlineData("delete")]
	[InlineData("edit")]
	[InlineData("uninstall")]
	public async Task StandardConversionDoesNotLeaseDuringPreparationAndRejectsChangedSourceBeforeImport(string change)
	{
		using var fixture = await Fixture.CreateAsync();
		var role = await fixture.Service.AddAsync(fixture.Pair, IasRoleSource.IasPackage, default);
		var stageReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var finishConversion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		var conversion = fixture.Service.ToStandardAsync(role.Id, async (_, output, token) =>
		{
			IasFileTree.Extract(WriteManifestRole(fixture.Root, "Standard.zip", Guid.NewGuid()), output, token);
			stageReady.TrySetResult();
			await finishConversion.Task.WaitAsync(token);
			return output;
		}, timeout.Token);

		await stageReady.Task.WaitAsync(timeout.Token);
		var source = fixture.Service.Sources(role.Id);
		if (change == "uninstall")
			await fixture.Service.RemoveAsync(role.Id, timeout.Token);
		else
		{
			await using var lease = await CoreServices.CreateLibraryWriteLeaseProvider()
				.AcquireAsync(fixture.Paths.ModsDirectory, timeout.Token);
			if (change == "delete") IasFileTree.Delete(source);
			else
			{
				var patch = Path.Combine(source, "main", ManifestRolePatch);
				var timestamp = File.GetLastWriteTimeUtc(patch);
				File.WriteAllText(patch, "evil-patch");
				File.SetLastWriteTimeUtc(patch, timestamp);
			}
		}
		var before = Inventory(fixture.Paths.ModsDirectory);
		finishConversion.SetResult();
		var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await conversion);
		Assert.Contains(change switch { "delete" => "已删除", "edit" => "已改动", _ => "已卸载" }, error.Message);
		Assert.Equal(before, Inventory(fixture.Paths.ModsDirectory));
		Assert.False(Directory.Exists(fixture.Snapshot));
		Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(fixture.Ias, "temp")));
	}

	[Fact]
	public async Task CancellingStandardConversionRemovesTemporaryOutput()
	{
		using var fixture = await Fixture.CreateAsync();
		var role = await fixture.Service.AddAsync(fixture.Pair, IasRoleSource.IasPackage, default);
		var before = Inventory(fixture.Paths.ModsDirectory);
		using var cancellation = new CancellationTokenSource();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			fixture.Service.ToStandardAsync(role.Id, (_, output, token) =>
			{
				Directory.CreateDirectory(output);
				File.WriteAllText(Path.Combine(output, "partial"), "testing cancellation");
				cancellation.Cancel();
				return Task.FromCanceled<string>(token);
			}, cancellation.Token));
		Assert.Equal(before, Inventory(fixture.Paths.ModsDirectory));
		Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(fixture.Ias, "temp")));
	}

	private const string ManifestRolePatch = "a7c4c9d700000000.patch_0";

	private static string WriteManifestRole(string root, string name = "Manifest_IAS_MainMod.zip", Guid? id = null)
	{
		var path = Path.Combine(root, name);
		using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
		Write(zip, "manifest.json", $$"""
			{
			  "Version": 1,
			  "Guid": "{{id ?? Guid.Parse("949ac530-c171-53cc-bf6f-e12dfed34ac2")}}",
			  "Name": "Manifest role",
			  "Options": [{ "Name": "本体（必选）", "Include": ["本体"] }]
			}
			""");
		WriteEmptyPatch(zip, $"本体/{ManifestRolePatch}");
		return path;
	}

	[Fact]
	public async Task RemovingFirstRoleKeepsTheSecondRoleAndItsMainMod()
	{
		using var fixture = await Fixture.CreateAsync();
		var first = await fixture.Service.AddAsync(fixture.Pair, IasRoleSource.IasPackage, default);
		var secondCompanion = Path.Combine(fixture.Root, "Second_IAS_Companion.hd2irpack.zip");
		using (var zip = ZipFile.Open(secondCompanion, ZipArchiveMode.Create))
			Write(zip, "mod_group.json", "{\"name\":\"Second\"}");
		var second = await fixture.Service.AddAsync([fixture.Pair[0], secondCompanion], IasRoleSource.IasPackage, default);

		await fixture.Service.RemoveAsync(first.Id, default);

		Assert.Equal(second.Id, Assert.Single(fixture.Service.GetRoles()).Id);
		var snapshot = await fixture.Service.LoadModsAsync(default);
		Assert.Equal(2, snapshot.Nodes.Count);
		Assert.Contains(new ModNodeId(Assert.Single(second.ModIds)), snapshot.Nodes.Keys);
		Assert.Equal(fixture.ManagedId, fixture.Service.ManagedModId);
		Assert.Equal(3, fixture.Worker.Requests.Count(request => request.Command == "regenerate"));
	}

	private static string[] Inventory(params string[] roots) =>
		roots.SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
			.Order(StringComparer.Ordinal).Select(path => $"{Path.GetRelativePath(root, path)}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}"))
			.ToArray();

	private sealed class RecordingExecutor : IApplyExecutor
	{
		internal ApplyPlan? Plan { get; private set; }

		public ValueTask<ApplyResult> ExecuteAsync(ApplyPlan plan, CancellationToken cancellationToken = default)
		{
			Plan = plan;
			return ValueTask.FromResult(new ApplyResult(true, [], null, plan.Issues));
		}

		public ValueTask<ApplyResult> DeactivateAsync(string gameDataDirectory, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();
	}

	private sealed class Fixture : IDisposable
	{
		internal string Root { get; } = Path.Combine(Path.GetTempPath(), "ias-deploy-tests", Guid.NewGuid().ToString("N"));
		internal StoragePaths Paths => new(Path.Combine(Root, "manager"));
		internal string Game => Path.Combine(Root, "game");
		internal string Ias => Path.Combine(Paths.AppRootDirectory, "ias");
		internal string Runtime => Path.Combine(Ias, "Runtime", "fake-game");
		internal string IasLibrary => Path.Combine(Ias, "library");
		internal string Snapshot => Path.Combine(Ias, "snapshots", "previous");
		internal string ManagedRelativePath => "existing-ias-data";
		internal Guid ManagedId { get; } = Guid.NewGuid();
		internal HttpClient Http { get; } = new();
		internal bool FailProfileDeploy { get; set; }
		internal bool GameRunning { get; set; }
		internal int DeployCalls { get; private set; }
		internal FakeWorker Worker { get; private set; } = null!;
		internal OptionActivationStore? SharedOptions { get; private set; }
		internal IasDeploymentService Service { get; private set; } = null!;
		internal string[] Pair => [Path.Combine(Root, "Sample_IAS_MainMod.zip"), Path.Combine(Root, "Sample_IAS_Companion.hd2irpack.zip")];

		internal void SetArchiveOwner(string id, bool enabled, params string[] archives)
		{
			Worker.RoleArchives[id] = archives;
			File.WriteAllText(Path.Combine(IasLibrary, id + ".json"), enabled ? "true" : "false");
		}

		internal static async Task<Fixture> CreateAsync(bool shareOptionActivations = false)
		{
			var fixture = new Fixture();
			try
			{
				fixture.Worker = new(fixture.Runtime);
				if (shareOptionActivations) fixture.SharedOptions = CoreServices.CreateOptionActivationStore(fixture.Paths);
				fixture.Service = new(fixture.Paths, fixture.Game, fixture.Http, fixture.Worker, () => fixture.GameRunning, (_, _, _) =>
				{
					fixture.DeployCalls++;
					return fixture.FailProfileDeploy ? Task.FromException(new InvalidOperationException("模拟部署失败。")) : Task.CompletedTask;
				}, fixture.SharedOptions);
				foreach (var path in new[] { fixture.Runtime, fixture.IasLibrary, Path.Combine(fixture.Paths.ModsDirectory, fixture.ManagedRelativePath) })
					Directory.CreateDirectory(path);
				File.WriteAllText(Path.Combine(fixture.Runtime, "permanent"), "恢复永久删除内容");
				File.WriteAllText(Path.Combine(fixture.IasLibrary, "managed-mod.json"), JsonSerializer.Serialize(fixture.ManagedId));
				File.WriteAllText(Path.Combine(fixture.Paths.ModsDirectory, fixture.ManagedRelativePath, "9ba626afa44a3aa3.patch_0"), "original");
				var components = Path.Combine(fixture.Ias, "components");
				Directory.CreateDirectory(Path.Combine(components, "1.6.4", "ExternalRuntime"));
				File.WriteAllText(Path.Combine(components, "current.json"), "{\"version\":\"1.6.4\"}");
				var host = new Dictionary<string, string>();
				foreach (var relative in new[] { "bin/helldivers2.exe", "data/game/game.dll" })
				{
					var path = Path.Combine(fixture.Game, relative);
					Directory.CreateDirectory(Path.GetDirectoryName(path)!);
					File.WriteAllText(path, relative);
					host[relative] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
				}
				using (var zip = ZipFile.Open(Path.Combine(components, "1.6.4", "ExternalRuntime", "ArmorBase.zip"), ZipArchiveMode.Create))
					Write(zip, "base.json", JsonSerializer.Serialize(new { schema = "HD2ArmorBase1", host }));
				foreach (var name in new[] { "ReShade", "DLSSFrameGeneration", "DLSSNeuralRendering" })
				{
					var expanded = Directory.CreateDirectory(Path.Combine(components, "1.6.4", "expanded", name)).FullName;
					File.WriteAllText(Path.Combine(expanded, "external-runtime.json"), "{}");
				}
				using (var zip = ZipFile.Open(fixture.Pair[0], ZipArchiveMode.Create))
					Write(zip, "a7c4c9d700000000.patch_0", "main-patch");
				using (var zip = ZipFile.Open(fixture.Pair[1], ZipArchiveMode.Create))
					Write(zip, "mod_group.json", "{\"name\":\"Sample\"}");
				var id = new ModNodeId(fixture.ManagedId);
				var profile = new Profile(ProfileId.New(), "Test", DateTimeOffset.UtcNow, null,
					[new(id, 0, DateTimeOffset.UtcNow)]);
				var node = new ModNode(id, fixture.ManagedRelativePath,
					new("IAS 系统", null, DateTimeOffset.UtcNow, null), [new("9ba626afa44a3aa3", 0)], []);
				await new JsonModLibraryStore(fixture.Paths, allowLegacyMigration: false)
					.SaveAsync(new(1, DateTimeOffset.UtcNow, new Dictionary<ModNodeId, ModNode> { [id] = node }, [profile], profile.Id));
				return fixture;
			}
			catch (Exception)
			{
				fixture.Dispose();
				throw;
			}
		}

		public void Dispose()
		{
			Http.Dispose();
			IasFileTree.Delete(Root);
		}
	}

	private sealed class FakeWorker(string runtime) : IIasWorker
	{
		internal List<IasWorkerRequest> Requests { get; } = [];
		internal bool FailRegenerate { get; set; }
		internal Action? BeforeRegenerateFailure { get; set; }
		internal bool UseEmptyDataPatch { get; set; }
		internal string? GraphicsError { get; set; }
		internal string[] CandidateArchives { get; set; } = [];
		internal Dictionary<string, string[]> RoleArchives { get; } = new(StringComparer.Ordinal);
		internal string? RegenerateError { get; set; }
		private string GraphicsPath => Path.Combine(runtime, "graphics.json");
		private IasGraphicsState ReadGraphics() => File.Exists(GraphicsPath)
			? JsonSerializer.Deserialize<IasGraphicsState>(File.ReadAllText(GraphicsPath))! : new(false, IasDlssMode.Off);

		public Task<IasWorkerResponse> RunAsync(IasWorkerRequest request, CancellationToken cancellationToken)
		{
			// 走一遍与进程相同的 JSON 边界。
			request = JsonSerializer.Deserialize<IasWorkerRequest>(JsonSerializer.Serialize(request))!;
			Requests.Add(request);
			switch (request.Command)
			{
				case "companion-archives":
					Assert.True(File.Exists(request.Argument));
					return Task.FromResult(new IasWorkerResponse(true, JsonSerializer.Serialize(CandidateArchives)));
				case "archive-occupancy":
					var owners = Directory.GetFiles(request.Library, "sdkgroup.*.json").Select(file =>
					{
						var id = Path.GetFileNameWithoutExtension(file);
						return new IasArchiveOwner(id, id, bool.Parse(File.ReadAllText(file)), RoleArchives.GetValueOrDefault(id) ?? []);
					}).Where(owner => owner.Enabled || owner.Id == request.Argument);
					return Task.FromResult(new IasWorkerResponse(true, JsonSerializer.Serialize(owners)));
				case "graphics-status":
					return Task.FromResult(new IasWorkerResponse(true, JsonSerializer.Serialize(ReadGraphics())));
				case "reshade-install":
					if (GraphicsError is { } error) return Task.FromResult(new IasWorkerResponse(false, Error: error));
					File.WriteAllText(Path.Combine(request.GameRoot, "bin", "ShaderToggler.ini"), "new cape settings");
					File.WriteAllText(GraphicsPath, JsonSerializer.Serialize(ReadGraphics() with { ReShadeEnabled = true }));
					return Task.FromResult(new IasWorkerResponse(true));
				case "reshade-remove":
					File.Delete(GraphicsPath);
					return Task.FromResult(new IasWorkerResponse(true));
				case "dlss-install":
				case "dlss-remove":
					var mode = request.Argument switch
					{
						"fg" => IasDlssMode.FrameGeneration,
						"nr" => IasDlssMode.NeuralRendering,
						null when request.Command == "dlss-remove" => IasDlssMode.Off,
						_ => throw new InvalidOperationException(request.Argument)
					};
					File.WriteAllText(GraphicsPath, JsonSerializer.Serialize(ReadGraphics() with { Dlss = mode }));
					return Task.FromResult(new IasWorkerResponse(true));
				case "import":
					Assert.True(File.Exists(request.Argument));
					using (var zip = ZipFile.OpenRead(request.Argument!))
					using (var entry = zip.GetEntry("mod_group.json")!.Open())
					using (var group = JsonDocument.Parse(entry))
					{
						var id = group.RootElement.GetProperty("name").GetString() == "Second" ? "sdkgroup.second" : "sdkgroup.role";
						File.WriteAllText(Path.Combine(request.Library, id + ".json"), "true");
						return Task.FromResult(new IasWorkerResponse(true, id));
					}
				case "remove":
					File.Delete(Path.Combine(request.Library, request.Argument + ".json"));
					return Task.FromResult(new IasWorkerResponse(true));
				case "set-enabled":
					File.WriteAllText(Path.Combine(request.Library, request.Argument + ".json"), request.Enabled ? "true" : "false");
					return Task.FromResult(new IasWorkerResponse(true));
				case "status":
					var entries = Directory.GetFiles(request.Library, "sdkgroup.*.json").Select(file => new
					{
						id = Path.GetFileNameWithoutExtension(file),
						enabled = bool.Parse(File.ReadAllText(file))
					});
					return Task.FromResult(new IasWorkerResponse(true, JsonSerializer.Serialize(new { Library = new { entries } })));
				case "regenerate":
					if (RegenerateError is not null)
						return Task.FromResult(new IasWorkerResponse(false, Error: RegenerateError));
					File.Delete(Path.Combine(runtime, "permanent"));
					File.WriteAllText(Path.Combine(runtime, "new"), "replacement");
					if (FailRegenerate)
					{
						BeforeRegenerateFailure?.Invoke();
						return Task.FromResult(new IasWorkerResponse(false, Error: "模拟更新运行目录后失败。"));
					}
					var path = Path.Combine(request.Export, "data.zip");
					using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
					{
						var enabledCount = Directory.GetFiles(request.Library, "sdkgroup.*.json").Count(file => File.ReadAllText(file) == "true");
						if (UseEmptyDataPatch) WriteEmptyPatch(zip, "data/9ba626afa44a3aa3.patch_0");
						else Write(zip, "data/9ba626afa44a3aa3.patch_0", $"generated:{enabledCount}");
					}
					return Task.FromResult(new IasWorkerResponse(true, path));
				default:
					throw new InvalidOperationException(request.Command);
			}
		}
	}

	private static void Write(ZipArchive archive, string name, string text)
	{
		using var writer = new StreamWriter(archive.CreateEntry(name).Open());
		writer.Write(text);
	}

	private static void WriteEmptyPatch(ZipArchive archive, string name)
	{
		using var entry = archive.CreateEntry(name).Open();
		using var writer = new BinaryWriter(entry);
		writer.Write(0xF0000011u);
		writer.Write(new byte[68]);
	}
}

