using HD2ModCore.Ias;
using HD2ModCore.Infrastructure;
using HD2ModManager.Services.Ias;

namespace HD2ModCli;

internal static class CliIasModuleCommands
{
	internal static async Task<int> RunAsync(string command, CliInvocation invocation)
	{
		var args = invocation.Command.Skip(2).ToArray();
		var options = invocation.Options;
		if (options.DryRun || options.Output is not null || options.Plan is not null || options.AddToProfile is not null)
			throw new CliUserException("IasArguments", "IAS 部署命令不接受 --dry-run、--output、--plan 或 --add-to-profile。");
		if (args is ["help"])
		{
			Console.WriteLine("hd2mod ias install|uninstall|add <路径...>|remove <id>|list|to-standard <id> --manager-root <目录> --game-root <目录> [--offline-package <ZIP>] [--remove-characters] [--json]");
			Console.WriteLine("hd2mod ias enable|disable --manager-root <目录> --game-root <目录> [--json]");
			Console.WriteLine("hd2mod ias role enable|disable <id> --manager-root <目录> --game-root <目录> [--json]");
			Console.WriteLine("hd2mod ias reshade on|off --manager-root <目录> --game-root <目录> [--json]");
			Console.WriteLine("hd2mod ias dlss off|fg|nr --manager-root <目录> --game-root <目录> [--json]");
			Console.WriteLine("hd2mod ias to-standard <id> --manager-root <目录> --game-root <目录> [--target-armor <archive|itemId>] [--target-helmet <archive|itemId>] [--merge-helmet]");
			Console.WriteLine("hd2mod ias recover --manager-root <目录> --game-root <目录> [--json]");
			return 0;
		}
		if (command is ("remove" or "to-standard") && args.Length != 1 ||
			command == "add" && args.Length == 0 ||
			command is ("install" or "uninstall" or "list" or "enable" or "disable" or "recover") && args.Length != 0)
			throw new CliUserException("IasArguments", $"ias {command} 的参数数量不正确。");
		if (command == "role" && args is not [("enable" or "disable"), _])
			throw new CliUserException("IasArguments", "用法：ias role enable|disable <id>。");
		if (command == "reshade" && args is not [("on" or "off")])
			throw new CliUserException("IasArguments", "用法：ias reshade on|off。");
		if (command == "dlss" && args is not [("off" or "fg" or "nr")])
			throw new CliUserException("IasArguments", "用法：ias dlss off|fg|nr。");
		var gameData = options.IasGameRoot is { } root ? Path.Combine(Path.GetFullPath(root), "data") : options.GameData;
		var workspace = CliWorkspace.Open(options with { GameData = gameData });
		var manager = workspace.ManagerRoot ?? throw new CliUserException("IasManagerRoot", "请指定 --manager-root。");
		var paths = new StoragePaths(manager, workspace.LibraryRoot);
		var game = workspace.GameDataRoot is { } data
			? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(data))) : null;
		using var http = IasUpdateHttp.Create(() => IasUpdateHttp.ReadSettings(manager));
		var service = new IasModuleService(() => paths, () => game, http);
		using var cancellation = new CancellationTokenSource();
		ConsoleCancelEventHandler cancel = (_, args) => { args.Cancel = true; cancellation.Cancel(); };
		Console.CancelKeyPress += cancel;
		try
		{
			var token = cancellation.Token;
			if (command == "list")
			{
				var roles = await service.GetCharactersAsync(token).ConfigureAwait(false);
				if (options.Json) CliOutput.WriteJson(roles);
				else if (roles.Count == 0) Console.WriteLine("尚无 IAS 角色。");
				else foreach (var role in roles)
					Console.WriteLine($"{role.Id}  {role.Name}{(role.Enabled == false ? "（已停用）" : "")}");
				return 0;
			}

			var progress = new CommandProgress();
			var result = command switch
			{
				"recover" => await RecoverAsync(service, token).ConfigureAwait(false),
				"install" => await service.InstallAsync(options.IasOfflinePackage, progress, token).ConfigureAwait(false),
				"uninstall" => await service.UninstallAsync(options.IasRemoveCharacters, progress, token).ConfigureAwait(false),
				"enable" or "disable" => await service.SetEnabledInActiveProfileAsync(command == "enable", progress, token).ConfigureAwait(false),
				"role" => await service.SetCharacterEnabledAsync(args[1], args[0] == "enable", progress, token).ConfigureAwait(false),
				"reshade" => await service.SetReShadeAsync(args[0] == "on", acknowledgeRisk: false, progress, token).ConfigureAwait(false),
				"dlss" => await service.SetDlssAsync(args[0] switch
				{
					"fg" => IasDlssMode.FrameGeneration,
					"nr" => IasDlssMode.NeuralRendering,
					_ => IasDlssMode.Off
				}, progress, token).ConfigureAwait(false),
				"add" => await service.AddAsync(args, progress, token).ConfigureAwait(false),
				"remove" => await service.RemoveCharacterAsync(args[0], progress, token).ConfigureAwait(false),
				"to-standard" => await service.ConvertToStandardModAsync(args[0], progress, token,
					targetArmor: options.IasTargetArmor, targetHelmet: options.IasTargetHelmet, mergeHelmet: options.IasMergeHelmet).ConfigureAwait(false),
				_ => throw new CliUserException("IasCommand", $"未知 IAS 命令：{command}")
			};
			if (options.Json) CliOutput.WriteJson(result);
			else
			{
				var output = result.Success ? Console.Out : Console.Error;
				output.WriteLine(result.Message);
				if (result.CreatedModId is { } created) output.WriteLine(created);
				foreach (var warning in result.Warnings) Console.Error.WriteLine($"警告：{warning}");
			}
			return result.Success ? 0 : 2;
		}
		finally { Console.CancelKeyPress -= cancel; }
	}

	private static async Task<IasOperationResult> RecoverAsync(IasModuleService service, CancellationToken token)
	{
		try
		{
			var recovered = await service.RecoverPendingAsync(token).ConfigureAwait(false);
			return new(true, recovered ? "已恢复并清理 IAS 快照。" : "没有待恢复的 IAS 快照。", []);
		}
		catch (Exception error) when (error is not OperationCanceledException)
		{
			return new(false, error.Message, []);
		}
	}

	private sealed class CommandProgress : IProgress<IasPageProgress>
	{
		public void Report(IasPageProgress value)
		{
			if (value.Percent is null or 0 or 100) Console.Error.WriteLine(value.Title);
		}
	}
}

