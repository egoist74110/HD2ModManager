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

	/// <summary>两条手臂的根：每条肩以下（含手指）整棵子树都改名。</summary>
	internal static readonly string[] ArmRoots = ["l_shoulder", "r_shoulder"];

	/// <summary>
	/// 要改名的骨：<paramref name="roots"/> 下面所有有名字的骨，父先子后。
	/// 实验 1 只改了肩到手 5 根，手指留在 avatar 的手上，被拉成"鸡爪"——所以一条链改名就整棵子树都改。
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
	/// 返回改过的 avatar 骨架：<paramref name="bones"/> 里每根骨换成新名字的哈希，
	/// 全局矩阵的平移换成模型关节位置（旋转不变），局部平移按新的父骨全局矩阵重算。
	/// 扭转骨和模型里没有的骨：保持它和父骨在潜兵骨架里的相对位置。
	/// 按父先子后的顺序处理，所以父骨也改过时子骨相对的是改过的父骨。
	/// </summary>
	internal static UnitTransformInfo Apply(UnitTransformInfo avatar, Hd2Rig target, IReadOnlyList<IasRigNode> modelNodes,
		IReadOnlyList<string> bones, Func<string, uint> hash)
	{
		var model = modelNodes.GroupBy(node => node.Name, StringComparer.Ordinal)
			.ToDictionary(group => group.Key, group => group.First().World.Origin, StringComparer.Ordinal);
		var chosen = bones.Select(name => target.Find(name) ?? throw new InvalidDataException($"潜兵骨架缺少 {name}。"))
			.OrderBy(bone => Depth(target, bone.Index)).ToArray();

		var origins = new Dictionary<int, Vec3>();
		foreach (var bone in chosen)
		{
			var parent = target.Bones[bone.ParentIndex];
			var parentAt = origins.TryGetValue(parent.Index, out var moved) ? moved : parent.WorldOrigin;
			origins[bone.Index] = !bone.Name!.Contains("twist", StringComparison.Ordinal) && model.TryGetValue(bone.Name!, out var own)
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
		foreach (var bone in chosen)
		{
			var parentWorld = Hd2RigReader.FromHd2RowVector(matrices[bone.ParentIndex].Values);
			var local = parentWorld.Inverse().TransformPoint(origins[bone.Index]);
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
