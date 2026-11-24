using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using HD2ModCore.Ias;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;
var output = Console.Out;
Console.SetOut(Console.Error);
IasWorkerResponse response;
try
{
	var request = JsonSerializer.Deserialize<IasWorkerRequest>(Console.In.ReadToEnd())
		?? throw new InvalidDataException("缺少 IAS worker 请求。");
	var load = System.Diagnostics.Stopwatch.StartNew();
	var official = new OfficialIas(request);
	OfficialIas.Timing("加载官方 DLL", load.Elapsed);
	response = new(true, official.Run());
}
catch (Exception exception)
{
	var cause = exception;
	while (cause is TargetInvocationException { InnerException: { } inner }) cause = inner;
	Console.Error.WriteLine(cause);
	response = new(false, Error: cause is UnauthorizedAccessException ? "访问 IAS 文件被拒绝。" : cause.Message,
		ErrorKind: cause is ReShadeConflictException ? IasWorkerErrorKind.ReShadeConflict : null);
}
output.WriteLine(JsonSerializer.Serialize(response));
return response.Success ? 0 : 1;

sealed class ReShadeConflictException(string message) : InvalidOperationException(message);

sealed class OfficialIas
{
	private readonly IasWorkerRequest _request;
	private readonly ConstructorInfo _store;
	private readonly MethodInfo _applyGraphics;
	private readonly Dictionary<string, MethodInfo> _methods = new();
	private readonly string _runtime;

	public OfficialIas(IasWorkerRequest request)
	{
		_request = request;
		var dll = Path.Combine(request.Components, "app", "HD2综合护甲系统.dll");
		if (!File.Exists(dll))
			throw Missing("官方客户端 DLL（HD2综合护甲系统.dll）");

		Assembly client;
		try { client = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll); }
		catch (Exception error) when (error is BadImageFormatException or FileLoadException)
		{
			throw Missing($"可加载的官方客户端 DLL（{error.Message}）");
		}
		Type TypeOf(string name) => client.GetType("HD2RuntimeManager." + name)
			?? throw Missing($"类型 HD2RuntimeManager.{name}");
		MethodInfo RequireMethod(Type owner, string name, Type result, bool isStatic, params Type[] parameters)
		{
			var flags = BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
			return owner.GetMethods(flags).FirstOrDefault(method => method.Name == name && method.ReturnType == result &&
				!method.ContainsGenericParameters && method.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameters))
				?? throw Missing($"方法 {(isStatic ? "static " : "")}{result.Name} {owner.Name}.{name}({string.Join(", ", parameters.Select(p => p.Name))})");
		}
		void Bind(Type owner, string name, Type result, bool isStatic, params Type[] parameters) =>
			_methods.Add(name, RequireMethod(owner, name, result, isStatic, parameters));
		var str = typeof(string);
		var none = typeof(void);
		var token = typeof(CancellationToken);
		var log = typeof(Action<string>);
		var inputs = TypeOf("NativeRuntimeInputs");
		var prep = TypeOf("NativeRuntimePreparation");
		var deployment = TypeOf("ExternalRuntimeDeployment");
		var plan = TypeOf("ExternalRuntimePlan");
		var change = TypeOf("ExternalRuntimeChange");
		var component = TypeOf("ExternalRuntimeComponent");
		var package = TypeOf("ExternalRuntimePackage");
		var file = TypeOf("ExternalRuntimeFile");
		var store = TypeOf("OwnedStore");
		var libraryEntry = TypeOf("ModLibraryEntry");
		var libraryState = TypeOf("ModLibraryState");
		_store = store.GetConstructors().FirstOrDefault(ctor =>
			ctor.GetParameters().Select(p => p.ParameterType).SequenceEqual([str, str]))
			?? throw Missing("构造函数 OwnedStore(String, String)");
		Bind(inputs, "PrepareSystem", prep, true, str, str, str, token);
		Bind(inputs, "Create", prep, true, str, str, str, typeof(bool));
		Bind(inputs, "Generate", none, true, prep, str, token, log);
		Bind(inputs, "Apply", str, true, prep, str, log);
		Bind(inputs, "Discard", typeof(bool), true, prep);
		Bind(deployment, "RuntimeDirectory", str, true, str, str);
		Bind(deployment, "ExportData", str, true, str, str, str, str, str);
		Bind(deployment, "Restore", none, true, str, str, log);
		Bind(store, "ImportToLibrary", libraryEntry, false, str);
		Bind(store, "SetLibraryEnabled", none, false, str, typeof(bool));
		Bind(store, "RemoveFromLibrary", none, false, str);
		Bind(store, "LibraryStatus", libraryState, false);
		Bind(deployment, "PrepareRuntime", plan, true, str, str, str, typeof(ISet<string>));
		Bind(deployment, "PrepareReShadeRemoval", plan, true, str, str);
		Bind(deployment, "PrepareDlss", plan, true, str, str, str);
		Bind(deployment, "PrepareDlssRemoval", plan, true, str, str);
		Bind(deployment, "ApplyPurge", none, true, plan, log);
		_applyGraphics = RequireMethod(deployment, "Apply", str, true, plan, str, log);
		Bind(deployment, "InstalledComponent", component, true, str, str, str, typeof(bool));
		Bind(deployment, "ReadPackage", package, true, str);
		Bind(TypeOf("ReShadeInstallCheck"), "Inspect", str, true, str, str, str);
		RequireField(prep, "Plan", plan);
		RequireField(plan, "RuntimeOnly", typeof(bool));
		RequireField(plan, "RuntimeRoot", str);
		RequireField(plan, "Game", str);
		RequireField(plan, "Component", str);
		RequireField(plan, "Changes", typeof(List<>).MakeGenericType(change));
		RequireField(change, "scope", str);
		RequireField(change, "path", str);
		RequireField(component, "variant", str);
		RequireField(package, "files", file.MakeArrayType());
		RequireField(file, "path", str);
		RequireField(file, "sha256", str);
		RequireField(libraryEntry, "id", str, publicOnly: true);
		RequireField(libraryEntry, "error", str);
		RequireField(libraryEntry, "enabled", typeof(bool), publicOnly: true);
		RequireField(libraryState, "entries", libraryEntry.MakeArrayType(), publicOnly: true);

		if (request.Command == "check-interface")
		{
			_runtime = string.Empty;
			return;
		}
		NativeLibrary.SetDllImportResolver(client, (name, _, _) =>
			name == "Microsoft.WindowsAppRuntime.dll"
			? NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, name)) : IntPtr.Zero);
		_runtime = (string)Call("RuntimeDirectory", null, request.GameRoot, null)!;
		var expected = Path.Combine(Directory.GetParent(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!.FullName, "Runtime");
		if (!Same(Path.GetDirectoryName(_runtime)!, expected))
			throw new InvalidDataException("IAS 运行目录超出指定位置。");
	}

	private string Expanded(string name) => Path.Combine(_request.Components, "expanded", name);
	private object Store() => _store.Invoke([_request.Library, _request.GameRoot]);
	private object? Call(string name, object? target, params object?[] args) => _methods[name].Invoke(target, args);
	private InvalidDataException Missing(string member) =>
		new($"此 IAS 版本 {_request.Version} 的接口有变化：缺少 {member}，请等待管理器更新。");
	private static FieldInfo? FindField(Type type, string name) => type.GetField(name,
		BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
	private void RequireField(Type type, string name, Type fieldType, bool publicOnly = false)
	{
		if (FindField(type, name) is not { } field || field.FieldType != fieldType || publicOnly && !field.IsPublic)
			throw Missing($"字段 {fieldType.Name} {type.Name}.{name}");
	}
	private static object? Field(object value, string name) => FindField(value.GetType(), name)!.GetValue(value);
	private static readonly Action<string> Log = value => Console.Error.WriteLine(value);
	internal static void Timing(string stage, TimeSpan elapsed) =>
		Console.Error.WriteLine($"IAS计时\t{stage}\t{elapsed.TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)}");
	private static T Timed<T>(string stage, Func<T> run)
	{
		var clock = System.Diagnostics.Stopwatch.StartNew();
		try { return run(); }
		finally { Timing(stage, clock.Elapsed); }
	}

	public string? Run()
	{
		var game = _request.GameRoot;
		var assets = Expanded("ArmorAssets");
		var argument = _request.Argument;
		switch (_request.Command)
		{
			case "check-interface":
				return null;
			case "prepare-base":
				Console.Error.WriteLine("IAS prepare-base：PrepareSystem");
				return Deploy(Timed("PrepareSystem", () => Call("PrepareSystem", null, game, assets, Expanded("ArmorBase"), CancellationToken.None)!), false);
			case "regenerate":
				Console.Error.WriteLine("IAS regenerate：Create");
				return Deploy(Timed("Create", () => Call("Create", null, game, _request.Library, assets, true)!), true);
			case "graphics-status":
				return JsonSerializer.Serialize(ReadGraphics());
			case "reshade-install":
			case "reshade-remove":
			case "dlss-install":
			case "dlss-remove":
				return ChangeGraphics();
			case "import":
				var store = Store();
				var entry = Call("ImportToLibrary", store, Required(argument))!;
				if (Field(entry, "error") is string { Length: > 0 } error)
					throw new InvalidDataException($"无法登记 IAS 配套包：{error}");
				var id = Required(Field(entry, "id") as string);
				Call("SetLibraryEnabled", store, id, true);
				return id;
			case "remove":
				Call("RemoveFromLibrary", Store(), Required(argument));
				return null;
			case "set-enabled":
				Call("SetLibraryEnabled", Store(), Required(argument), _request.Enabled);
				return null;
			case "status":
				return JsonSerializer.Serialize(new { RuntimeDirectory = _runtime, Library = Call("LibraryStatus", Store()) },
					new JsonSerializerOptions { IncludeFields = true });
			case "restore":
				var record = Required(argument);
				using (var journal = JsonDocument.Parse(File.ReadAllBytes(record)))
				{
					var root = journal.RootElement;
					if (!root.GetProperty("runtime_only").GetBoolean() ||
						!Same(root.GetProperty("game").GetString()!, game) ||
						!Same(root.GetProperty("runtime_root").GetString()!, _runtime) ||
						!Path.GetFullPath(record).StartsWith(Path.TrimEndingDirectorySeparator(_request.Backup) + Path.DirectorySeparatorChar,
							StringComparison.OrdinalIgnoreCase))
						throw new InvalidDataException("IAS 恢复事务超出指定范围。");
				}
				Call("Restore", null, record, game, Log);
				return null;
			default:
				throw new InvalidDataException($"未知 IAS worker 命令：{_request.Command}");
		}
	}

	private IasGraphicsState ReadGraphics()
	{
		var dlss = Call("InstalledComponent", null, _request.GameRoot, "dlss", null, false);
		var mode = dlss is null ? IasDlssMode.Off : Field(dlss, "variant") switch
		{
			"frame-generation" => IasDlssMode.FrameGeneration,
			"neural-rendering" => IasDlssMode.NeuralRendering,
			_ => throw new InvalidDataException("IAS 运行目录中的 DLSS 模式无效。")
		};
		return new(Call("InstalledComponent", null, _request.GameRoot, "reshade", null, false) is not null, mode);
	}

	private string? ChangeGraphics()
	{
		var game = _request.GameRoot;
		var reshade = _request.Command.StartsWith("reshade-", StringComparison.Ordinal);
		var removing = _request.Command.EndsWith("-remove", StringComparison.Ordinal);
		var component = reshade ? "reshade" : "dlss";
		object? plan;
		if (removing)
		{
			plan = Call(reshade ? "PrepareReShadeRemoval" : "PrepareDlssRemoval", null, game, null);
			if (plan is null) return null;
		}
		else if (reshade)
		{
			var package = Expanded("ReShade");
			var files = (IEnumerable)Field(Call("ReadPackage", null, package)!, "files")!;
			var dxgi = files.Cast<object>().Single(row => Field(row, "path") is "dxgi.dll");
			var bundled = Path.Combine(package, "files", "dxgi.dll");
			var conflict = Call("Inspect", null, Path.Combine(game, "bin", "dxgi.dll"), bundled, Field(dxgi, "sha256"));
			if (conflict is string message) throw new ReShadeConflictException(message);
			// 与官方界面一致，只准备运行目录和披风设置，data 包交给托管 Mod。
			plan = Call("PrepareRuntime", null, game, package, null, null);
		}
		else
		{
			if (!ReadGraphics().ReShadeEnabled) throw new InvalidOperationException("请先打开 ReShade 滤镜。");
			var package = _request.Argument switch
			{
				"fg" => "DLSSFrameGeneration",
				"nr" => "DLSSNeuralRendering",
				_ => throw new InvalidDataException("DLSS 模式必须为 fg 或 nr。")
			};
			plan = Call("PrepareDlss", null, game, Expanded(package), null);
		}

		if (plan is null || Field(plan, "RuntimeOnly") is not true || Field(plan, "Component") is not string actual ||
			actual != component || !Same((string)Field(plan, "Game")!, game) || !Same((string)Field(plan, "RuntimeRoot")!, _runtime))
			throw new InvalidDataException("IAS 画面增强计划超出指定运行范围。");
		foreach (var change in (IEnumerable)Field(plan, "Changes")!)
		{
			if (Field(change, "scope") is "runtime") continue;
			if (reshade && !removing && Field(change, "scope") is "game" && Field(change, "path") is "bin/ShaderToggler.ini")
				continue;
			throw new InvalidDataException("IAS 画面增强计划包含未经允许的游戏文件。");
		}

		Console.Error.WriteLine($"IAS {_request.Command}：{(removing ? "ApplyPurge" : "Apply")}");
		if (removing)
		{
			// 官方删除计划是永久清理，失败恢复由 Core 的运行目录快照负责。
			Call("ApplyPurge", null, plan, Log);
			return null;
		}
		return (string)_applyGraphics.Invoke(null, [plan, _request.Backup, Log])!;
	}

	private string Deploy(object prep, bool generate)
	{
		return PreparedIasDeployment.Run(generate ? Generate : null, Apply, Discard, generate ? ExportData : null);

		void Generate()
		{
			Console.Error.WriteLine("IAS regenerate：Generate");
			Timed("Generate", () => Call("Generate", null, prep, Expanded("Generator"), CancellationToken.None, Log));
		}

		string Apply()
		{
			var plan = Field(prep, "Plan")!;
			if (Field(plan, "RuntimeOnly") is not true || Field(plan, "RuntimeRoot") is not string root || !Same(root, _runtime))
				throw new InvalidDataException("IAS 部署计划超出护甲运行目录。");
			Console.Error.WriteLine($"IAS {_request.Command}：Apply");
			return Timed("Apply", () => (string)Call("Apply", null, prep, _request.Backup, Log)!);
		}

		void Discard()
		{
			Console.Error.WriteLine($"IAS {_request.Command}：Discard");
			if (Timed("Discard", () => Call("Discard", null, prep)) is not true)
				Console.Error.WriteLine("官方 IAS 暂存目录清理失败。");
		}

		string ExportData()
		{
			Console.Error.WriteLine("IAS regenerate：ExportData");
			return Timed("ExportData", () => (string)Call("ExportData", null, _request.GameRoot, _request.Export, _request.Icon, _request.Version, null)!);
		}
	}

	private static string Required(string? path) => string.IsNullOrWhiteSpace(path)
		? throw new InvalidDataException("IAS worker 缺少命令参数。") : path;

	private static bool Same(string first, string second) => string.Equals(
		Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
		Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);
}

