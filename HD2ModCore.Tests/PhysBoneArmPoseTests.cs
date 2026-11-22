using HD2ModCore.Infrastructure;
using Xunit;

namespace HD2ModCore.Tests;

public sealed class PhysBoneArmPoseTests
{
	[Fact]
	public void ComposeStandalonePackage_DefaultOmitsArmPoseFlag()
	{
		var defaultExternal = PhysBoneLuaSource.ComposeStandalonePackage(LuaAddonCatalog.PhysicsAddonId);
		var explicitOff = PhysBoneLuaSource.ComposeStandalonePackage(LuaAddonCatalog.PhysicsAddonId, "external", armPoseEnabled: false);

		Assert.Equal(defaultExternal, explicitOff);
		Assert.Contains("PhysBoneRuntime.install(engine, { compat = PhysBoneCompat, mode = 'external' })", defaultExternal);
		Assert.DoesNotContain("armPose = true", defaultExternal);
	}

	[Fact]
	public void ComposeStandalonePackage_WhenArmPoseEnabled_InjectsArmPoseTrue()
	{
		var enabledExternal = PhysBoneLuaSource.ComposeStandalonePackage(LuaAddonCatalog.PhysicsAddonId, "external", armPoseEnabled: true);
		var enabledBoot = PhysBoneLuaSource.ComposeStandalonePackage(LuaAddonCatalog.PhysicsAddonId, "boot", armPoseEnabled: true);

		Assert.Contains("PhysBoneRuntime.install(engine, { compat = PhysBoneCompat, mode = 'external', armPose = true })", enabledExternal);
		Assert.Contains("PhysBoneRuntime.install(engine, { compat = PhysBoneCompat, mode = 'boot', armPose = true })", enabledBoot);
	}

	[Fact]
	public void LuaAddonCatalog_Physics_RespectsArmPoseDefaultOffAndOptIn()
	{
		var off = LuaAddonCatalog.Physics();
		var on = LuaAddonCatalog.Physics(armPoseEnabled: true);

		Assert.DoesNotContain("armPose = true", off.Source);
		Assert.Contains("armPose = true", on.Source);
		Assert.False(LuaDeploymentService.ArmPoseEnabled);
	}

	private const string PoseJson = """
		{
		  "armShoulderTarget": { "l": [0.04482, 0.0121, 0.09788], "r": [0.04482, -0.0121, 0.09788] },
		  "legJointTarget": {
		    "l_thigh": [0.09427, 0.00951, 0.08237], "l_knee": [0.43693, 0.00895, 0.06084],
		    "l_foot": [0.49222, 0.05014, 0.06257], "l_ball": [0.14371, -0.00352, 0.04922],
		    "r_thigh": [0.09427, 0.00951, -0.08237], "r_knee": [0.43693, 0.00895, -0.06084],
		    "r_foot": [0.49222, 0.05014, -0.06257], "r_ball": [0.14371, -0.00352, -0.04922]
		  },
		  "shoulderShiftMeters": { "l": [0.1, 0, 0], "r": [-0.1, 0, 0] }
		}
		""";

	[Fact]
	public void JointPoseData_BecomesInstallOptionsOnlyWhenArmPoseIsOn()
	{
		var options = JointPoseData.ToLuaOptions(PoseJson);
		Assert.Contains("armShoulderTarget = { l = { 0.04482, 0.0121, 0.09788 }", options, StringComparison.Ordinal);
		Assert.Contains("r_ball = { 0.14371, -0.00352, -0.04922 }", options, StringComparison.Ordinal);
		Assert.DoesNotContain("shoulderShift", options, StringComparison.Ordinal);

		var on = PhysBoneLuaSource.ComposeStandalonePackage(LuaAddonCatalog.PhysicsAddonId, "boot", armPoseEnabled: true, jointPoseOptions: options);
		Assert.Contains("armPose = true, armShoulderTarget = {", on, StringComparison.Ordinal);
		var off = PhysBoneLuaSource.ComposeStandalonePackage(LuaAddonCatalog.PhysicsAddonId, "boot", armPoseEnabled: false, jointPoseOptions: options);
		Assert.DoesNotContain("legJointTarget = {", off, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("""{ "legJointTarget": { "l_thigh": [0, 0, 0] } }""")]
	[InlineData("""{ "armShoulderTarget": { "l": [0, 0, 2], "r": [0, 0, 0] } }""")]
	[InlineData("""{ "other": 1 }""")]
	public void JointPoseData_RejectsIncompleteOrOutOfRangeData(string json) =>
		Assert.Throws<InvalidDataException>(() => JointPoseData.ToLuaOptions(json));

	[Fact]
	public void JointPoseData_FindsTheFileInTheModOrItsParentButNotAboveTheLibrary()
	{
		var root = Path.Combine(Path.GetTempPath(), "jointpose-" + Guid.NewGuid().ToString("N"));
		try
		{
			var mod = Directory.CreateDirectory(Path.Combine(root, "mods", "ias", "coat_on")).FullName;
			var plain = Directory.CreateDirectory(Path.Combine(root, "mods", "plain")).FullName;
			File.WriteAllText(Path.Combine(root, JointPoseData.FileName), "{}");                 // 库外面的不算
			Assert.Empty(JointPoseData.Find([plain, mod], Path.Combine(root, "mods")));
			var file = Path.Combine(root, "mods", "ias", JointPoseData.FileName);
			File.WriteAllText(file, PoseJson);
			Assert.Equal([file], JointPoseData.Find([plain, mod, Path.GetDirectoryName(mod)!], Path.Combine(root, "mods")));
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}
}
