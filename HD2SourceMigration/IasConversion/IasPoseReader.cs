using HD2SourceModel;

namespace HD2SourceMigration.IasConversion;

internal sealed record IasPoseAxis(uint Flags, int SecondaryAxis, IReadOnlyList<float[]> Points);
internal sealed record IasPoseDriver(string Bone, string? Reference, float[] Neutral);
internal sealed record IasPoseRule(string Target, IasPoseDriver Primary, IasPoseDriver? Secondary,
	int Mode, float Influence, IReadOnlyList<IasPoseAxis> Axes);

internal static class IasPoseReader
{
	internal static (ulong Unit, Mat4 Basis, IReadOnlyList<IasPoseRule> Rules) Read(byte[] bytes,
		IReadOnlyDictionary<ulong, IasRigProfile> profiles)
	{
		using var input = new BinaryReader(new MemoryStream(bytes));
		const int headerSize = 120;
		if (bytes.Length < headerSize || !input.ReadBytes(8).AsSpan().SequenceEqual("HD2POSE1"u8) || input.ReadUInt32() != 1 ||
			input.ReadUInt32() != headerSize || input.ReadUInt32() != bytes.Length ||
			input.ReadUInt32() != IasRigReader.Crc32(bytes.AsSpan(headerSize)))
			throw new InvalidDataException("Invalid HD2POSE1 header or CRC32.");
		var count = input.ReadInt32();
		if (count is < 1 or > 64 || input.ReadUInt32() != 0)
			throw new InvalidDataException("Invalid HD2POSE1 rule count or reserved value.");
		var unit = input.ReadUInt64();
		if (!profiles.TryGetValue(unit, out var profile))
			throw new InvalidDataException($"Missing HD2POSE1 rig profile {unit:x16}.");
		var digest = input.ReadBytes(32);
		if (profile.PoseDigest is { } expected && !digest.AsSpan().SequenceEqual(expected))
			throw new InvalidDataException($"HD2POSE1 rig digest differs for {unit:x16}.");
		var basis = IasRigReader.ReadMatrix(input);
		string Name(int index) => (uint)index < profile.Nodes.Count ? profile.Nodes[index].Name
			: throw new InvalidDataException($"Invalid HD2POSE1 node index {index}.");
		string? Reference(int index) => index == -1 ? null : Name(index);
		float[] Neutral()
		{
			var w = input.ReadSingle();
			var q = new[] { input.ReadSingle(), input.ReadSingle(), input.ReadSingle(), w };
			if (q.Any(value => !float.IsFinite(value)) || Math.Abs(q.Sum(value => value * value) - 1) > 0.002)
				throw new InvalidDataException("Invalid HD2POSE1 neutral rotation.");
			return q;
		}
		var rules = new List<IasPoseRule>(count);
		var targets = new HashSet<string>(StringComparer.Ordinal);
		for (var i = 0; i < count; i++)
		{
			var source = Name(input.ReadInt32());
			var target = Name(input.ReadInt32());
			var secondary = input.ReadInt32();
			var reference = Reference(input.ReadInt32());
			var secondaryReference = Reference(input.ReadInt32());
			var mode = input.ReadInt32();
			var influence = input.ReadSingle();
			if (mode is < 0 or > 3 || !float.IsFinite(influence) || influence is < 0 or > 1 || !targets.Add(target))
				throw new InvalidDataException($"Invalid HD2POSE1 rule for {target}.");
			var primary = new IasPoseDriver(source, reference, Neutral());
			var secondaryNeutral = Neutral();
			var axes = new IasPoseAxis[3];
			for (var axis = 0; axis < axes.Length; axis++)
			{
				var flags = input.ReadUInt32();
				var secondaryAxis = input.ReadInt32();
				var points = input.ReadInt32();
				if (flags > 3 || secondaryAxis is < 0 or > 2 || points is < 2 or > 256 || input.ReadUInt32() != 0)
					throw new InvalidDataException($"Invalid HD2POSE1 curve for {target}.");
				var curve = new float[points][];
				for (var j = 0; j < points; j++)
				{
					var point = new[] { input.ReadSingle(), input.ReadSingle(), input.ReadSingle(), input.ReadSingle() };
					if (point.Any(value => !float.IsFinite(value) || value is < -180 or > 180) ||
						(j > 0 && point[0] <= curve[j - 1][0]))
						throw new InvalidDataException($"Invalid HD2POSE1 control point for {target}.");
					curve[j] = point;
				}
				axes[axis] = new(flags, secondaryAxis, curve);
			}
			rules.Add(new(target, primary, mode == 0 ? null : new(Name(secondary), secondaryReference, secondaryNeutral),
				mode, influence, axes));
		}
		if (input.BaseStream.Position != bytes.Length)
			throw new InvalidDataException("Unexpected HD2POSE1 trailing data.");
		return (unit, basis, rules);
	}
}
