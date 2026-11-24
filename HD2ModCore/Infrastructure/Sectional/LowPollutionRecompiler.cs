namespace HD2ModCore.Infrastructure.Sectional;

/// <param name="Recompiled">重编成功的素体。</param>
/// <param name="Failed">重编失败的素体与原因：它们对应的映射目标本次要跳过。</param>
/// <param name="Blocked">需要重编但不能自动做的素体（目标不是自己的素体）。</param>
/// <param name="UpToDate">已经是最新的，没动。</param>
public sealed record LowPollutionRecompileResult(
	IReadOnlyList<LowPollutionPrepItem> Recompiled,
	IReadOnlyList<(LowPollutionPrepItem Item, string Error)> Failed,
	IReadOnlyList<LowPollutionPrepItem> Blocked,
	IReadOnlyList<LowPollutionPrepItem> UpToDate,
	bool Canceled)
{
	public IReadOnlyList<LowPollutionPrepItem> Restored { get; init; } = [];

	/// <summary>本次映射要跳过的素体 key（失败 + 不能自动做）。</summary>
	public IReadOnlySet<string> SkippedModelKeys => Failed.Select(f => f.Item.ModelKey)
		.Concat(Blocked.Select(item => item.ModelKey))
		.ToHashSet(StringComparer.OrdinalIgnoreCase);

	public int RecompileCount { get; init; }
}

/// <summary>
/// 「应用」映射前逐个准备待处理素体：先尝试换入缓存，再按需编译；一个失败不影响下一个，取消就停。
/// </summary>
public static class LowPollutionRecompiler
{
	public static async Task<LowPollutionRecompileResult> RunAsync(
		IReadOnlyList<LowPollutionPrepItem> items,
		Func<LowPollutionPrepItem, CancellationToken, Task<(bool Ok, string Message)>> compile,
		Action<int, int, LowPollutionPrepItem>? progress,
		CancellationToken cancellationToken = default,
		Func<LowPollutionPrepItem, CancellationToken, ValueTask<bool>>? tryRestore = null)
	{
		var recompiled = new List<LowPollutionPrepItem>();
		var restored = new List<LowPollutionPrepItem>();
		var failed = new List<(LowPollutionPrepItem, string)>();
		var recompileCount = 0;
		var need = items.Where(item => item.Status == LowPollutionPrepStatus.NeedsRecompile).ToArray();
		var canceled = false;
		for (var index = 0; index < need.Length; index++)
		{
			if (cancellationToken.IsCancellationRequested) { canceled = true; break; }
			progress?.Invoke(index + 1, need.Length, need[index]);
			try
			{
				if (tryRestore is not null && await tryRestore(need[index], cancellationToken).ConfigureAwait(false))
				{
					restored.Add(need[index]);
					continue;
				}
				recompileCount++;
				var (ok, message) = await compile(need[index], cancellationToken).ConfigureAwait(false);
				if (ok) recompiled.Add(need[index]);
				else failed.Add((need[index], message));
			}
			catch (OperationCanceledException)
			{
				canceled = true;
				break;
			}
			catch (Exception exception) when (exception is IOException or InvalidOperationException
				or InvalidDataException or UnauthorizedAccessException)
			{
				failed.Add((need[index], exception.Message));
			}
		}

		return new LowPollutionRecompileResult(
			recompiled,
			failed,
			[.. items.Where(item => item.Status == LowPollutionPrepStatus.Blocked)],
			[.. items.Where(item => item.Status == LowPollutionPrepStatus.UpToDate)],
			canceled)
		{
			Restored = restored,
			RecompileCount = recompileCount,
		};
	}
}
