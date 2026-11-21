namespace IasModelCli;

internal static class Program
{
	public static async Task<int> Main(string[] args)
	{
		if (args.Length == 0 || args.Length % 2 == 0 || args[0] is not ("hd2" or "l4d2"))
		{
			Console.Error.WriteLine("Run ias_model.py --help for converter usage.");
			return 2;
		}

		try
		{
			var options = new Dictionary<string, string>(StringComparer.Ordinal);
			for (var i = 1; i < args.Length; i += 2)
				options.Add(args[i], args[i + 1]);

			if (args[0] == "hd2")
				await PatchExtractor.RunAsync(options).ConfigureAwait(false);
			else
				await VpkExtractor.RunAsync(options).ConfigureAwait(false);

			return 0;
		}
		catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException
			or KeyNotFoundException or FormatException or OverflowException or System.Text.Json.JsonException)
		{
			Console.Error.WriteLine($"Model extraction failed: {error.Message}");
			return 1;
		}
	}
}
