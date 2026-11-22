using HD2SourceModel;

namespace HD2SourceMigration.Retarget;

public sealed partial class Retargeter
{
	internal static RetargetResult RunNative(
		IReadOnlyList<Hd2RigBone> sourceBones,
		SourceGeometry geometry,
		IReadOnlyList<SourceVertexWeight[]> weights,
		Hd2Rig target,
		Vec3 sourceHead,
		bool refitArms = true)
	{
		var aliases = BodyBones.Concat(FingerPairs()).Concat(SnapToes())
			.ToDictionary(pair => pair.Hd2, pair => pair.Source, StringComparer.Ordinal);
		var bones = sourceBones.Select(bone =>
		{
			var name = aliases.TryGetValue(bone.DisplayName, out var alias)
				? RetargetConstants.SourceBonePrefix + alias : bone.DisplayName;
			return new SourceBone(bone.Index, name, bone.ParentIndex, bone.WorldBind.Inverse(), bone.WorldBind, null);
		}).ToArray();
		var document = new SourceModelDocument(0, "IAS", 0, bones, [], [], []);
		var options = new RetargetOptions
		{
			Alignment = RetargetAlignment.Identity,
			SpineMap = RetargetSpineMap.Legacy,
			Scale = 1,
			HeadOffset = 0,
			LegTilt = 0,
			LegSplay = 0,
			LegRefit = LegRefitMode.Bone,
			MeshShift = Vec3.Zero,
			HeadScale = 1,
			UpperScale = 1,
			IncludeJiggleBones = false,
			Fallback = RetargetFallback.Parent,
			FingerPose = false,
			HandReorient = HandReorientMode.Off,
			AxisDeltas = false,
			AxisRoundtrip = false,
			ArmScratch = false,
			ArmFit = refitArms ? 1.0 : 0.0,
			ArmRefitScratch = refitArms,
			ArmUp = 1,
			ArmPerp = 1,
			ShoulderFix = false,
			KneeBlend = false,
		};
		var retargeter = new Retargeter(document, geometry, target, null, options, null,
			sourceBones.Select(bone => bone.DisplayName).ToArray(), weights);
		retargeter._nativeHeadAnchor = sourceHead;
		return retargeter.Run();
	}

	private Vec3? _nativeHeadAnchor;
}
