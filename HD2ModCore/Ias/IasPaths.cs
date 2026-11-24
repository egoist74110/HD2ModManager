namespace HD2ModCore.Ias;

internal static class IasPaths
{
	// 官方发行包的组件职责与目录协议。版本由签名清单决定。
	internal static readonly string[] RuntimeFiles =
	[
		"ExternalRuntime/ArmorBase.zip",
		"ExternalRuntime/ArmorAssets.zip",
		"ExternalRuntime/Generator.zip",
		"ExternalRuntime/bundle.json"
	];

	internal static readonly string[] GraphicsComponents = ["ReShade", "DLSSFrameGeneration", "DLSSNeuralRendering"];
	internal static string RuntimeArchive(string name) => $"ExternalRuntime/{name}.zip";
	internal static string Expanded(IasInstalled installed, string name) =>
		Path.Combine(installed.ComponentsDirectory, "expanded", name);
	internal static readonly string[] GraphicsArchives = GraphicsComponents.Select(RuntimeArchive).ToArray();

	internal static bool IsRetainedFile(string path) =>
		path.StartsWith("app/", StringComparison.Ordinal) || RuntimeFiles.Contains(path, StringComparer.Ordinal) ||
		GraphicsArchives.Contains(path, StringComparer.Ordinal);

	internal static bool IsVersion(string? version) =>
		version is { Length: <= 64 } && Version.TryParse(version, out _) &&
		version.All(character => char.IsAsciiDigit(character) || character == '.');

	internal static bool IsSha256(string? hash) => hash is { Length: 64 } && hash.All(char.IsAsciiHexDigit);

	internal static bool IsRelativePath(string path)
	{
		foreach (var component in path.Split('/'))
		{
			if (component.Length == 0 || component.EndsWith('.') || component.EndsWith(' ') ||
				component.Any(character => character < ' ' || "<>:\"\\|?*".Contains(character)))
				return false;
			// Windows 会把带扩展名的设备名也解释为设备。
			var name = component.Split('.')[0].TrimEnd().ToUpperInvariant();
			if (name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
				(name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) ||
					name.StartsWith("LPT", StringComparison.Ordinal)) && "0123456789¹²³".Contains(name[3])))
				return false;
		}
		return true;
	}

	internal static void RejectLink(string path)
	{
		try
		{
			if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
				throw new IOException($"IAS 路径不能指向链接：{path}");
		}
		catch (FileNotFoundException) { }
		catch (DirectoryNotFoundException) { }
	}
}
