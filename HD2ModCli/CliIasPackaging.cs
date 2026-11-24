using HD2SourceMigration.IasPackaging;

namespace HD2ModCli;

public sealed record IasPackArguments
{
	public string? Generator { get; init; }
	public string? AqSdk { get; init; }
	public string? Texconv { get; init; }
	public string? RigGender { get; init; }
	public string? ModelId { get; init; }
	public string? L4d2Directory { get; init; }
	public IReadOnlyList<string> MaterialVpkPaths { get; init; } = [];
	public string? ModelStem { get; init; }
	public string? BodyArchive { get; init; }
	public string? HelmetArchive { get; init; }
	public bool WithPhysics { get; init; } = true;

	internal IasPackArguments WithValue(string option, string value)
	{
		return option switch
		{
			"--ias" => this with { Generator = value },
			"--aqsdk" => this with { AqSdk = value },
			"--texconv" => this with { Texconv = value },
			"--rig-gender" => this with { RigGender = value },
			"--id" => this with { ModelId = value },
			"--l4d2-root" => this with { L4d2Directory = value },
			"--material-vpk" => this with { MaterialVpkPaths = [.. MaterialVpkPaths, value] },
			"--model-stem" => this with { ModelStem = value },
			"--body-archive" => this with { BodyArchive = value },
			"--helmet-archive" => this with { HelmetArchive = value },
			_ => throw new CliUserException("UnknownOption", $"不支持的参数：{option}"),
		};
	}
}

internal static class CliIasPackaging
{
	internal static async Task<int> RunAsync(string[] command, CliInvocation invocation)
	{
		const string usage = "hd2mod ias pack hd2|l4d2 <输入> --game-data <目录> --output <新目录> --ias <Generator目录> --aqsdk <目录> [--texconv <exe>] [--name <名称>] [--rig-gender female|male] [--id <UUID>] [--no-ias-phys] [--bone-hash <文件>] [--l4d2-root <目录>] [--material-vpk <文件>（l4d2，可重复）] [--model-stem <路径>] [--body-archive <归档> --helmet-archive <归档>（l4d2）] [--json]";
		if (command.Length == 3 && command[2] == "help")
		{
			if (invocation.Options.Json) CliOutput.WriteJson(new { help = usage });
			else Console.WriteLine(usage);
			return 0;
		}
		var global = invocation.Options;
		if (command.Length != 4 || command[2] is not ("hd2" or "l4d2")
			|| string.IsNullOrWhiteSpace(global.Output) || string.IsNullOrWhiteSpace(global.GameData))
			throw new CliUserException("InvalidIasPackArguments", usage);

		var pack = global.IasPack ?? new();
		if (command[2] == "hd2" && pack.MaterialVpkPaths.Count != 0)
			throw new CliUserException("InvalidIasPackArguments", "--material-vpk 仅用于 l4d2。");
		if (command[2] == "hd2" && (pack.BodyArchive is not null || pack.HelmetArchive is not null))
			throw new CliUserException("InvalidIasPackArguments", "底子护甲和头盔参数仅用于 l4d2。普通 Mod 按自身归档匹配。");
		var gender = pack.RigGender?.ToLowerInvariant() switch
		{
			null or "female" => IasRigGender.Female,
			"male" => IasRigGender.Male,
			_ => throw new CliUserException("InvalidIasRigGender", "--rig-gender 必须是 female 或 male。"),
		};
		Guid? modelId = null;
		if (pack.ModelId is not null)
		{
			if (!Guid.TryParse(pack.ModelId, out var id) || id == Guid.Empty)
				throw new CliUserException("InvalidIasModelId", "--id 必须是非全零的 UUID。");
			modelId = id;
		}
		var options = new IasPackagingOptions(global.Output, global.GameData)
		{
			GeneratorDirectory = pack.Generator,
			AqSdkDirectory = pack.AqSdk,
			TexconvPath = pack.Texconv,
			Name = global.Name,
			RigGender = gender,
			WithIasPhys = pack.WithPhysics,
			ModelId = modelId,
			BoneHashPath = global.BoneHash,
			L4d2Directory = pack.L4d2Directory,
			MaterialVpkPaths = pack.MaterialVpkPaths,
			ModelStem = pack.ModelStem,
			BodyArchive = pack.BodyArchive,
			HelmetArchive = pack.HelmetArchive,
			RecordTiming = (stage, elapsed) => Console.Error.WriteLine($"IAS 计时\t{stage}\t{elapsed.TotalMilliseconds:F0}ms"),
		};
		var service = new IasPackagingService();
		using var cancellation = new CancellationTokenSource();
		ConsoleCancelEventHandler cancel = (_, args) => { args.Cancel = true; cancellation.Cancel(); };
		Console.CancelKeyPress += cancel;
		IasPackagingResult result;
		try
		{
			result = command[2] == "hd2"
				? await service.ConvertPatchModAsync(invocation.Command[3], options, ct: cancellation.Token).ConfigureAwait(false)
				: await service.ConvertL4d2VpkAsync(invocation.Command[3], options, ct: cancellation.Token).ConfigureAwait(false);
		}
		finally { Console.CancelKeyPress -= cancel; }

		if (global.Json)
			CliOutput.WriteJson(result);
		else
		{
			if (result.IsSuccess)
			{
				Console.WriteLine($"{result.Name} ({result.ModelId})");
				Console.WriteLine(result.MainModZip);
				Console.WriteLine(result.CompanionZip);
			}
			foreach (var warning in result.Warnings)
				Console.Error.WriteLine($"警告：{warning}");
			foreach (var error in result.Errors)
				Console.Error.WriteLine($"错误：{error}");
		}
		return result.IsSuccess ? 0 : 2;
	}
}
