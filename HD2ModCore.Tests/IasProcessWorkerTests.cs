using HD2ModCore.Ias;
using Xunit;

namespace HD2ModCore.Tests;

public sealed class IasProcessWorkerTests
{
	[Fact]
	public async Task TemporaryDirectoriesStayShortAndIndependentAcrossManagerRoots()
	{
		var systemTemp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
		var source = Path.Combine(systemTemp, "ias-worker");
		var shallow = new IasProcessWorker(source, Path.Combine(systemTemp, "manager", "ias", "worker"));
		var deep = new IasProcessWorker(source, Path.Combine(systemTemp, "manager",
			new string('a', 100), new string('b', 100), "ias", "worker"));
		string? shallowTemp = null;
		string? deepTemp = null;

		await shallow.RunInTemporaryDirectoryAsync(async shallowStart =>
		{
			shallowTemp = shallowStart.Environment["TMP"]!;
			Assert.Equal(shallowTemp, shallowStart.Environment["TEMP"]);
			Assert.True(Directory.Exists(shallowTemp));
			var result = await deep.RunInTemporaryDirectoryAsync(deepStart =>
			{
				deepTemp = deepStart.Environment["TMP"]!;
				Assert.Equal(deepTemp, deepStart.Environment["TEMP"]);
				Assert.Equal(systemTemp, Path.GetDirectoryName(deepTemp));
				Assert.Equal(shallowTemp.Length, deepTemp.Length);
				Assert.True(deepTemp.Length + 140 <= 259);
				Assert.NotEqual(shallowTemp, deepTemp);
				Assert.True(Directory.Exists(shallowTemp));
				File.WriteAllText(Path.Combine(deepTemp, "output"), "test");
				return Task.FromResult(new IasWorkerResponse(false, Error: "模拟 worker 失败"));
			}, default);
			Assert.False(result.Success);
			Assert.False(Directory.Exists(deepTemp));
			Assert.True(Directory.Exists(shallowTemp));
			return new(true);
		}, default);

		Assert.False(Directory.Exists(shallowTemp));
	}

	[Fact]
	public void PathBudgetRejectsTheFirstExcessCharacterWithChineseError()
	{
		var root = Path.GetPathRoot(Path.GetTempPath())!;
		var path = root + new string('a', 119 - root.Length);
		IasProcessWorker.CheckTempPathLength(path);

		var error = Assert.Throws<PathTooLongException>(() => IasProcessWorker.CheckTempPathLength(path + "b"));

		Assert.Contains("IAS worker 暂存路径过长", error.Message);
		Assert.Contains("120 个字符，最多 119 个字符", error.Message);
		Assert.Contains("TEMP/TMP", error.Message);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task TemporaryDirectoryIsRemovedWhenInvocationThrows(bool cancel)
	{
		var systemTemp = Path.GetTempPath();
		var worker = new IasProcessWorker(Path.Combine(systemTemp, "ias-worker"), Path.Combine(systemTemp, "worker"));
		using var cancellation = new CancellationTokenSource();
		string? temporary = null;

		var error = await Record.ExceptionAsync(() => worker.RunInTemporaryDirectoryAsync(start =>
		{
			temporary = start.Environment["TMP"]!;
			File.WriteAllText(Path.Combine(temporary, "output"), "test");
			if (!cancel) throw new IOException("模拟 worker 启动失败");
			cancellation.Cancel();
			return Task.FromCanceled<IasWorkerResponse>(cancellation.Token);
		}, cancellation.Token));

		if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error);
		else Assert.IsType<IOException>(error);
		Assert.NotNull(temporary);
		Assert.False(Directory.Exists(temporary));
	}
}
