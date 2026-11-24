using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2SourceMigration.Retarget;
using HD2SourceMigration.VpkConversion;
using HD2SourceModel;

namespace HD2SourceMigration.IasConversion;

internal sealed class IasPhysicsMigration
{
	private const float SourceUnit = 0.01905f;

	private sealed record Joint(IasPhysicsChain Chain, IasPhysicsJoint Parameters, int ChainIndex);
	private readonly Dictionary<int, Joint> joints = [];
	private readonly string prefix;
	private readonly IasRigProfile profile;
	private readonly float step;
	private RetargetExtraBone[] extras = [];
	private Dictionary<int, (int Index, RetargetExtraBone Bone)> bySlot = [];

	internal IasPhysicsMigration(IasPhysicsSource source, ulong published, IasRigProfile profile, IasRigProfile canonical)
	{
		this.profile = profile;
		step = source.FixedStep;
		// 每个发布 Unit 单独命名模拟骨，避免运行时按名字匹配到另一件护甲的链。
		prefix = $"jiggle_{published:x16}_";
		var consumerByName = profile.Nodes.Select((node, index) => (node.Name, index))
			.ToDictionary(item => item.Name, item => item.index, StringComparer.Ordinal);
		var canonicalBySlot = canonical.Nodes.ToDictionary(node => node.Slot);
		foreach (var chain in source.Chains)
		{
			for (var i = 0; i < chain.Joints.Count; i++)
			{
				var joint = chain.Joints[i];
				if (!canonicalBySlot.TryGetValue(joint.Slot, out var node) || node.Name != joint.Name)
					throw new InvalidDataException($"IAS physics slot {joint.Slot} does not identify {joint.Name} in {source.UnitId:x16}.");
				if (i > 0 && consumerByName.TryGetValue(joint.Name, out var index))
					joints.Add(index, new(chain, joint, i));
			}
		}
	}

	internal bool HasBones => joints.Count > 0;

	internal void Plan(IReadOnlyList<Hd2RigBone> sourceBones, Hd2Rig target, IReadOnlyDictionary<int, int> mapped)
	{
		var planned = new List<RetargetExtraBone>(joints.Count);
		bySlot = [];
		foreach (var bone in sourceBones)
		{
			if (!joints.ContainsKey(bone.Index))
				continue;
			var anchor = Native(bone.Index);
			var parent = bone.ParentIndex < 0 ? anchor :
				bySlot.TryGetValue(profile.Nodes[bone.ParentIndex].Slot, out var extraParent)
					? extraParent.Index : Native(bone.ParentIndex);
			var name = BoneName(joints[bone.Index]);
			var extra = new RetargetExtraBone(name, Hd2NameHash.Of(name), parent, bone.WorldOrigin, anchor);
			bySlot.Add(profile.Nodes[bone.Index].Slot, (target.Bones.Count + planned.Count, extra));
			planned.Add(extra);
		}
		extras = planned.ToArray();

		int Native(int index)
		{
			var semantic = profile.ResolveSemanticNode(index);
			return mapped.TryGetValue(semantic, out var native)
				? native : throw new InvalidDataException($"Missing IAS physics anchor for {profile.Nodes[index].Name}.");
		}
	}

	internal uint? WeightTarget(int slot) => bySlot.TryGetValue(slot, out var target) ? target.Bone.NameHash : null;

	/// <summary>保留有最终顶点权重的整条模拟链，以及它依赖的父链。</summary>
	internal void KeepWeightedChains(IReadOnlySet<uint> weighted, int nativeCount)
	{
		var chainsByName = joints.Values.ToDictionary(joint => BoneName(joint), joint => joint.Chain);
		var chainByIndex = extras.Select(extra => chainsByName.GetValueOrDefault(extra.Name)).ToArray();
		var keep = new HashSet<IasPhysicsChain>();
		var pending = new Queue<IasPhysicsChain>(extras.Select((extra, index) => (extra, index))
			.Where(pair => weighted.Contains(pair.extra.NameHash)).Select(pair => chainByIndex[pair.index]).OfType<IasPhysicsChain>());
		var members = Enumerable.Range(0, extras.Length).ToLookup(index => chainByIndex[index]);
		while (pending.TryDequeue(out var chain))
		{
			if (!keep.Add(chain))
				continue;
			foreach (var index in members[chain])
			{
				var parent = extras[index].ParentIndex - nativeCount;
				if (parent >= 0 && chainByIndex[parent] is { } parentChain && parentChain != chain)
					pending.Enqueue(parentChain);
			}
		}
		var retained = new List<RetargetExtraBone>();
		var remap = new int[extras.Length];
		for (var i = 0; i < extras.Length; i++)
		{
			if (chainByIndex[i] is not { } chain || !keep.Contains(chain))
				continue;
			var extra = extras[i];
			var parent = extra.ParentIndex - nativeCount;
			remap[i] = nativeCount + retained.Count;
			retained.Add(parent < 0 ? extra : extra with { ParentIndex = remap[parent] });
		}
		extras = retained.ToArray();
	}

	internal UnitTransformInfo Append(UnitTransformInfo rig, IEnumerable<RetargetedMesh> lods, bool compact)
	{
		var combined = UnitTransformInfoAppender.Append(rig, extras)!;
		if (!compact || extras.Length == 0)
			return combined;

		// 头盔只保留蒙皮、模拟骨和它们的原版祖先，以满足 Unit 的 256 节点上限。
		var used = lods.SelectMany(mesh => mesh.Vertices)
			.SelectMany(vertex => vertex.Influences).Select(weight => weight.BoneHash)
			.Concat(extras.Select(bone => bone.NameHash)).ToHashSet();
		var keep = new HashSet<int>();
		for (var i = 0; i < combined.NameHashes.Count; i++)
		{
			if (!used.Contains(combined.NameHashes[i]))
				continue;
			var current = i;
			while (keep.Add(current))
				current = combined.Entries[current].ParentIndex;
		}
		var indices = keep.Order().ToArray();
		var remap = indices.Select((oldIndex, index) => (oldIndex, index)).ToDictionary(pair => pair.oldIndex, pair => pair.index);
		return combined with
		{
			NameHashes = indices.Select(index => combined.NameHashes[index]).ToArray(),
			Matrices = indices.Select(index => combined.Matrices[index]).ToArray(),
			LocalTransforms = indices.Select(index => combined.LocalTransforms[index]).ToArray(),
			Entries = indices.Select(index => combined.Entries[index] with
			{
				ParentIndex = checked((ushort)remap[combined.Entries[index].ParentIndex]),
			}).ToArray(),
		};
	}

	internal void AddFacts(List<SourceJiggleBoneFact> facts, Hd2Rig target)
	{
		var extrasByName = extras.ToDictionary(extra => extra.Name);
		var byName = joints.Values.ToDictionary(BoneName);
		var factIndices = new Dictionary<int, int>();
		for (var i = 0; i < extras.Length; i++)
		{
			var extra = extras[i];
			if (!byName.TryGetValue(extra.Name, out var joint))
				continue;
			var parent = extra.ParentIndex - target.Bones.Count;
			var anchor = parent < 0 ? target.Bones[extra.ParentIndex].Name! : extras[parent].Name;
			var next = joint.ChainIndex + 1;
			var neighbor = next < joint.Chain.Joints.Count
				? extrasByName.GetValueOrDefault(BoneName(joint.Chain.Joints[next])) : null;
			var length = neighbor is not null
				? (neighbor.WorldOrigin - extra.WorldOrigin).Length
				: (extra.WorldOrigin - (parent < 0 ? target.Bones[extra.ParentIndex].WorldOrigin : extras[parent].WorldOrigin)).Length;
			var parameters = ConvertParameters(joint.Chain, joint.Parameters, step, (float)length);
			factIndices.Add(i, facts.Count);
			facts.Add(new(facts.Count, extra.Name, factIndices.GetValueOrDefault(parent, -1), anchor,
				parameters.Flags, [], extra.WorldOrigin, parameters));
		}
	}

	private string BoneName(Joint joint) => BoneName(joint.Parameters);

	private string BoneName(IasPhysicsJoint joint) => prefix + joint.Name;

	internal static SourceJiggleBone ConvertParameters(IasPhysicsChain chain, IasPhysicsJoint joint, float step, float length)
	{
		var flags = SourceJiggleBone.Flexible | SourceJiggleBone.LengthConstrained;
		var k = chain.Stiffness * joint.StiffnessScale;
		// 离散速度衰减换算为连续阻尼。完全衰减时保留一个下限以避免无穷大。
		var c = -MathF.Log(MathF.Max(0.001f, 1f - chain.Damping * joint.DampingScale)) / step;
		var cone = 0f;
		var yaw = 0f;
		var pitch = 0f;
		if (joint.Limit is IasLimit.Cone or IasLimit.Spherical)
		{
			flags |= SourceJiggleBone.AngleConstrained;
			cone = joint.Limit == IasLimit.Cone ? joint.Angle : MathF.Max(joint.Angle, joint.SecondAngle);
		}
		else if (joint.Limit == IasLimit.Hinge)
		{
			flags |= SourceJiggleBone.YawConstrained | SourceJiggleBone.PitchConstrained;
			yaw = MathF.Max(joint.Angle, joint.SecondAngle);
		}
		return new(flags, MathF.Max(0.001f, length) / SourceUnit, (float)Math.Max(0, -chain.Gravity.Z) / SourceUnit,
			k, c, k, c, 0, 0, cone, -yaw, yaw, 0, 0, -pitch, pitch, 0, 0,
			BaseMass: 0, BaseStiffness: 0, BaseDamping: 0, BaseMinLeft: 0, BaseMaxLeft: 0, BaseLeftFriction: 0,
			BaseMinUp: 0, BaseMaxUp: 0, BaseUpFriction: 0, BaseMinForward: 0, BaseMaxForward: 0, BaseForwardFriction: 0);
	}
}
