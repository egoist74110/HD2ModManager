using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2SourceModel;

namespace HD2SourceMigration.IasConversion;

/// <summary>
/// 独立骨（实验 1，见 docs/agent/ias-independent-bones-plan-20261122.md）。
/// <para>
/// 实机证实：护甲网格里和 avatar 同名的骨，蒙皮直接用角色动画，运行时写什么都不认，
/// 模型自己的关节位置也会被丢掉。把骨改个名（<see cref="Prefix"/> + 原名），引擎就不再把它
/// 对到 avatar 上，它变成普通场景节点：绑定里放模型自己的关节位置，旋转由运行时每帧从
/// avatar 同名骨抄过来。效果是「在模型自己的关节处做 avatar 的动作」。
/// </para>
/// </summary>
internal static class IasIndependentBones
{
	internal const string Prefix = "ias_";

	/// <summary>两条手臂的候选子树，从肩起，含手指。</summary>
	internal static readonly string[] ArmRoots = ["l_shoulder", "r_shoulder"];
	internal static readonly string[] BodyRoots = ["hips", "spine1"];
	internal static readonly IReadOnlySet<string> ModelAttachments = new HashSet<string>(StringComparer.Ordinal)
	{
		"attach_samplepouch", "attach_intelpad", "support_mg",
	};

	private static bool IsAttachment(string name) =>
		name.StartsWith("attach_", StringComparison.Ordinal) || name == "support_mg";

	/// <summary>
	/// 候选骨：<paramref name="roots"/> 下面所有有名字的骨，父先子后。
	/// </summary>
	internal static IReadOnlyList<string> Collect(Hd2Rig target, IEnumerable<string> roots) =>
		roots.SelectMany(root => Subtree(target, root))
			.Select(index => target.Bones[index])
			.Where(bone => bone.HasName)
			.OrderBy(bone => Depth(target, bone.Index)).ThenBy(bone => bone.Index)
			.Select(bone => bone.Name!)
			.Distinct(StringComparer.Ordinal)
			.ToArray();

	/// <summary>
	/// 按最终蒙皮权重和物理锚点选择骨：先闭合到肩的父链，再把选中骨下面整棵子树都带上。
	/// 实机（2026-11-23 E4）：只改名一部分（肩改名、肘/手指仍叫 avatar 原名挂在改名骨下面）→ 进飞船加载即崩。
	/// 所以一侧手臂要么整条改名，要么整条保持原名（和 E3 的结构一致）。
	/// </summary>
	internal static IReadOnlyList<string> SelectWeighted(Hd2Rig target, UnitTransformInfo renamed,
		IReadOnlyList<string> candidates, IReadOnlySet<int> arms, IReadOnlySet<uint> weighted,
		IEnumerable<int>? physicsParents)
	{
		var keep = new HashSet<int>();
		var used = arms.Where(index => weighted.Contains(renamed.NameHashes[index]));
		foreach (var index in used.Concat(physicsParents ?? []))
		{
			var current = index;
			while (arms.Contains(current) && keep.Add(current))
				current = target.Bones[current].ParentIndex;
		}
		for (var grew = true; grew;)
		{
			grew = false;
			foreach (var index in arms)
				if (!keep.Contains(index) && keep.Contains(target.Bones[index].ParentIndex)
					&& target.Bones[index].Name is { } name && !IsAttachment(name))
					grew |= keep.Add(index);
		}
		return candidates.Where(name => keep.Contains(target.Find(name)!.Index)).ToArray();
	}

	/// <summary>
	/// 返回改过的 avatar 骨架：<paramref name="bones"/> 里每根骨换成新名字的哈希，
	/// 全局矩阵的平移换成模型关节位置（旋转不变），局部平移按新的父骨全局矩阵重算。
	/// 模型里没有的骨：保持它和父骨在潜兵骨架里的相对位置。
	/// 按父先子后的顺序处理，所以父骨也改过时子骨相对的是改过的父骨。
	/// </summary>
	internal static UnitTransformInfo Apply(UnitTransformInfo avatar, Hd2Rig target, IReadOnlyList<IasRigNode> modelNodes,
		IReadOnlyList<string> bones, Func<string, uint> hash)
	{
		if (bones.Count == 0)
			return avatar;
		var model = modelNodes.GroupBy(node => node.Name, StringComparer.Ordinal)
			.ToDictionary(group => group.Key, group => group.First().World.Origin, StringComparer.Ordinal);
		var chosen = bones.Select(name => target.Find(name) ?? throw new InvalidDataException($"潜兵骨架缺少 {name}。"))
			.OrderBy(bone => Depth(target, bone.Index)).ToArray();

		var origins = new Dictionary<int, Vec3>();
		foreach (var bone in chosen)
		{
			var parent = target.Bones[bone.ParentIndex];
			var parentAt = origins.TryGetValue(parent.Index, out var moved) ? moved : parent.WorldOrigin;
			origins[bone.Index] = model.TryGetValue(bone.Name!, out var own)
				? own
				: parentAt + (bone.WorldOrigin - parent.WorldOrigin);
		}

		var hashes = avatar.NameHashes.ToArray();
		var matrices = avatar.Matrices.ToArray();
		var locals = avatar.LocalTransforms.ToArray();
		foreach (var bone in chosen)
		{
			var at = origins[bone.Index];
			var values = matrices[bone.Index].Values.ToArray();
			// HD2 行向量约定：平移在平坦下标 12/13/14。
			values[12] = (float)at.X;
			values[13] = (float)at.Y;
			values[14] = (float)at.Z;
			matrices[bone.Index] = new UnitTransformMatrix(values);
			hashes[bone.Index] = hash(Prefix + bone.Name);
		}
		foreach (var bone in target.Bones)
		{
			if (!origins.ContainsKey(bone.Index) && !origins.ContainsKey(bone.ParentIndex))
				continue;
			var parentWorld = Hd2RigReader.FromHd2RowVector(matrices[bone.ParentIndex].Values);
			var local = !origins.ContainsKey(bone.Index) && bone.Name is { } name && IsAttachment(name)
				? Hd2RigReader.FromHd2RowVector(avatar.Matrices[bone.ParentIndex].Values).Inverse().TransformPoint(bone.WorldOrigin)
				: parentWorld.Inverse().TransformPoint(origins.GetValueOrDefault(bone.Index, bone.WorldOrigin));
			if (!origins.ContainsKey(bone.Index) && bone.Name is { } attachment && IsAttachment(attachment))
			{
				var at = parentWorld.TransformPoint(local);
				var values = matrices[bone.Index].Values.ToArray();
				values[12] = (float)at.X;
				values[13] = (float)at.Y;
				values[14] = (float)at.Z;
				matrices[bone.Index] = new UnitTransformMatrix(values);
			}
			locals[bone.Index] = locals[bone.Index] with { Position = [(float)local.X, (float)local.Y, (float)local.Z] };
		}
		return avatar with { NameHashes = hashes, Matrices = matrices, LocalTransforms = locals };
	}

	/// <summary><paramref name="root"/> 及其全部后代在潜兵骨架里的下标（这些骨的顶点不再做平移）。</summary>
	internal static IReadOnlySet<int> Subtree(Hd2Rig target, string root)
	{
		var start = target.Find(root) ?? throw new InvalidDataException($"潜兵骨架缺少 {root}。");
		var result = new HashSet<int> { start.Index };
		var frontier = new Queue<int>([start.Index]);
		while (frontier.TryDequeue(out var parent))
			foreach (var child in target.Bones.Where(bone => bone.ParentIndex == parent && bone.Index != parent))
				if (result.Add(child.Index))
					frontier.Enqueue(child.Index);
		return result;
	}

	private static int Depth(Hd2Rig target, int index)
	{
		var depth = 0;
		// 根节点的父下标指向自己或越界。
		while (depth < target.Bones.Count)
		{
			var parent = target.Bones[index].ParentIndex;
			if (parent == index || parent < 0 || parent >= target.Bones.Count)
				break;
			index = parent;
			depth++;
		}
		return depth;
	}
}
