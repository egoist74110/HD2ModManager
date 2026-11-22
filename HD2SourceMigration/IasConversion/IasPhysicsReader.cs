using System.Buffers.Binary;
using System.Text;
using HD2SourceModel;

namespace HD2SourceMigration.IasConversion;

internal enum IasLimit : uint { None, Cone, Hinge, Spherical }

internal sealed record IasPhysicsJoint(string Name, int Slot, float Radius, IasLimit Limit,
	float Angle, float SecondAngle, float StiffnessScale, float DampingScale);

internal sealed record IasPhysicsChain(string Name, float Stiffness, float Damping, Vec3 Gravity,
	IReadOnlyList<IasPhysicsJoint> Joints);

internal sealed record IasPhysicsSource(ulong UnitId, float FixedStep, IReadOnlyList<IasPhysicsChain> Chains);

/// <summary>Reads the HD2PHY1 v1 wire layout. Only fields used by the migration are interpreted.</summary>
internal static class IasPhysicsReader
{
	private const int HeaderSize = 80;
	private const int ChainSize = 120;
	private const int JointSize = 164;
	private static readonly UTF8Encoding Utf8 = new(false, true);

	internal static IasPhysicsSource Read(ReadOnlySpan<byte> bytes)
	{
		if (bytes.Length < HeaderSize || !bytes[..8].SequenceEqual("HD2PHY1\0"u8) ||
			U32(bytes, 8) != 1 || U32(bytes, 12) != HeaderSize || U32(bytes, 16) != bytes.Length)
			throw new InvalidDataException("Invalid HD2PHY1 v1 header.");

		var unit = BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]);
		var palette = U32(bytes, 40);
		var bones = U32(bytes, 44);
		var chainCount = U32(bytes, 48);
		var jointCount = U32(bytes, 52);
		var colliders = U32(bytes, 56);
		var links = U32(bytes, 60);
		var contacts = U32(bytes, 64);
		var step = Real(bytes, 68, 1f / 240f, 1f / 15f);
		var features = U32(bytes, 72);
		if (unit == 0 || palette is < 1 or > 4096 || bones is < 1 or > 256 ||
			chainCount is < 1 or > 1024 || jointCount is < 2 or > 8192 ||
			colliders > 1024 || links > 1024 || contacts > 8192 || (features & ~2047u) != 0 || U32(bytes, 76) != 0)
			throw new InvalidDataException("Invalid HD2PHY1 table counts or flags.");
		var jointOffset = HeaderSize + (int)chainCount * ChainSize;
		var expected = jointOffset + jointCount * JointSize + colliders * 188 + links * 32 + contacts * 32
			+ ((features & 1024) != 0 ? jointCount * 8 : 0);
		if (expected != bytes.Length || IasRigReader.Crc32(bytes[HeaderSize..]) != U32(bytes, 20))
			throw new InvalidDataException("HD2PHY1 payload length or CRC32 differs.");

		var chains = new List<IasPhysicsChain>((int)chainCount);
		var names = new HashSet<string>(StringComparer.Ordinal);
		var simulated = new HashSet<int>();
		var drivers = new HashSet<int>();
		var cursor = 0;
		for (var i = 0; i < chainCount; i++)
		{
			var record = bytes.Slice(HeaderSize + i * ChainSize, ChainSize);
			var name = Name(record);
			var count = U32(record, 68);
			if (!names.Add(name) || U32(record, 64) != cursor || count is < 2 or > 64 ||
				cursor + count > jointCount || U32(record, 116) != 0)
				throw new InvalidDataException($"Invalid HD2PHY1 chain {name}.");
			var joints = new List<IasPhysicsJoint>((int)count);
			for (var j = 0; j < count; j++, cursor++)
			{
				var joint = bytes.Slice(jointOffset + cursor * JointSize, JointSize);
				var slot = (int)U32(joint, 72);
				var kind = (IasLimit)U32(joint, 80);
				var jointName = Name(joint);
				if (slot < 0 || slot >= palette || kind > IasLimit.Spherical
					|| (j > 0 && !simulated.Add(slot)) || U32(joint, 124) != 0 || U32(joint, 160) != 0)
					throw new InvalidDataException($"Invalid HD2PHY1 joint {jointName} in {name}.");
				if (j == 0)
					drivers.Add(slot);
				joints.Add(new(jointName, slot, Real(joint, 76, 0, 10), kind,
					Real(joint, 84, 0, MathF.PI), Real(joint, 88, 0, MathF.PI),
					Real(joint, 104, 0, 100), Real(joint, 108, 0, 100)));
			}
			chains.Add(new(name, Real(record, 72, 0, 1000), Real(record, 76, 0, 1),
				new(Real(record, 80), Real(record, 84), Real(record, 88)), joints));
		}
		if (cursor != jointCount || drivers.Overlaps(simulated))
			throw new InvalidDataException("Invalid HD2PHY1 driver or joint coverage.");
		return new(unit, step, chains);
	}

	private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);

	private static float Real(ReadOnlySpan<byte> data, int offset, float min = -float.MaxValue, float max = float.MaxValue)
	{
		var value = BinaryPrimitives.ReadSingleLittleEndian(data[offset..]);
		return float.IsFinite(value) && value >= min && value <= max ? value
			: throw new InvalidDataException($"Invalid HD2PHY1 float at record offset {offset}.");
	}

	private static string Name(ReadOnlySpan<byte> data)
	{
		var end = data[..64].IndexOf((byte)0);
		if (end < 1)
			throw new InvalidDataException("Invalid HD2PHY1 name.");
		try
		{
			return Utf8.GetString(data[..end]);
		}
		catch (DecoderFallbackException exception)
		{
			throw new InvalidDataException("Invalid HD2PHY1 UTF-8 name.", exception);
		}
	}
}
