using System.Text;
using HD2SourceModel;

namespace HD2SourceMigration.IasConversion;

internal sealed record IasRigNode(string Name, int Parent, int SourceIndex, int Slot, uint Flags, Mat4 World);

internal sealed record IasRigProfile(IReadOnlyList<string> SourceNames, IReadOnlyList<IasRigNode> Nodes)
{
	internal int ResolveSemanticNode(int node)
	{
		var source = Nodes[node].SourceIndex;
		if (source < 0)
			throw new InvalidDataException($"IAS bone {Nodes[node].Name} has no source mapping.");
		for (var current = node; current >= 0; current = Nodes[current].Parent)
		{
			var candidate = Nodes[current];
			if ((candidate.Flags & 256) == 0 && candidate.SourceIndex == source && candidate.Name == SourceNames[source])
				return current;
		}
		throw new InvalidDataException($"IAS bone {Nodes[node].Name} has no ancestor for {SourceNames[source]}.");
	}
}

internal static class IasRigReader
{
	internal static IReadOnlyDictionary<ulong, IasRigProfile> Read(Stream stream)
	{
		using var input = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
		if (!input.ReadBytes(8).AsSpan().SequenceEqual("HD2IRG1\0"u8) || input.ReadUInt32() != 1 || input.ReadUInt32() != 32)
			throw new InvalidDataException("Expected an HD2IRG1 v1 rig.");
		var sourceCount = ReadCount(input, 256);
		var profileCount = ReadCount(input, 4096);
		var checksum = input.ReadUInt32();
		var extensions = input.ReadUInt32();
		using var data = new MemoryStream();
		stream.CopyTo(data);
		if (Crc32(data.GetBuffer().AsSpan(0, checked((int)data.Length))) != checksum)
			throw new InvalidDataException("HD2IRG1 payload CRC32 differs.");
		data.Position = 0;
		using var payload = new BinaryReader(data);

		var sourceNames = new string[sourceCount];
		for (var i = 0; i < sourceNames.Length; i++)
		{
			sourceNames[i] = ReadName(payload);
			_ = ReadParent(payload, i);
			_ = payload.ReadUInt32();
			_ = ReadMatrix(payload);
		}

		var profiles = new Dictionary<ulong, IasRigProfile>();
		for (var profileIndex = 0; profileIndex < profileCount; profileIndex++)
		{
			var unit = payload.ReadUInt64();
			var count = ReadCount(payload, 256);
			var needleLength = ReadCount(payload, checked((int)data.Length));
			_ = payload.ReadInt32();
			var slots = ReadCount(payload, 256);
			_ = payload.ReadUInt32();
			var nodes = new IasRigNode[count];
			var usedSlots = new HashSet<int>();
			for (var i = 0; i < count; i++)
			{
				var name = ReadName(payload);
				var parent = ReadParent(payload, i);
				var sourceIndex = payload.ReadInt32();
				var slot = ReadCount(payload, slots - 1);
				var flags = payload.ReadUInt32();
				_ = payload.ReadSingle();
				var local = ReadMatrix(payload);
				if (sourceIndex < -1 || sourceIndex >= sourceCount || !usedSlots.Add(slot))
					throw new InvalidDataException($"Invalid IAS target node {name} in {unit:x16}.");
				nodes[i] = new(name, parent, sourceIndex, slot, flags, parent < 0 ? local : nodes[parent].World * local);
			}
			if (payload.ReadBytes(needleLength).Length != needleLength)
				throw new EndOfStreamException("Truncated rig profile needle.");
			profiles.Add(unit, new(sourceNames, nodes));
		}
		if ((extensions & 1) != 0)
		{
			if (!payload.ReadBytes(8).AsSpan().SequenceEqual("HD2GRIP1"u8))
				throw new InvalidDataException("Invalid HD2IRG1 grip extension.");
			var count = ReadCount(payload, 4096);
			_ = payload.ReadUInt32();
			data.Position += count * 64;
		}
		// Bit 1 identifies a shared-v3 consumer rig and adds no bytes to HD2IRG1.
		if ((extensions & ~3u) != 0 || data.Position != data.Length)
			throw new InvalidDataException("Unexpected HD2IRG1 extension or trailing data.");
		return profiles;
	}

	internal static uint Crc32(ReadOnlySpan<byte> content)
	{
		var crc = uint.MaxValue;
		foreach (var value in content)
		{
			crc ^= value;
			for (var bit = 0; bit < 8; bit++)
				crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
		}
		return ~crc;
	}

	private static int ReadCount(BinaryReader input, int max)
	{
		var value = input.ReadInt32();
		return value >= 0 && value <= max ? value : throw new InvalidDataException($"Invalid rig count or index {value}.");
	}

	private static int ReadParent(BinaryReader input, int index)
	{
		var parent = input.ReadInt32();
		return parent >= -1 && parent < index ? parent : throw new InvalidDataException($"Invalid rig parent {parent} at node {index}.");
	}

	private static string ReadName(BinaryReader input)
	{
		var name = input.ReadBytes(64);
		var end = Array.IndexOf(name, (byte)0);
		return name.Length == 64 && end > 0 ? Encoding.UTF8.GetString(name, 0, end) : throw new InvalidDataException("Invalid rig bone name.");
	}

	private static Mat4 ReadMatrix(BinaryReader input)
	{
		var m = new float[12];
		for (var i = 0; i < m.Length; i++)
		{
			m[i] = input.ReadSingle();
			if (!float.IsFinite(m[i])) throw new InvalidDataException("Non-finite rig bind matrix.");
		}
		return new(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], 0, 0, 0, 1);
	}
}
