using HD2SourceMigration.IasConversion;
using HD2SourceModel;

namespace HD2SourceMigration.Tests;

public sealed class IasArmBindAlignmentTests
{
	[Theory]
	[InlineData("r_shoulder")]
	[InlineData("l_shoulder_twist")]
	[InlineData("l_elbow")]
	[InlineData("r_hand_twist")]
	[InlineData("l_hand")]
	[InlineData("r_thumb_finger1")]
	[InlineData("l_index_finger2")]
	[InlineData("r_middle_finger3")]
	[InlineData("l_ring_finger1")]
	[InlineData("r_pinky_finger3")]
	public void MapsSourceBindToAvatarAndPreservesUnalignedInfluence(string name)
	{
		var source = Translation(2, 0, 0);
		var avatar = new Mat4(0, -2, 0, 0, 1, 0, 0, 3, 0, 0, 1, 0, 0, 0, 0, 1);
		var alignment = Create(name, source, avatar);
		var point = new Vec3(3, 0, 0);
		var normal = new Vec3(1, 1, 0).Normalized();
		var mappedNormal = new Vec3(-0.5, 1, 0);

		Assert.Equal(avatar.Origin, alignment.Transform(source.Origin, normal, [(7, 1)]).Position);
		var full = alignment.Transform(point, normal, [(7, 1)]);
		Assert.Equal(new Vec3(0, 4, 0), full.Position);
		AssertNear(mappedNormal.Normalized(), full.Normal);
		var mixed = alignment.Transform(point, normal, [(7, 0.125f), (8, 0.375f)]);
		Assert.Equal(new Vec3(2.25, 1, 0), mixed.Position);
		AssertNear(((normal * 0.75) + (mappedNormal * (normal.X * 0.25))).Normalized(), mixed.Normal);
	}

	[Theory]
	[InlineData("chest")]
	[InlineData("l_clavicle")]
	[InlineData("l_shoulderarmour")]
	[InlineData("jiggle_l_hand")]
	[InlineData("l_index_finger1v2")]
	public void LeavesOtherWeightedBonesUnchanged(string name)
	{
		var alignment = Create(name, Mat4.Identity, Translation(4, 7, 8));
		var point = new Vec3(2, 3, 4);
		var normal = new Vec3(0, 0, 1);
		Assert.Equal((point, normal), alignment.Transform(point, normal, [(7, 1)]));
	}

	[Fact]
	public void KeepsAValidNormalWhenOpposedInfluencesCancel()
	{
		var halfTurn = new Mat4(-1, 0, 0, 0, 0, -1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
		var alignment = Create("l_hand", Mat4.Identity, halfTurn);
		var normal = new Vec3(1, 0, 0);
		Assert.Equal(normal, alignment.Transform(Vec3.Zero, normal, [(7, 0.5f), (8, 0.5f)]).Normal);
	}

	[Fact]
	public void BlendsHandAndFingerInACommonWorldSpace()
	{
		var profile = new IasRigProfile(["l_hand", "l_thumb_finger1"],
			[new("l_hand", -1, 0, 7, 0, Mat4.Identity), new("l_thumb_finger1", 0, 1, 24, 0, Translation(2, 0, 0))]);
		var source = new Hd2RigBone[] { new(0, "l_hand", -1, Mat4.Identity), new(1, "l_thumb_finger1", 0, Translation(2, 0, 0)) };
		var avatar = new Hd2Rig([new(0, "l_hand", -1, Translation(0, 4, 0)), new(1, "l_thumb_finger1", 0, Translation(4, 4, 0))]);
		var alignment = new IasArmBindAlignment(profile, source, avatar);
		var point = new Vec3(3, 5, 7);
		var normal = new Vec3(0, 0, 1);
		Assert.Equal((new Vec3(4.5, 9, 7), normal), alignment.Transform(point, normal, [(7, 0.25f), (24, 0.75f)]));
		Assert.Equal((point, normal), alignment.Transform(point, normal, [(99, 1)]));
	}

	[Fact]
	public void BlendsAcrossChestShoulderAndForearmWithTheOriginalWeights()
	{
		var profile = new IasRigProfile(["chest", "l_shoulder", "l_elbow", "l_hand_twist"],
			[new("chest", -1, 0, 2, 0, Mat4.Identity), new("l_shoulder", 0, 1, 7, 0, Mat4.Identity),
			 new("l_elbow", 1, 2, 11, 0, Mat4.Identity), new("l_hand_twist", 2, 3, 24, 0, Mat4.Identity)]);
		var source = profile.Nodes.Select((node, i) => new Hd2RigBone(i, node.Name, node.Parent, node.World)).ToArray();
		var avatar = new Hd2Rig([new(0, "chest", -1, Translation(9, 9, 9)),
			new(1, "l_shoulder", 0, Translation(2, 0, 0)), new(2, "l_elbow", 1, Translation(0, 4, 0)),
			new(3, "l_hand_twist", 2, Translation(0, 0, 6))]);
		var alignment = new IasArmBindAlignment(profile, source, avatar);
		var normal = new Vec3(0, 0, 1);
		Assert.Equal((new Vec3(0.5, 1, 1.5), normal),
			alignment.Transform(Vec3.Zero, normal, [(2, .25f), (7, .25f), (11, .25f), (24, .25f)]));
	}

	private static IasArmBindAlignment Create(string name, Mat4 source, Mat4 avatar) =>
		new(new([name], [new(name, -1, 0, 7, 0, source)]),
			[new(0, name, -1, source)], new([new(0, name, -1, avatar)]));

	private static Mat4 Translation(double x, double y, double z) => new(1, 0, 0, x, 0, 1, 0, y, 0, 0, 1, z, 0, 0, 0, 1);

	private static void AssertNear(Vec3 expected, Vec3 actual) =>
		Assert.InRange(Vec3.Distance(expected, actual), 0, 1e-12);
}
