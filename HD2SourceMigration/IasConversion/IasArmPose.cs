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
	internal static IReadOnlyDictionary<int, Vec3> ComputeBoneShifts(Hd2Rig target, IReadOnlyList<IasRigNode> modelNodes) =>
		ComputeChainShifts(target, modelNodes, ["l_shoulder", "r_shoulder"],
			name => !name.Contains("twist", StringComparison.Ordinal));

	/// <summary>
	/// 腿部运行时归位的骨：每帧把它们的局部位置写回模型自己的关节（<see cref="ComputeLegTargets"/>）。
	/// 和手臂不同，腿要整条回到模型的转轴上——潜兵的膝、踝比模型低 11 cm 且偏外，膝盖会在小腿中段弯。
	/// <c>toe</c> 不在里面：它沿用 <c>ball</c> 的平移，省一次写入。
	/// </summary>
	internal static readonly string[] LegTargetBones = ["thigh", "knee", "foot", "ball"];

	/// <summary>
	/// 腿部链每根潜兵骨的平移量，规则同 <see cref="ComputeBoneShifts"/>，
	/// 但只有 <see cref="LegTargetBones"/> 用自己的平移，其余（toe、扭转骨、模型没有的骨）沿用父骨——
	/// 只有这样，运行时只挪这几根骨时每个顶点才逐一抵消。
	/// </summary>
	internal static IReadOnlyDictionary<int, Vec3> ComputeLegShifts(Hd2Rig target, IReadOnlyList<IasRigNode> modelNodes) =>
		ComputeChainShifts(target, modelNodes, ["l_thigh", "r_thigh"], IsLegTarget);

	private static bool IsLegTarget(string name) =>
		name.Length > 2 && name[1] == '_' && LegTargetBones.Contains(name[2..], StringComparer.Ordinal);

	private static IReadOnlyDictionary<int, Vec3> ComputeChainShifts(Hd2Rig target, IReadOnlyList<IasRigNode> modelNodes,
		IReadOnlyList<string> roots, Func<string, bool> ownShift)
	{
		var model = ModelOrigins(modelNodes);
		var shifts = new Dictionary<int, Vec3>();
		foreach (var rootName in roots)
		{
			var root = target.Find(rootName)
				?? throw new InvalidDataException($"潜兵骨架缺少 {rootName}。");
			if (!model.TryGetValue(root.DisplayName, out var rootModel))
				throw new InvalidDataException($"模型骨架缺少 {rootName}。");
			shifts[root.Index] = root.WorldOrigin - rootModel;
			// 父子下标不保证有序：按层展开。
			var frontier = new Queue<int>([root.Index]);
			while (frontier.TryDequeue(out var parent))
			{
				foreach (var child in target.Bones.Where(bone => bone.ParentIndex == parent && bone.Index != parent))
				{
					var own = child.HasName && ownShift(child.Name!)
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

	/// <summary>
	/// 腿部归位目标：骨名 → 在潜兵父骨 bind 朝向下的局部位置（米）。
	/// 父骨也在归位表里时，相对的是父骨归位后的位置（即模型的父关节），
	/// 所以按 thigh → knee → foot → ball 依次写回，静止姿势下每根骨都正好落在模型关节上。
	/// </summary>
	internal static IReadOnlyDictionary<string, Vec3> ComputeLegTargets(Hd2Rig target, IReadOnlyList<IasRigNode> modelNodes)
	{
		var model = ModelOrigins(modelNodes);
		var targets = new Dictionary<string, Vec3>(StringComparer.Ordinal);
		foreach (var side in new[] { "l", "r" })
		{
			foreach (var bone in LegTargetBones)
			{
				var name = side + "_" + bone;
				var own = target.Find(name) ?? throw new InvalidDataException($"潜兵骨架缺少 {name}。");
				if (!model.TryGetValue(name, out var ownModel))
					throw new InvalidDataException($"模型骨架缺少 {name}。");
				var parent = target.Bones[own.ParentIndex];
				var parentAt = parent.HasName && IsLegTarget(parent.Name!) && model.TryGetValue(parent.Name!, out var parentModel)
					? parentModel
					: parent.WorldOrigin;
				// WorldBind⁻¹ 去掉父骨的 bind 平移和朝向；先把「相对父骨的位移」放回父骨 bind 原点上。
				targets[name] = parent.WorldBind.Inverse().TransformPoint(ownModel - parentAt + parent.WorldOrigin);
			}
		}
		return targets;
	}

	private static Dictionary<string, Vec3> ModelOrigins(IReadOnlyList<IasRigNode> modelNodes) =>
		modelNodes.GroupBy(node => node.Name, StringComparer.Ordinal)
			.ToDictionary(group => group.Key, group => group.First().World.Origin, StringComparer.Ordinal);

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
