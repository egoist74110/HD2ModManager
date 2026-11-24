namespace HD2ModCore.Ias;

internal static class PreparedIasDeployment
{
	internal static string Run(Action? generate, Func<string> apply, Action discard, Func<string>? export)
	{
		string transaction;
		try
		{
			generate?.Invoke();
			transaction = apply();
		}
		finally
		{
			discard();
		}

		return export is null ? transaction : export();
	}
}
