using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2SourceMigration.Retarget;
using HD2SourceMigration.VpkConversion;
using HD2SourceModel;

namespace HD2SourceMigration.IasConversion;

internal sealed class IasPhysicsMigration
{
	internal const int GroupShift = 8;
	internal const int ChainStart = 1 << 12;
	private const float SourceUnit = 0.01905f;

	private sealed record Joint(IasPhysicsChain Chain, IasPhysicsJoint Parameters, int ChainIndex);
	private readonly Dictionary<int, Joint> joints = [];
	private readonly string prefix;
	private readonly IasRigProfile profile;
	private readonly float step;
	private RetargetExtraBone[] extras = [];
	private Dictionary<int, RetargetExtraBone> bySlot = [];

	internal IasPhysicsMigration(IasPhysicsSource source, ulong published, IasRigProfile profile, IasRigProfile canonical)
	{
		this.profile = profile;
		step = source.FixedStep;
		prefix = $"ias_phys_{source.UnitId:x16}_{published:x16}/";
		var canonicalBySlot = canonical.Nodes.ToDictionary(node => node.Slot);
		var consumerByName = profile.Nodes.Select((node, index) => (node.Name, index))
			.ToDictionary(item => item.Name, item => item.index, StringComparer.Ordinal);
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

	internal static int Group(string name)
	{
		if (name.StartsWith("Bang", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Ahoge", StringComparison.OrdinalIgnoreCase))
			return 1;
		if (name.StartsWith("ACC", StringComparison.OrdinalIgnoreCase))
			return 2;
		if (name.StartsWith("Breast", StringComparison.OrdinalIgnoreCase))
			return 3;
		if (name.StartsWith("Butt", StringComparison.OrdinalIgnoreCase))
			return 4;
		return 0;
	}

	internal void Plan(IReadOnlyList<Hd2RigBone> sourceBones, Hd2Rig target, RetargetResult geometry,
		IReadOnlyDictionary<int, Vec3>? shifts)
	{
		var selected = joints.Keys.ToHashSet();
		var resolved = new Dictionary<int, int>();
		int Native(int index)
		{
			var source = profile.ResolveSemanticNode(index);
			return geometry.Resolved.TryGetValue(source, out var native)
				? native : throw new InvalidDataException($"Missing IAS physics anchor for {profile.Nodes[index].Name}.");
		}

		var bones = new List<SourceBone>(sourceBones.Count);
		foreach (var bone in sourceBones)
		{
			var parent = bone.ParentIndex;
			var name = bone.DisplayName;
			if (selected.Contains(bone.Index))
			{
				resolved[bone.Index] = Native(bone.Index);
				name = prefix + name;
				if (parent >= 0 && !selected.Contains(parent))
					resolved[parent] = Native(parent);
			}
			bones.Add(new(bone.Index, name, parent, Mat4.Identity, bone.WorldBind, null));
		}
		extras = Retargeter.PlanJiggleBones(bones, resolved, target.Bones.Count, index =>
		{
			var anchor = Native(index);
			var position = sourceBones[index].WorldBind.Origin;
			if (geometry.Deform.TryGetValue(anchor, out var deform))
				position = deform.TransformPoint(position);
			return position + (shifts?.GetValueOrDefault(anchor) ?? Vec3.Zero);
		}, selected).ToArray();
		if (extras.Length != joints.Count)
			throw new InvalidDataException($"Only {extras.Length}/{joints.Count} IAS physics bones have a bind anchor.");
		bySlot = joints.Keys.ToDictionary(index => profile.Nodes[index].Slot,
			index => extras[resolved[index] - target.Bones.Count]);
	}

	internal uint? WeightTarget(int slot) => bySlot.GetValueOrDefault(slot)?.NameHash;

	internal UnitTransformInfo Append(UnitTransformInfo rig, IEnumerable<RetargetedMesh> lods, bool compact)
	{
		var combined = UnitTransformInfoAppender.Append(rig, extras)!;
		if (!compact || extras.Length == 0)
			return combined;

		// Keep the skin and physics nodes plus their native ancestors so the 256-node IAS limit still applies.
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

	internal void AddFacts(List<SourceJiggleBoneFact> facts, Hd2Rig target, IReadOnlySet<string> independent)
	{
		var start = facts.Count;
		var extrasByName = extras.ToDictionary(extra => extra.Name);
		var byName = joints.Values.ToDictionary(joint => prefix + joint.Parameters.Name);
		for (var i = 0; i < extras.Length; i++)
		{
			var extra = extras[i];
			var joint = byName[extra.Name];
			var parent = extra.ParentIndex - target.Bones.Count;
			var anchor = parent < 0 ? target.Bones[extra.ParentIndex].Name! : extras[parent].Name;
			if (independent.Contains(anchor))
				anchor = IasIndependentBones.Prefix + anchor;
			var next = joint.ChainIndex + 1;
			var neighbor = next < joint.Chain.Joints.Count
				? extrasByName.GetValueOrDefault(prefix + joint.Chain.Joints[next].Name) : null;
			var length = neighbor is not null
				? (neighbor.WorldOrigin - extra.WorldOrigin).Length
				: (extra.WorldOrigin - (parent < 0 ? target.Bones[extra.ParentIndex].WorldOrigin : extras[parent].WorldOrigin)).Length;
			var parameters = ConvertParameters(joint.Chain, joint.Parameters, step, (float)length, joint.ChainIndex == 1);
			facts.Add(new(start + i, extra.Name, parent < 0 ? -1 : start + parent, anchor,
				parameters.Flags, [], extra.WorldOrigin, parameters));
		}
	}

	internal static SourceJiggleBone ConvertParameters(IasPhysicsChain chain, IasPhysicsJoint joint, float step, float length, bool first)
	{
		var flags = SourceJiggleBone.Flexible | SourceJiggleBone.LengthConstrained | (Group(chain.Name) << GroupShift);
		if (first)
			flags |= ChainStart;
		var k = chain.Stiffness * joint.StiffnessScale;
		// Convert the discrete velocity retention into a continuous damping rate. A 100% loss is bounded.
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
