using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using Xunit;

namespace HD2ModCore.Tests;

public sealed class PhysBoneArmPoseTests
{
	[Theory]
	[InlineData("boot")]
	[InlineData("external")]
	public void IasOptionalGroupsAreInjectedWithoutArmPose(string mode)
	{
		var source = PhysBoneLuaSource.ComposeStandalonePackage(mode: mode, iasPhysicsGroups: 5);
		Assert.Contains($"mode = '{mode}', iasPhysicsGroups = 5 }})", source, StringComparison.Ordinal);
		var defaults = PhysBoneLuaSource.ComposeStandalonePackage(mode: mode);
		Assert.Contains($"mode = '{mode}', iasPhysicsGroups = 0 }})", defaults, StringComparison.Ordinal);
	}

	[Fact]
	public void ComposeStandalonePackage_DefaultOmitsArmPoseFlag()
	{
		var defaultExternal = PhysBoneLuaSource.ComposeStandalonePackage(LuaAddonCatalog.PhysicsAddonId);
		var explicitOff = PhysBoneLuaSource.ComposeStandalonePackage(LuaAddonCatalog.PhysicsAddonId, "external", armPoseEnabled: false);

		Assert.Equal(defaultExternal, explicitOff);
		Assert.Contains("PhysBoneRuntime.install(engine, { compat = PhysBoneCompat, mode = 'external', iasPhysicsGroups = 0 })", defaultExternal);
		Assert.DoesNotContain("armPose = true", defaultExternal);
	}

	[Fact]
	public void ComposeStandalonePackage_WhenArmPoseEnabled_InjectsArmPoseTrue()
	{
		var enabledExternal = PhysBoneLuaSource.ComposeStandalonePackage(LuaAddonCatalog.PhysicsAddonId, "external", armPoseEnabled: true);
		var enabledBoot = PhysBoneLuaSource.ComposeStandalonePackage(LuaAddonCatalog.PhysicsAddonId, "boot", armPoseEnabled: true);

		Assert.Contains("PhysBoneRuntime.install(engine, { compat = PhysBoneCompat, mode = 'external', armPose = true, iasPhysicsGroups = 0 })", enabledExternal);
		Assert.Contains("PhysBoneRuntime.install(engine, { compat = PhysBoneCompat, mode = 'boot', armPose = true, iasPhysicsGroups = 0 })", enabledBoot);
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

	[Fact]
	public void JointPoseData_IndependentBonesBecomeALuaList()
	{
		var options = JointPoseData.ToLuaOptions("""
			{ "independentBones": [ { "bone": "ias_l_shoulder", "source": "l_shoulder" }, { "bone": "ias_l_hand", "source": "l_hand" } ] }
			""");
		Assert.Equal("""independentBones = { { "ias_l_shoulder", "l_shoulder" }, { "ias_l_hand", "l_hand" } }""", options);
	}

	[Fact]
	public void JointPoseData_InjectsRestPoseAndOnlySelectedPieces()
	{
		const string json = """
			{
			  "independentBones": [{ "bone": "ias_l_shoulder", "source": "l_shoulder" }],
			  "independentRest": [{ "parent": "l_clavicle", "avatar": [0,0,0,1], "model": [0,0,1,0], "position": [0.125,0,0] }],
			  "independentPieces": { "#ID[0102030405060708]": ["ias_l_shoulder"], "#ID[1111111111111111]": ["ias_l_shoulder"] },
			  "independentPieceRoutes": { "#ID[0102030405060708]": { "slot": "LeftArm" }, "#ID[1111111111111111]": { "slot": "Torso_Armor", "option": "coat/on", "disabledBy": "coat/off" } }
			}
			""";
		var withoutCoat = JointPoseData.ToLuaOptions(json, new HashSet<string> { "coat/off" });
		var withCoat = JointPoseData.ToLuaOptions(json, new HashSet<string> { "coat/on" });
		Assert.Contains("""independentRest = { { "l_clavicle", { 0, 0, 0, 1 }, { 0, 0, 1, 0 }, { 0.125, 0, 0 } } }""", withoutCoat);
		Assert.Contains("""["#ID[0102030405060708]"] = { "ias_l_shoulder" }""", withoutCoat);
		Assert.DoesNotContain("1111111111111111", withoutCoat);
		Assert.Contains("[\"#ID[1111111111111111]\"] = \"Torso_Armor\"", withCoat);
		Assert.DoesNotContain("1111111111111111", JointPoseData.ToLuaOptions(json, new HashSet<string> { "coat/on", "coat/off" }));
		Assert.Throws<InvalidDataException>(() => JointPoseData.ToLuaOptions(json.Replace("[0,0,1,0]", "[0,0,0,0]")));
		Assert.Throws<InvalidDataException>(() => JointPoseData.ToLuaOptions(json.Replace("#ID[0102030405060708]", "invalid")));
		Assert.Throws<InvalidDataException>(() => JointPoseData.ToLuaOptions(json.Replace("[\"ias_l_shoulder\"]", "[\"ias_unknown\"]")));
	}

	[Fact]
	public void JointPoseData_ProfileHashesCoverPhysicsOnlyPiecesAndFollowActiveOptions()
	{
		const string json = """
			{
			  "profileHashes": [
			    { "hash": "0102030405060708", "option": null },
			    { "hash": "aaaaaaaaaaaaaaaa", "option": "coat/on" },
			    { "hash": "bbbbbbbbbbbbbbbb", "option": "coat/off" }
			  ],
			  "independentBones": []
			}
			""";
		// 只有物理件（没有独立骨）也要把名单送进运行时；空 independentBones 在有 profile 表时放行。
		Assert.Equal("""profileHashes = { "0102030405060708", "aaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbb" }""", JointPoseData.ToLuaOptions(json));
		Assert.Equal("""profileHashes = { "0102030405060708", "aaaaaaaaaaaaaaaa" }""",
			JointPoseData.ToLuaOptions(json, new HashSet<string> { "coat/on" }));
		Assert.Equal("""profileHashes = { "0102030405060708" }""", JointPoseData.ToLuaOptions(json, new HashSet<string>()));
		var declared = json.Replace("\"option\": \"coat/on\"", "\"option\": \"coat/on\", \"corpseEligible\": true, \"disabledBy\": \"coat/off\"");
		Assert.Contains("""corpseProfiles = { ["aaaaaaaaaaaaaaaa"] = true }""",
			JointPoseData.ToLuaOptions(declared, new HashSet<string> { "coat/on" }));
		Assert.DoesNotContain("corpseProfiles", JointPoseData.ToLuaOptions(declared, new HashSet<string> { "coat/on", "coat/off" }));
		// 与独立骨并存时 independentPieces 的语义不变。
		var both = JointPoseData.ToLuaOptions("""
			{
			  "profileHashes": [{ "hash": "0102030405060708" }, { "hash": "cccccccccccccccc" }],
			  "independentBones": [{ "bone": "ias_l_shoulder", "source": "l_shoulder" }],
			  "independentPieces": { "#ID[0102030405060708]": ["ias_l_shoulder"] }
			}
			""");
		Assert.Contains("""profileHashes = { "0102030405060708", "cccccccccccccccc" }""", both);
		Assert.Contains("""independentPieces = { ["#ID[0102030405060708]"] = { "ias_l_shoulder" } }""", both);
		Assert.Throws<InvalidDataException>(() => JointPoseData.ToLuaOptions(json.Replace("0102030405060708", "0102")));
		Assert.Throws<InvalidDataException>(() => JointPoseData.ToLuaOptions(json.Replace("0102030405060708", "0102030405060ABC")));
	}

	[Fact]
	public void JointPoseData_UsesTheActiveShoeBindForTheSharedUnitId()
	{
		const string json = """
			{
			  "independentBones": [{ "bone": "ias_l_ball", "source": "l_ball" }],
			  "independentPieces": { "#ID[0102030405060708]": ["ias_l_ball"], "#ID[0102030405060708]@shoes/off": ["ias_l_ball"] },
			  "independentPieceRoutes": {
			    "#ID[0102030405060708]": { "slot": "LeftLeg", "option": "shoes/on", "disabledBy": "shoes/off" },
			    "#ID[0102030405060708]@shoes/off": { "slot": "LeftLeg", "option": "shoes/off" }
			  },
			  "independentTranslations": {
			    "#ID[0102030405060708]": { "ias_l_ball": { "avatar": [0,0,0], "model": [0,0,0.125] } },
			    "#ID[0102030405060708]@shoes/off": { "ias_l_ball": { "avatar": [0,0,0], "model": [0,0,0.25] } }
			  }
			}
			""";
		var shoesOn = JointPoseData.ToLuaOptions(json, new HashSet<string> { "shoes/on" });
		var shoesOff = JointPoseData.ToLuaOptions(json, new HashSet<string> { "shoes/off" });
		Assert.Contains("""["ias_l_ball"] = { { 0, 0, 0 }, { 0, 0, 0.125 } }""", shoesOn);
		Assert.Contains("""["ias_l_ball"] = { { 0, 0, 0 }, { 0, 0, 0.25 } }""", shoesOff);
		Assert.DoesNotContain("@shoes/off", shoesOff);
	}

	[Fact]
	public void JointPoseData_ClothingAndCalibrationFollowTheActivePiece()
	{
		const string json = """
			{
			  "independentBones": [{"bone":"ias_hips", "source":"hips"}],
			  "independentPieces": {"#ID[0102030405060708]": ["ias_hips"]},
			  "independentPieceRoutes": {"#ID[0102030405060708]": {"slot":"Torso_Armor", "option":"coat/on"}},
			  "independentLegs": {"#ID[0102030405060708]": {"l": {"lateralScale":0.43, "bendNormal":[1,0,0]}}},
			  "independentPoseRules": {"#ID[0102030405060708]": [{
			    "target":"ias_pose_Jacket_01", "mode":0, "influence":0.75, "basis":[0,0,0,1],
			    "primary":{"bone":"ias_hips","reference":null,"neutral":[0,0,0,1],"offset":[0,0,0,1],"referenceOffset":[0,0,0,1]},
			    "axes":[
			      {"flags":1, "secondaryAxis":0, "points":[[-90,0,0,0],[30,1,2,3]]},
			      {"flags":0, "secondaryAxis":1, "points":[[-90,0,0,0],[30,0,0,0]]},
			      {"flags":0, "secondaryAxis":2, "points":[[-90,0,0,0],[30,0,0,0]]}
			    ]
			  }]}
			}
			""";
		var on = JointPoseData.ToLuaOptions(json, new HashSet<string> { "coat/on" });
		Assert.Contains("independentPoseRules", on);
		Assert.Contains("target = \"ias_pose_Jacket_01\"", on);
		Assert.Contains("{ 30, 1, 2, 3 }", on);
		Assert.Contains("l = { 0.43, { 1, 0, 0 } }", on);
		var off = JointPoseData.ToLuaOptions(json, new HashSet<string> { "coat/off" });
		Assert.DoesNotContain("independentPoseRules", off);
		Assert.DoesNotContain("independentLegs", off);
		Assert.Throws<InvalidDataException>(() => JointPoseData.ToLuaOptions(json.Replace("[-90,0,0,0],[30,1,2,3]",
			"[30,0,0,0],[30,1,2,3]", StringComparison.Ordinal)));
	}

	[Fact]
	public void JointPoseData_GripsUseTheSelectedVariant()
	{
		const string json = """
			{
			  "independentBones": [{"bone":"ias_r_hand", "source":"r_hand"}],
			  "independentPieces": {"#ID[0102030405060708]": ["ias_r_hand"], "#ID[0102030405060708]@coat/on": ["ias_r_hand"]},
			  "independentPieceRoutes": {
			    "#ID[0102030405060708]": {"slot":"RightArm", "disabledBy":"coat/on"},
			    "#ID[0102030405060708]@coat/on": {"slot":"RightArm", "option":"coat/on"}
			  },
			  "independentGrips": {
			    "#ID[0102030405060708]": {"r": [1,0,0,0.05,0,1,0,0.1,0,0,1,0]},
			    "#ID[0102030405060708]@coat/on": {"r": [1,0,0,0.06,0,1,0,0.1,0,0,1,0]}
			  }
			}
			""";
		Assert.Contains("r = { 1, 0, 0, 0.05, 0, 1, 0, 0.1, 0, 0, 1, 0 }",
			JointPoseData.ToLuaOptions(json));
		var enabled = new HashSet<string> { "coat/on" };
		var on = JointPoseData.ToLuaOptions(json, enabled);
		Assert.Contains("r = { 1, 0, 0, 0.06, 0, 1, 0, 0.1, 0, 0, 1, 0 }", on);
		Assert.DoesNotContain("0.05", on);
		Assert.Throws<InvalidDataException>(() => JointPoseData.ToLuaOptions(
			json.Replace("[1,0,0,0.06,0,1,0,0.1,0,0,1,0]", "[1,0,0]", StringComparison.Ordinal), enabled));
	}

	[Fact]
	public void JointPoseDeployment_FlattenedOptionsUseActiveSourceIncludes()
	{
		var root = Path.Combine(Path.GetTempPath(), "jointpose-flat-" + Guid.NewGuid().ToString("N"));
		try
		{
			var mod = Directory.CreateDirectory(Path.Combine(root, "IAS")).FullName;
			File.WriteAllText(Path.Combine(mod, "manifest.json"), """
				{ "options": [{ "include": ["coat/on"], "subOptions": [{}, { "include": ["coat/off"] }] }] }
				""");
			File.WriteAllText(Path.Combine(mod, JointPoseData.FileName), """
				{
				  "independentBones": [{ "bone": "ias_l_shoulder", "source": "l_shoulder" }],
				  "independentPieces": { "#ID[1111111111111111]": ["ias_l_shoulder"] },
				  "independentPieceRoutes": { "#ID[1111111111111111]": { "slot": "Torso_Armor", "option": "coat/on", "disabledBy": "coat/off" } }
				}
				""");
			var owner = new ModNode(ModNodeId.New(), "IAS", new("IAS", null, DateTimeOffset.UtcNow, null), [], []);
			var on = new ModNode(ModNodeId.New(), "IAS-coat-on", new("On", null, DateTimeOffset.UtcNow, null, ModNodeKind.Option,
				[owner.Id.Value.ToString("N")], SourcePackagePath: "option:0"), [], []);
			var off = new ModNode(ModNodeId.New(), "IAS-coat-off", new("Off", null, DateTimeOffset.UtcNow, null, ModNodeKind.Option,
				[on.Id.Value.ToString("N")], SourcePackagePath: "option:0/option:1"), [], []);
			var snapshot = new LibrarySnapshot(1, DateTimeOffset.UtcNow, new[] { owner, on, off }.ToDictionary(node => node.Id), []);
			var withCoat = LuaDeploymentService.ReadJointPoseOptions([new(owner.Id, 0), new(on.Id, 1)], snapshot, root, []);
			var withoutCoat = LuaDeploymentService.ReadJointPoseOptions([new(owner.Id, 0), new(on.Id, 1), new(off.Id, 2)], snapshot, root, []);
			Assert.Contains("1111111111111111", withCoat);
			Assert.NotNull(withoutCoat);
			Assert.DoesNotContain("1111111111111111", withoutCoat);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Theory]
	[InlineData("""{ "independentBones": [] }""")]
	[InlineData("""{ "independentBones": [ { "bone": "ias_l\"", "source": "l_hand" } ] }""")]
	[InlineData("""{ "independentBones": [ { "bone": "IAS_L", "source": "l_hand" } ] }""")]
	[InlineData("""{ "independentBones": [ { "bone": "ias_l_hand" } ] }""")]
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
