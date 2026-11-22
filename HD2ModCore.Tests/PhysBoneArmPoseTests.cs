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
}
