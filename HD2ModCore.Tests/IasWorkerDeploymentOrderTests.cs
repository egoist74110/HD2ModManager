using HD2ModCore.Ias;
using Xunit;

namespace HD2ModCore.Tests;

public sealed class IasWorkerDeploymentOrderTests
{
	[Fact]
	public void RegenerateDiscardsBeforeExporting()
	{
		var calls = new List<string>();

		var result = PreparedIasDeployment.Run(
			() => calls.Add("Generate"),
			() => { calls.Add("Apply"); return "transaction.json"; },
			() => calls.Add("Discard"),
			() => { calls.Add("ExportData"); return "data.zip"; });

		Assert.Equal(new[] { "Generate", "Apply", "Discard", "ExportData" }, calls);
		Assert.Equal("data.zip", result);
	}

	[Fact]
	public void PrepareBaseDiscardsAndReturnsTransaction()
	{
		var calls = new List<string>();

		var result = PreparedIasDeployment.Run(null,
			() => { calls.Add("Apply"); return "transaction.json"; },
			() => calls.Add("Discard"), null);

		Assert.Equal(new[] { "Apply", "Discard" }, calls);
		Assert.Equal("transaction.json", result);
	}

	[Theory]
	[InlineData("Generate", new[] { "Generate", "Discard" })]
	[InlineData("Apply", new[] { "Generate", "Apply", "Discard" })]
	[InlineData("Discard", new[] { "Generate", "Apply", "Discard" })]
	public void FailurePropagatesWithoutExporting(string failedStep, string[] expected)
	{
		var calls = new List<string>();
		var failure = new IOException(failedStep);
		void Record(string step)
		{
			calls.Add(step);
			if (step == failedStep) throw failure;
		}

		var thrown = Assert.Throws<IOException>(() => PreparedIasDeployment.Run(
			() => Record("Generate"),
			() => { Record("Apply"); return "transaction.json"; },
			() => Record("Discard"),
			() => { Record("ExportData"); return "data.zip"; }));

		Assert.Equal(expected, calls);
		Assert.Same(failure, thrown);
	}
}
