using HD2SourceModel;

namespace HD2SourceMigration.IasConversion;

/// <summary>
/// 运行时手臂修正要用的肩关节目标位置（锁骨局部坐标，米），由 Lua 每帧写回 <c>*_shoulder</c>。
/// </summary>
/// <param name="Left">左肩关节在潜兵 <c>l_clavicle</c> 局部坐标里应在的位置。</param>
/// <param name="Right">右肩关节在潜兵 <c>r_clavicle</c> 局部坐标里应在的位置。</param>
/// <param name="LeftShift">左肩关节相对潜兵原位要挪动的世界位移（只用于报告）。</param>
/// <param name="RightShift">右肩关节相对潜兵原位要挪动的世界位移（只用于报告）。</param>
public sealed record IasShoulderTargets(Vec3 Left, Vec3 Right, Vec3 LeftShift, Vec3 RightShift);

/// <summary>
/// 转换时手臂网格被整体平移到潜兵的肩关节上（潜兵肩更宽、更低），连接手臂和躯干的那段网格因此被拉长；
/// 手臂又绕着潜兵的肩关节转，就成了「焊接钢管」。运行时把肩关节放回模型自己骨架里的位置，
/// 网格随之复原，手臂改绕模型原来的转轴转。这里算出那个位置。
/// </summary>
internal static class IasArmPose
{
	/// <summary>
	/// 手臂链每根潜兵骨的平移量（潜兵关节位置 − 模型同名关节位置），键是潜兵骨下标。
	/// 顶点按自己的蒙皮权重混合这些平移挪到潜兵关节上（<see cref="BlendShift"/>）。
	/// 运行时把 <c>*_shoulder</c> 挪回模型位置时，每个顶点退回的量恰好是「肩权重 × 肩平移」，
	/// 和这里加上去的一一抵消——不会像按空间位置渐变的 ArmRefit 那样在肩顶留下凸起。
	/// 扭转骨（模型里的扭转骨位置语义不同）和模型没有的骨沿用父骨的平移。
	/// </summary>
	internal static IReadOnlyDictionary<int, Vec3> ComputeBoneShifts(Hd2Rig target, IReadOnlyList<IasRigNode> modelNodes)
	{
		var model = modelNodes.GroupBy(node => node.Name, StringComparer.Ordinal)
			.ToDictionary(group => group.Key, group => group.First().World.Origin, StringComparer.Ordinal);
		var shifts = new Dictionary<int, Vec3>();
		foreach (var side in new[] { "l", "r" })
		{
			var root = target.Find(side + "_shoulder")
				?? throw new InvalidDataException($"潜兵骨架缺少 {side}_shoulder。");
			if (!model.TryGetValue(root.DisplayName, out var rootModel))
				throw new InvalidDataException($"模型骨架缺少 {side}_shoulder。");
			shifts[root.Index] = root.WorldOrigin - rootModel;
			// 父子下标不保证有序：按层展开。
			var frontier = new Queue<int>([root.Index]);
			while (frontier.TryDequeue(out var parent))
			{
				foreach (var child in target.Bones.Where(bone => bone.ParentIndex == parent && bone.Index != parent))
				{
					var own = child.HasName && !child.Name!.Contains("twist", StringComparison.Ordinal)
						&& model.TryGetValue(child.Name!, out var origin)
						? child.WorldOrigin - origin
						: shifts[parent];
					shifts[child.Index] = own;
					frontier.Enqueue(child.Index);
				}
			}
		}
		return shifts;
	}

	/// <summary>顶点的平移 = Σ 权重 × 该骨平移；不在表里的骨（躯干、头、腿）平移为 0。</summary>
	internal static Vec3 BlendShift(IEnumerable<VpkConversion.RetargetedInfluence> influences, IReadOnlyDictionary<uint, Vec3> shiftByHash)
	{
		var sum = Vec3.Zero;
		foreach (var influence in influences)
			if (shiftByHash.TryGetValue(influence.BoneHash, out var shift))
				sum += shift * influence.Weight;
		return sum;
	}

	internal static IasShoulderTargets ComputeShoulderTargets(Hd2Rig target, IReadOnlyList<IasRigNode> modelNodes)
	{
		var (left, leftShift) = Side("l");
		var (right, rightShift) = Side("r");
		return new(left, right, leftShift, rightShift);

		(Vec3 Local, Vec3 Shift) Side(string side)
		{
			var clavicle = target.Find(side + "_clavicle")
				?? throw new InvalidDataException($"潜兵骨架缺少 {side}_clavicle。");
			var shoulder = target.Find(side + "_shoulder")
				?? throw new InvalidDataException($"潜兵骨架缺少 {side}_shoulder。");
			var model = modelNodes.FirstOrDefault(node => node.Name == side + "_shoulder")
				?? throw new InvalidDataException($"模型骨架缺少 {side}_shoulder。");
			var local = clavicle.WorldBind.Inverse().TransformPoint(model.World.Origin);
			return (local, model.World.Origin - shoulder.WorldOrigin);
		}
	}
}
