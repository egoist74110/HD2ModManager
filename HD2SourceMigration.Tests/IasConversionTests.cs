using System.Text;
using System.Text.Json;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2SourceMigration.IasConversion;
using HD2SourceMigration.Retarget;
using HD2SourceModel;

namespace HD2SourceMigration.Tests;

public sealed class IasConversionTests
{
	[Fact]
	public void RigReaderFoldsCustomChainIntoMappedNativeAncestorAndChecksCrc()
	{
		using var payload = new MemoryStream();
		using var body = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true);
		WriteName(body, "head");
		body.Write(-1);
		body.Write(0u);
		WriteMatrix(body, 1);
		body.Write(42ul);
		body.Write(3);
		body.Write(0);
		body.Write(0);
		body.Write(3);
		body.Write(8u);
		WriteTarget("head", -1, 0, 0, 1);
		WriteTarget("hair", 0, 1, 256, 0.1f);
		WriteTarget("hair_tip", 1, 2, 256, 0.2f);

		var bytes = payload.ToArray();
		using var rig = new MemoryStream();
		using var header = new BinaryWriter(rig, Encoding.UTF8, leaveOpen: true);
		header.Write("HD2IRG1\0"u8);
		header.Write(1);
		header.Write(32);
		header.Write(1);
		header.Write(1);
		header.Write(IasRigReader.Crc32(bytes));
		header.Write(2u);
		header.Write(bytes);
		rig.Position = 0;
		var profile = IasRigReader.Read(rig)[42];
		Assert.Equal(0, profile.ResolveSemanticNode(2));
		Assert.Equal(1.3, profile.Nodes[2].World.Origin.Z, 6);
		rig.Position = rig.Length - 1;
		rig.WriteByte(1);
		rig.Position = 0;
		Assert.Throws<InvalidDataException>(() => IasRigReader.Read(rig));
		return;

		void WriteTarget(string name, int parent, int slot, uint flags, float z)
		{
			WriteName(body, name);
			body.Write(parent);
			body.Write(0);
			body.Write(slot);
			body.Write(flags);
			body.Write(1f);
			WriteMatrix(body, z);
		}
	}

	[Fact]
	public void FoldedWeightsMergeBeforeTakingFourAndNormalize()
	{
		var merged = IasNativeMesh.MergeInfluences(
			[new(1, 0.24f), new(2, 0.25f), new(3, 0.16f), new(1, 0.05f), new(4, 0.16f), new(5, 0.09f)]);
		Assert.Equal(new uint[] { 1, 2, 3, 4 }, merged.Select(item => item.BoneHash));
		Assert.Equal(1f, merged.Sum(item => item.Weight), 6);
		Assert.Equal(0.29f / 0.86f, merged[0].Weight, 6);
	}

	[Fact]
	public void NativeRetargetKeepsMetresAndFourthInfluence()
	{
		var source = new[] { new Hd2RigBone(0, "head", 0, Mat4.Identity), new Hd2RigBone(1, "neck", 1, Mat4.Identity) };
		var vertices = new[] { new SourceVertex(new(1, 0, 0), new(0, 0, 1), 0, 0, default, default, default),
			new SourceVertex(new(0, 1, 0), new(0, 0, 1), 1, 0, default, default, default),
			new SourceVertex(new(0, 0, 0), new(0, 0, 1), 0, 1, default, default, default) };
		var geometry = new SourceGeometry(vertices, [new(0, 1, 2, 0)], []);
		var result = Retargeter.RunNative(source, geometry,
			Enumerable.Repeat(new SourceVertexWeight[] { new(0, 0), new(0, 0), new(0, 0), new(1, 1) }, 3).ToArray(),
			new Hd2Rig(source), Vec3.Zero);
		Assert.Equal(new Vec3(1, 0, 0), result.SourcePoints[0]);
		Assert.Equal(new Vec3(1, 0, 0), result.Output[0]);
		Assert.Equal(new Vec3(0, 0, 1), Vec3.Cross(result.Output[1] - result.Output[0], result.Output[2] - result.Output[0]));
		Assert.Equal(1, result.Resolved[1]);
	}

	[Fact]
	public void Dp8RoutesKeepBaseAndTwoIndependentDefaultFirstGroups()
	{
		var aliases = IasDp8Routes.Slots.ToDictionary(slot => slot.Source, slot => slot.Source + 1);
		var lines = new List<string> { "manifest_version=6", $"archive={IasDp8Routes.SourceBody}" };
		AddOptions("Torso_Armor", "coat", "外套", "穿外套", "脱外套");
		AddOptions("LeftLeg", "shoes", "鞋子", "穿鞋", "脱鞋");
		var plan = IasDp8Routes.Plan(aliases, lines);
		// 10 条有网格的本体路由 + 10 个藏起来的 DP-8 护甲层部件（肩甲、腿甲、腰带等）。
		Assert.Equal(20, plan.Base.Count);
		Assert.Equal(10, plan.Base.Count(route => route.Hidden));
		Assert.All(plan.Base.Where(route => route.Hidden), route => Assert.True(route.Slot.Empty));
		Assert.Equal(23, IasDp8Routes.Slots.Select(slot => slot.Target).Distinct().Count());
		Assert.Equal(0x510291bd9b94c994ul, plan.Base.Single(route => route.Slot.Helmet).Slot.Target);
		Assert.Equal(new[] { 0xe3c2ff7999bd66adul, 0xd438f72d512e082eul },
			plan.Groups[0].Options[1].Routes.Select(route => route.Slot.Target));
		Assert.All(plan.Groups[0].Options[1].Routes, route => Assert.True(route.Hidden));
		Assert.All(plan.Groups[1].Options.SelectMany(option => option.Routes), route => Assert.False(route.Hidden));
		Assert.Equal(0xd939d51b56980ee7ul, plan.Groups[1].Options[1].Routes.Single().Slot.Target);
		using var manifest = JsonDocument.Parse(IasDp8Converter.SerializeManifest(plan));
		var groups = manifest.RootElement.GetProperty("options");
		Assert.Equal(new[] { "外套", "鞋子" }, groups.EnumerateArray().Select(group => group.GetProperty("name").GetString()));
		Assert.Equal(new[] { "coat/on", "shoes/on" },
			groups.EnumerateArray().Select(group => group.GetProperty("include")[0].GetString()));
		Assert.Equal(new[] { "coat/off", "shoes/off" },
			groups.EnumerateArray().Select(group => group.GetProperty("subOptions")[1].GetProperty("include")[0].GetString()));

		void AddOptions(string part, string group, string name, string onName, string offName)
		{
			lines.AddRange([
				$"free_part={group},{part},on",
				$"free_part_display_name={group},{name}",
				$"free_option={group},off,1",
				$"free_option={group},on,0",
				$"free_option_display_name={group},on,{onName}",
				$"free_option_display_name={group},off,{offName}",
			]);
			foreach (var slot in IasDp8Routes.Slots.Where(slot => slot.Part == part))
			{
				lines.Add($"free_route={group},on,{slot.Source:x16},{aliases[slot.Source]:x16},{aliases[slot.Source]:x16}");
				lines.Add($"free_route={group},off,{slot.Source:x16},{aliases[slot.Source]:x16},{aliases[slot.Source] + 1:x16}");
			}
		}
	}

	[Fact]
	public void NativeArmRefitInterpolatesTwistResidualAlongSegmentInsteadOfSubtractingMidArmFromElbow()
	{
		var source = new[]
		{
			new Hd2RigBone(0, "hips", -1, Translate(0, 0, 1.1)),
			new Hd2RigBone(1, "head", 0, Translate(0, 0, 1.7)),
			new Hd2RigBone(2, "l_shoulder", 0, Translate(-0.10, 0, 1.50)),
			// Sample places l_shoulder_twist at the elbow, whereas native HD2 places it mid-upper-arm.
			new Hd2RigBone(3, "l_shoulder_twist", 2, Translate(-0.35, 0, 1.40)),
			new Hd2RigBone(4, "l_elbow", 2, Translate(-0.35, 0, 1.40)),
			new Hd2RigBone(5, "l_hand", 4, Translate(-0.55, 0.10, 1.30)),
		};
		var target = new[]
		{
			new Hd2RigBone(0, "hips", -1, Translate(0, 0, 1.1)),
			new Hd2RigBone(1, "head", 0, Translate(0, 0, 1.7)),
			new Hd2RigBone(2, "l_shoulder", 0, Translate(-0.15, 0, 1.52)),
			new Hd2RigBone(3, "l_shoulder_twist", 2, Translate(-0.28, 0, 1.44)),
			new Hd2RigBone(4, "l_elbow", 2, Translate(-0.47, 0, 1.32)),
			new Hd2RigBone(5, "l_hand", 4, Translate(-0.65, 0.12, 1.22)),
		};
		var vertices = new[]
		{
			new SourceVertex(new(-0.35, 0, 1.40), new(0, 0, 1), 0, 0, default, default, default),
			new SourceVertex(new(-0.35, 0.01, 1.40), new(0, 0, 1), 1, 0, default, default, default),
			new SourceVertex(new(-0.55, 0.10, 1.30), new(0, 0, 1), 0, 1, default, default, default),
		};
		var weights = new SourceVertexWeight[][]
		{
			[new(3, 1f)],
			[new(4, 1f)],
			[new(5, 1f)],
		};
		var geometry = new SourceGeometry(vertices, [new(0, 1, 2, 0)], []);
		var result = Retargeter.RunNative(source, geometry, weights, new Hd2Rig(target), new(0, 0, 1.7), refitArms: true);
		var twistElbowGap = (result.Output[0] - result.Output[1]).Length;
		Assert.InRange(twistElbowGap, 0.0, 0.08);
	}

	private static Mat4 Translate(double x, double y, double z) =>
		new(1, 0, 0, x, 0, 1, 0, y, 0, 0, 1, z, 0, 0, 0, 1);

	[Fact]
	public void SpatialArmFoldContextEliminatesCustomChestDragOnDistalSleevesWhilePreservingTorsoHem()
	{
		var sourceBones = new[]
		{
			new Hd2RigBone(0, "chest", -1, Translate(0, 0, 1.36)),
			new Hd2RigBone(1, "l_clavicle", 0, Translate(-0.07, 0, 1.57)),
			new Hd2RigBone(2, "l_shoulder", 1, Translate(-0.10, 0, 1.57)),
			new Hd2RigBone(3, "l_elbow", 2, Translate(-0.36, 0, 1.39)),
			new Hd2RigBone(4, "l_hand", 3, Translate(-0.55, 0.14, 1.30)),
		};
		var context = IasNativeMesh.BuildArmFoldContext(sourceBones);
		Assert.NotNull(context);

		var nodes = new[]
		{
			new IasRigNode("chest", -1, 0, 0, 0, Translate(0, 0, 1.36)),
			new IasRigNode("l_clavicle", 0, 1, 1, 0, Translate(-0.07, 0, 1.57)),
			new IasRigNode("l_shoulder", 1, 2, 2, 0, Translate(-0.10, 0, 1.57)),
			new IasRigNode("l_elbow", 2, 3, 3, 0, Translate(-0.36, 0, 1.39)),
			new IasRigNode("l_hand", 3, 4, 4, 0, Translate(-0.55, 0.14, 1.30)),
			new IasRigNode("Jacket_01_01_L", 0, 0, 5, 256, Translate(-0.32, 0.12, 1.55)),
		};
		var profile = new IasRigProfile(["chest", "l_clavicle", "l_shoulder", "l_elbow", "l_hand"], nodes);
		var bySlot = nodes.Select((n, i) => (n.Slot, i)).ToDictionary(x => x.Slot, x => x.i);
		var cache = new Dictionary<int, int>();

		// Distal sleeve vertex near l_elbow with mixed native arm + custom Jacket weights -> zero chest drag.
		var distal = IasNativeMesh.FoldVertexWeights(
			[(2, 0.4f), (3, 0.3f), (5, 0.3f)],
			bySlot, cache, profile, new(-0.38, 0.02, 1.40), context);
		Assert.DoesNotContain(distal, w => w.BoneIndex == 0);
		Assert.Equal(1.0, distal.Sum(w => w.Weight), 5);
		Assert.Equal(0.4f / 0.7f, distal.Single(w => w.BoneIndex == 2).Weight, 5);

		// Lower coat hem vertex below the arm -> custom Jacket weight stays on chest.
		var hem = IasNativeMesh.FoldVertexWeights(
			[(5, 1.0f)],
			bySlot, cache, profile, new(-0.16, 0.20, 0.94), context);
		var hemWeight = Assert.Single(hem);
		Assert.Equal(0, hemWeight.BoneIndex);
		Assert.Equal(1.0f, hemWeight.Weight, 5);
	}

	[Fact]
	public void MisalignedTwistBoneFoldsProportionallyAlongSegmentToEliminateWrongPivot()
	{
		var sourceBones = new[]
		{
			new Hd2RigBone(0, "chest", -1, Translate(0, 0, 1.36)),
			new Hd2RigBone(1, "l_clavicle", 0, Translate(-0.07, 0, 1.57)),
			new Hd2RigBone(2, "l_shoulder", 1, Translate(-0.10, 0, 1.57)),
			new Hd2RigBone(3, "l_shoulder_twist", 2, Translate(-0.36, 0, 1.39)), // at elbow!
			new Hd2RigBone(4, "l_elbow", 2, Translate(-0.36, 0, 1.39)),
			new Hd2RigBone(5, "l_hand", 4, Translate(-0.55, 0.14, 1.30)),
		};
		var targetBones = new[]
		{
			new Hd2RigBone(0, "chest", -1, Translate(0, 0, 1.36)),
			new Hd2RigBone(1, "l_clavicle", 0, Translate(-0.07, 0, 1.57)),
			new Hd2RigBone(2, "l_shoulder", 1, Translate(-0.15, 0, 1.52)),
			new Hd2RigBone(3, "l_shoulder_twist", 2, Translate(-0.28, 0, 1.44)), // mid-upper-arm!
			new Hd2RigBone(4, "l_elbow", 2, Translate(-0.47, 0, 1.32)),
			new Hd2RigBone(5, "l_hand", 4, Translate(-0.65, 0.12, 1.22)),
		};
		var context = IasNativeMesh.BuildArmFoldContext(sourceBones, new Hd2Rig(targetBones));
		Assert.NotNull(context);
		Assert.True(context!.MisalignedTwists.ContainsKey(3));

		var nodes = new[]
		{
			new IasRigNode("chest", -1, 0, 0, 0, Translate(0, 0, 1.36)),
			new IasRigNode("l_clavicle", 0, 1, 1, 0, Translate(-0.07, 0, 1.57)),
			new IasRigNode("l_shoulder", 1, 2, 2, 0, Translate(-0.10, 0, 1.57)),
			new IasRigNode("l_shoulder_twist", 2, 3, 3, 0, Translate(-0.36, 0, 1.39)),
			new IasRigNode("l_elbow", 2, 4, 4, 0, Translate(-0.36, 0, 1.39)),
			new IasRigNode("l_hand", 4, 5, 5, 0, Translate(-0.55, 0.14, 1.30)),
		};
		var profile = new IasRigProfile(["chest", "l_clavicle", "l_shoulder", "l_shoulder_twist", "l_elbow", "l_hand"], nodes);
		var bySlot = nodes.Select((n, i) => (n.Slot, i)).ToDictionary(x => x.Slot, x => x.i);
		var cache = new Dictionary<int, int>();

		var folded = IasNativeMesh.FoldVertexWeights(
			[(3, 0.8f), (4, 0.2f)],
			bySlot, cache, profile, new(-0.36, 0.0, 1.39), context);
		Assert.DoesNotContain(folded, w => w.BoneIndex == 3);
		Assert.Equal(0.4f, folded.Single(w => w.BoneIndex == 2).Weight, 5);
		Assert.Equal(0.6f, folded.Single(w => w.BoneIndex == 4).Weight, 5);
	}

	[Fact]
	public void NativeRetargetDoesNotApplyArmScratchStretchOrL4d2BasisToElbowEdges()
	{
		var source = new[]
		{
			new Hd2RigBone(0, "hips", -1, Translate(0, 0, 1.1)),
			new Hd2RigBone(1, "head", 0, Translate(0, 0, 1.7)),
			new Hd2RigBone(2, "l_shoulder", 0, Translate(-0.10, -0.02, 1.56)),
			new Hd2RigBone(3, "l_elbow", 2, Translate(-0.35, -0.04, 1.39)),
			new Hd2RigBone(4, "l_hand", 3, Translate(-0.55, 0.14, 1.30)),
		};
		var target = new[]
		{
			new Hd2RigBone(0, "hips", -1, Translate(0, 0, 1.1)),
			new Hd2RigBone(1, "head", 0, Translate(0, 0, 1.7)),
			new Hd2RigBone(2, "l_shoulder", 0, Translate(-0.21, -0.07, 1.47)),
			new Hd2RigBone(3, "l_elbow", 2, Translate(-0.47, -0.05, 1.32)),
			new Hd2RigBone(4, "l_hand", 3, Translate(-0.66, 0.14, 1.22)),
		};
		var pA = new Vec3(-0.34, -0.04, 1.39);
		var pB = new Vec3(-0.36, -0.04, 1.39);
		var vertices = new[]
		{
			new SourceVertex(pA, new(0, 0, 1), 0, 0, default, default, default),
			new SourceVertex(pB, new(0, 0, 1), 1, 0, default, default, default),
			new SourceVertex(new(-0.55, 0.14, 1.30), new(0, 0, 1), 0, 1, default, default, default),
		};
		var weights = new SourceVertexWeight[][]
		{
			[new(2, 0.5f), new(3, 0.5f)],
			[new(3, 1.0f)],
			[new(4, 1.0f)],
		};
		var result = Retargeter.RunNative(source, new SourceGeometry(vertices, [new(0, 1, 2, 0)], []), weights, new Hd2Rig(target), new(0, 0, 1.7), refitArms: true);
		var origLen = (pB - pA).Length;
		var outLen = (result.Output[1] - result.Output[0]).Length;
		// Neither ArmScratch nor L4D2 InitialBasisRotations should deform the native IAS elbow bone.
		Assert.InRange((result.Deform[3].Origin - Vec3.Zero).Length, 0.0, 1e-9);
		Assert.InRange(outLen / origLen, 0.75, 1.50);
	}

	[Fact]
	public void HiddenUnitGeneratorProducesOnlyASubMillimetrePlaceholder()
	{
		var stream = new UnitStreamInfo(0, 0, 0, 1, 0, 0, 12, 0, 0, 0, 0, 0, 0, 0,
			[new(0, "position", 2, "vec3_float", 0, 0, 12)]);
		var mesh = new UnitRawMeshData(0, 0, 0, 0, [new(0, 123, [new(0, 1, 2)])], [new(0, 1, 2)], []);
		var result = new HiddenUnitGenerator().Generate(mesh, stream);
		Assert.True(result.IsValid);
		Assert.Single(result.Mesh!.Triangles);
		Assert.Equal(3, result.Mesh.Vertices.Count);
		Assert.All(result.Mesh.Vertices, vertex =>
			Assert.All(vertex.Components[0].FloatValues, coordinate => Assert.InRange(coordinate, 0f, .0001f)));
	}

	[Fact]
	public void ShoulderTargetPutsTheJointBackWhereTheModelSkeletonHasItInClavicleSpace()
	{
		// 左锁骨绕 Z 转 90°（局部 +X → 世界 +Y），潜兵肩在锁骨外侧 14 cm；模型肩更靠里、更高。
		static Mat4 Pose(double x, double y, double z, bool rotated) => rotated
			? new Mat4(0, -1, 0, x, 1, 0, 0, y, 0, 0, 1, z, 0, 0, 0, 1)
			: new Mat4(1, 0, 0, x, 0, 1, 0, y, 0, 0, 1, z, 0, 0, 0, 1);
		var rig = new Hd2Rig([
			new Hd2RigBone(0, "l_clavicle", 0, Pose(-0.07, 0, 1.47, rotated: true)),
			new Hd2RigBone(1, "l_shoulder", 0, Pose(-0.21, 0, 1.47, rotated: false)),
			new Hd2RigBone(2, "r_clavicle", 2, Pose(0.07, 0, 1.47, rotated: false)),
			new Hd2RigBone(3, "r_shoulder", 2, Pose(0.21, 0, 1.47, rotated: false)),
		]);
		var model = new[]
		{
			new IasRigNode("l_shoulder", -1, 0, 0, 0, Pose(-0.10, -0.02, 1.57, rotated: false)),
			new IasRigNode("r_shoulder", -1, 0, 0, 0, Pose(0.10, -0.02, 1.57, rotated: false)),
		};

		var targets = IasArmPose.ComputeShoulderTargets(rig, model);

		// 世界位移 (-0.03, -0.02, +0.10) 在转过 90° 的锁骨里 = 局部 (-0.02, +0.03, +0.10)。
		AssertNear(new Vec3(-0.02, 0.03, 0.10), targets.Left);
		AssertNear(new Vec3(0.03, -0.02, 0.10), targets.Right);
		AssertNear(new Vec3(0.11, -0.02, 0.10), targets.LeftShift);
		// 用锁骨的世界变换把目标放回去，必须正好落在模型自己的肩关节上。
		AssertNear(model[0].World.Origin, rig.Find("l_clavicle")!.WorldBind.TransformPoint(targets.Left));

		var json = IasDp8Converter.SerializeArmPose(targets);
		using var document = JsonDocument.Parse(json);
		var left = document.RootElement.GetProperty("armShoulderTarget").GetProperty("l");
		Assert.Equal(3, left.GetArrayLength());
		Assert.Equal(0.1, left[2].GetDouble(), 5);

		static void AssertNear(Vec3 expected, Vec3 actual)
		{
			Assert.Equal(expected.X, actual.X, 6);
			Assert.Equal(expected.Y, actual.Y, 6);
			Assert.Equal(expected.Z, actual.Z, 6);
		}
	}

	[Fact]
	public void ArmBoneShiftsCancelExactlyWhenTheShoulderIsMovedBack()
	{
		static Mat4 At(double x, double y, double z) => new(1, 0, 0, x, 0, 1, 0, y, 0, 0, 1, z, 0, 0, 0, 1);
		// 潜兵：l_clavicle(0) → l_shoulder(1) → l_shoulder_twist(2) / l_elbow(3)；右侧同理；chest(8) 不在手臂链里。
		var rig = new Hd2Rig([
			new Hd2RigBone(0, "l_clavicle", 8, At(-0.07, 0, 1.47)),
			new Hd2RigBone(1, "l_shoulder", 0, At(-0.21, 0, 1.47)),
			new Hd2RigBone(2, "l_shoulder_twist", 1, At(-0.31, 0, 1.40)),
			new Hd2RigBone(3, "l_elbow", 1, At(-0.47, 0, 1.32)),
			new Hd2RigBone(4, "r_clavicle", 8, At(0.07, 0, 1.47)),
			new Hd2RigBone(5, "r_shoulder", 4, At(0.21, 0, 1.47)),
			new Hd2RigBone(6, "r_elbow", 5, At(0.47, 0, 1.32)),
			new Hd2RigBone(7, "r_extra", 6, At(0.60, 0, 1.30)),
			new Hd2RigBone(8, "chest", 8, At(0, 0, 1.34)),
		]);
		var model = new[]
		{
			new IasRigNode("l_shoulder", -1, 0, 0, 0, At(-0.10, 0, 1.57)),
			// 模型的扭转骨在肘部：位置语义不同，必须忽略。
			new IasRigNode("l_shoulder_twist", 0, 0, 0, 0, At(-0.35, 0, 1.39)),
			new IasRigNode("l_elbow", 0, 0, 0, 0, At(-0.36, 0, 1.39)),
			new IasRigNode("r_shoulder", -1, 0, 0, 0, At(0.10, 0, 1.57)),
			new IasRigNode("r_elbow", 3, 0, 0, 0, At(0.36, 0, 1.39)),
		};

		var shifts = IasArmPose.ComputeBoneShifts(rig, model);

		Assert.Equal(new[] { 1, 2, 3, 5, 6, 7 }, shifts.Keys.Order());
		Assert.Equal(-0.11, shifts[1].X, 6);
		Assert.Equal(-0.10, shifts[1].Z, 6);
		Assert.Equal(shifts[1], shifts[2]);                 // 扭转骨沿用肩
		Assert.Equal(-0.11, shifts[3].X, 6);                // 肘：潜兵 − 模型
		Assert.Equal(-0.07, shifts[3].Z, 6);
		Assert.Equal(shifts[6], shifts[7]);                 // 模型没有的骨沿用父骨

		// 肩顶的混合顶点：0.4 胸 + 0.6 肩。运行时肩挪回去，退回 0.6 × 肩平移，正好抵消。
		var byHash = new Dictionary<uint, Vec3> { [11] = shifts[1], [13] = shifts[3] };
		var blended = IasArmPose.BlendShift([new(99, 0.4f), new(11, 0.6f)], byHash);
		var undone = blended - shifts[1] * 0.6f;
		Assert.True(undone.Length < 1e-6, $"残差 {undone.Length}");
	}

	[Fact]
	public void LegTargetsPutEveryJointBackOnTheModelAndShiftsCancel()
	{
		static Mat4 At(double x, double y, double z) => new(1, 0, 0, x, 0, 1, 0, y, 0, 0, 1, z, 0, 0, 0, 1);
		// hips 绕 Z 转 90°：目标必须是父骨 bind 朝向下的局部坐标，而不是世界差值。
		var hips = new Mat4(0, -1, 0, 0, 1, 0, 0, -0.02, 0, 0, 1, 0.97, 0, 0, 0, 1);
		var bones = new List<Hd2RigBone> { new(0, "hips", 0, hips) };
		var model = new List<IasRigNode>();
		foreach (var (side, sign) in new[] { ("l", -1.0), ("r", 1.0) })
		{
			var b = bones.Count;
			bones.Add(new(b, side + "_thigh", 0, At(sign * 0.087, -0.019, 0.94)));
			bones.Add(new(b + 1, side + "_thigh_twist", b, At(sign * 0.10, -0.02, 0.75)));
			bones.Add(new(b + 2, side + "_knee", b, At(sign * 0.1445, -0.0345, 0.516)));
			bones.Add(new(b + 3, side + "_foot", b + 2, At(sign * 0.19, -0.1175, 0.104)));
			bones.Add(new(b + 4, side + "_ball", b + 3, At(sign * 0.194, 0.011, 0.008)));
			bones.Add(new(b + 5, side + "_toe", b + 4, At(sign * 0.197, 0.131, 0.006)));
			model.Add(new(side + "_thigh", -1, 0, 0, 0, At(sign * 0.082, 0.004, 1.065)));
			model.Add(new(side + "_knee", -1, 0, 0, 0, At(sign * 0.082, 0.005, 0.6235)));
			model.Add(new(side + "_foot", -1, 0, 0, 0, At(sign * 0.082, -0.038, 0.127)));
			model.Add(new(side + "_ball", -1, 0, 0, 0, At(sign * 0.082, 0.0995, 0.062)));
			model.Add(new(side + "_toe", -1, 0, 0, 0, At(sign * 0.082, 0.148, 0.039)));
		}
		var rig = new Hd2Rig(bones);

		var shifts = IasArmPose.ComputeLegShifts(rig, model);
		var targets = IasArmPose.ComputeLegTargets(rig, model);

		Assert.DoesNotContain(0, shifts.Keys);              // hips 不在腿链里
		Assert.Equal(shifts[1], shifts[2]);                 // 扭转骨沿用 thigh
		Assert.Equal(shifts[5], shifts[6]);                 // toe 沿用 ball，不单独归位
		Assert.Equal(8, targets.Count);
		Assert.DoesNotContain("l_toe", targets.Keys);
		// 按 thigh → knee → foot → ball 依次把局部目标放回去，每根骨都落在模型关节上，
		// 且挪动量正好是 −平移，顶点逐一抵消。
		var modelAt = model.ToDictionary(node => node.Name, node => node.World.Origin);
		foreach (var side in new[] { "l", "r" })
		{
			var parentAt = rig.Find("hips")!.WorldOrigin;
			foreach (var name in IasArmPose.LegTargetBones.Select(bone => side + "_" + bone))
			{
				var own = rig.Find(name)!;
				var parent = rig.Bones[own.ParentIndex];
				var placed = parentAt + (parent.WorldBind.TransformPoint(targets[name]) - parent.WorldOrigin);
				var miss = placed - modelAt[name];
				Assert.True(miss.Length < 1e-6, $"{name} 偏 {miss.Length}");
				var undone = own.WorldOrigin - placed - shifts[own.Index];
				Assert.True(undone.Length < 1e-6, $"{name} 残差 {undone.Length}");
				parentAt = placed;
			}
		}

		var json = IasDp8Converter.SerializeArmPose(new(default, default, default, default), targets);
		using var document = JsonDocument.Parse(json);
		Assert.Equal(3, document.RootElement.GetProperty("legJointTarget").GetProperty("r_knee").GetArrayLength());
	}

	[Fact]
	public void ShoulderTargetRefusesASkeletonWithoutShoulders()
	{
		var rig = new Hd2Rig([new Hd2RigBone(0, "l_clavicle", 0, Mat4.Identity)]);
		Assert.Throws<InvalidDataException>(() => IasArmPose.ComputeShoulderTargets(rig, []));
	}

	private static void WriteName(BinaryWriter writer, string name)
	{
		var bytes = Encoding.UTF8.GetBytes(name);
		writer.Write(bytes);
		writer.Write(new byte[64 - bytes.Length]);
	}

	private static void WriteMatrix(BinaryWriter writer, float z)
	{
		foreach (var value in new[] { 1f, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, z })
			writer.Write(value);
	}
}
