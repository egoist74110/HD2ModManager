using HD2SourceModel;

namespace HD2SourceMigration.IasConversion;

internal sealed class IasArmBindAlignment
{
	private readonly Dictionary<int, (Mat4 Position, Mat4 Normal)> framesBySlot = [];

	internal IasArmBindAlignment(IasRigProfile profile, IReadOnlyList<Hd2RigBone> source, Hd2Rig avatar,
		IasPhysicsMigration? physics = null)
	{
		for (var i = 0; i < profile.Nodes.Count; i++)
		{
			var node = profile.Nodes[i];
			if (IsArmBone(node.Name) && physics?.WeightTarget(node.Slot) is null && avatar.Find(node.Name) is { } target)
			{
				var bind = source[i].WorldBind;
				var delta = target.WorldBind * bind.Inverse();
				framesBySlot.Add(node.Slot, (delta, delta.Inverse().Transposed()));
			}
		}
	}

	internal (Vec3 Position, Vec3 Normal) Transform(Vec3 position, Vec3 normal,
		IReadOnlyList<(int Slot, float Weight)> weights)
	{
		var positionDelta = Vec3.Zero;
		var normalDelta = Vec3.Zero;
		var total = 0.0;
		var affected = false;
		foreach (var (slot, weight) in weights)
		{
			total += weight;
			if (!framesBySlot.TryGetValue(slot, out var frame))
				continue;
			affected = true;
			positionDelta += (frame.Position.TransformPoint(position) - position) * weight;
			var transformedNormal = frame.Normal.TransformPoint(normal) - frame.Normal.Origin;
			normalDelta += (transformedNormal - normal) * weight;
		}

		// 躯干和物理槽位保留单位变换，肩部交界沿原有混权过渡。
		if (!affected)
			return (position, normal);
		var blendedNormal = normal + (normalDelta / total);
		return (position + (positionDelta / total), blendedNormal.Length > 1e-10 ? blendedNormal.Normalized() : normal);
	}

	// HD2 的 shoulder/elbow 分别是上臂和前臂，hand_twist 隶属 elbow。
	// 锁骨保留源位置，避免它的混权把绑定差值带到衣身和头颈。
	internal static bool IsArmBone(string name) => name.Length > 2 && name[..2] is ("l_" or "r_") &&
		(name.AsSpan(2) is "shoulder" or "shoulder_twist" or "elbow" or "hand_twist" or "hand" or
		 "thumb_finger1" or "thumb_finger2" or "thumb_finger3" or
		 "index_finger1" or "index_finger2" or "index_finger3" or
		 "middle_finger1" or "middle_finger2" or "middle_finger3" or
		 "ring_finger1" or "ring_finger2" or "ring_finger3" or
		 "pinky_finger1" or "pinky_finger2" or "pinky_finger3");
}
