namespace HD2SourceMigration.Retarget;

public sealed partial class Retargeter
{
	// IAS already supplies native-space geometry. Resolve its named bones without
	// entering the generic rest-pose fitting pipeline.
	internal static IReadOnlyDictionary<int, int> RunNative(IReadOnlyList<Hd2RigBone> sourceBones, Hd2Rig target)
	{
		var mapped = new Dictionary<int, int>();
		foreach (var bone in sourceBones)
			if (target.Find(bone.DisplayName) is { } native)
				mapped.Add(bone.Index, native.Index);
		return mapped;
	}
}
