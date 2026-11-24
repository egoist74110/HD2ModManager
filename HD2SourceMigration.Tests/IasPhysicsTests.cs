using System.Buffers.Binary;
using System.Text;
using HD2ModAdaptation.PatchReconstruction;
using HD2SourceMigration.IasConversion;
using HD2SourceModel;

namespace HD2SourceMigration.Tests;

public sealed class IasPhysicsTests
{
	[Fact]
	public void ReadsSampleHeadAndBodyWhenFixtureDirectoryIsProvided()
	{
		var directory = Environment.GetEnvironmentVariable("HD2_IAS_PHYSICS_FIXTURES");
		if (string.IsNullOrWhiteSpace(directory))
			return;
		var fixtures = Directory.EnumerateFiles(directory, "*.hd2phys")
			.Select(path => IasPhysicsReader.Read(File.ReadAllBytes(path))).ToDictionary(source => source.UnitId);
		Assert.Equal(2, fixtures.Count);
		AssertCounts(fixtures[0x1c0c131d96ecbb19], 41, 234);
		AssertCounts(fixtures[0x1a7498ba25211a65], 15, 62);

		static void AssertCounts(IasPhysicsSource source, int chains, int simulated)
		{
			Assert.Equal(chains, source.Chains.Count);
			Assert.Equal(simulated, source.Chains.Sum(chain => chain.Joints.Count - 1));
		}
	}

	[Fact]
	public void ReadsOneDriverAndOneSimulatedJointAndChecksCrc()
	{
		var bytes = Fixture();
		var source = IasPhysicsReader.Read(bytes);
		Assert.Equal(41ul, source.UnitId);
		var chain = Assert.Single(source.Chains);
		Assert.Equal("Back.001", chain.Name);
		Assert.Equal(new Vec3(0, 0, -4), chain.Gravity);
		Assert.Equal("head", chain.Joints[0].Name);
		Assert.Equal(IasLimit.Cone, chain.Joints[1].Limit);
		Assert.Equal(0.025f, chain.Joints[1].Radius);
		bytes[^1] ^= 1;
		Assert.Throws<InvalidDataException>(() => IasPhysicsReader.Read(bytes));
	}

	[Fact]
	public void ConvertsToMasterSpringRigUnitsAndFlags()
	{
		var joint = new IasPhysicsJoint("Bang_R", 11, .025f, IasLimit.Cone, .5f, 0, 2, 1);
		var chain = new IasPhysicsChain("Bang_R", 6, .5f, new(0, 0, -4), [joint]);
		var spring = IasPhysicsMigration.ConvertParameters(chain, joint, 1f / 120, .2f);
		Assert.Equal(12, spring.YawStiffness);
		Assert.Equal(.2, spring.Length * .01905, 6);
		Assert.Equal(4, spring.TipMass * .01905, 5);
		Assert.Equal(.5, spring.AngleLimit);
		Assert.Equal(SourceJiggleBone.Flexible | SourceJiggleBone.LengthConstrained | SourceJiggleBone.AngleConstrained,
			spring.Flags);
	}

	internal static async Task<HashSet<uint>> ReadSpringHashesAsync(string patch)
	{
		var container = await PhysBoneCarrier.TryReadContainerFromFileAsync(patch + ".gpu_resources");
		if (container is null)
			return [];
		const int record = 48;
		var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(container.AsSpan(record));
		Assert.Equal(PhysBoneCarrier.RigRecordName, Encoding.UTF8.GetString(container, record + 56, nameLength));
		var offset = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(container.AsSpan(record + 4)));
		var length = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(container.AsSpan(record + 12)));
		return ReadSpringHashes(container.AsSpan(offset, length));
	}

	private static HashSet<uint> ReadSpringHashes(ReadOnlySpan<byte> rig)
	{
		Assert.True(rig.StartsWith(PhysBoneSpringRig.Magic));
		Assert.Equal(PhysBoneSpringRig.Version, BinaryPrimitives.ReadUInt32LittleEndian(rig[8..]));
		var count = BinaryPrimitives.ReadInt32LittleEndian(rig[12..]);
		var nameTable = BinaryPrimitives.ReadInt32LittleEndian(rig[16..]);
		var size = BinaryPrimitives.ReadInt32LittleEndian(rig[24..]);
		var hashes = new HashSet<uint>();
		for (var i = 0; i < count; i++)
		{
			var record = rig[(32 + i * size)..];
			var name = rig[(nameTable + BinaryPrimitives.ReadInt32LittleEndian(record[8..]))..];
			var bone = Encoding.UTF8.GetString(name[..name.IndexOf((byte)0)]);
			Assert.StartsWith("jiggle_", bone, StringComparison.Ordinal);
			Assert.True(hashes.Add(Hd2NameHash.Of(bone)));
			Assert.InRange(BinaryPrimitives.ReadInt32LittleEndian(record[4..]), -1, i - 1);
			Assert.InRange(BinaryPrimitives.ReadUInt32LittleEndian(record[16..]), 0u, 127u);
		}
		Assert.NotEmpty(hashes);
		return hashes;
	}

	private static byte[] Fixture()
	{
		const int size = 80 + 120 + 2 * 164;
		using var stream = new MemoryStream(new byte[size]);
		using var writer = new BinaryWriter(stream);
		writer.Write("HD2PHY1\0"u8);
		foreach (var value in new[] { 1, 80, size, 0 }) writer.Write(value);
		writer.Write(41ul);
		writer.Write(0ul);
		foreach (var value in new[] { 2, 2, 1, 2, 0, 0, 0 }) writer.Write(value);
		writer.Write(1f / 120);
		stream.Position = 80;
		writer.Write("Back.001"u8);
		stream.Position = 144;
		writer.Write(0);
		writer.Write(2);
		writer.Write(6f);
		writer.Write(.4f);
		stream.Position = 168;
		writer.Write(-4f);
		stream.Position = 200;
		writer.Write("head"u8);
		stream.Position = 364;
		writer.Write("Back.001"u8);
		stream.Position = 436;
		writer.Write(1);
		writer.Write(.025f);
		writer.Write((int)IasLimit.Cone);
		writer.Write(.5f);
		stream.Position = 20;
		writer.Write(IasRigReader.Crc32(stream.ToArray().AsSpan(80)));
		return stream.ToArray();
	}
}
