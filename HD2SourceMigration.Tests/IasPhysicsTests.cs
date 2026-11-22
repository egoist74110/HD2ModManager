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

	[Theory]
	[InlineData("Back_L.016", 0)]
	[InlineData("JacketStrap_01", 0)]
	[InlineData("Bang_R", 1)]
	[InlineData("Ahoge.001", 1)]
	[InlineData("ACC2", 2)]
	[InlineData("Breast.L", 3)]
	[InlineData("Butt.R", 4)]
	public void ClassifiesChain(string chain, int group) =>
		Assert.Equal(group, IasPhysicsMigration.Group(chain));

	[Fact]
	public void ConvertsToSpringRigUnitsAndPreservesConeAndGroup()
	{
		var joint = new IasPhysicsJoint("Bang_R", 11, .025f, IasLimit.Cone, .5f, 0, 2, 1);
		var chain = new IasPhysicsChain("Bang_R", 6, .5f, new(0, 0, -4), [joint]);
		var spring = IasPhysicsMigration.ConvertParameters(chain, joint, 1f / 120, .2f, first: true);
		Assert.Equal(12, spring.YawStiffness);
		Assert.Equal(.2, spring.Length * .01905, 6);
		Assert.Equal(4, spring.TipMass * .01905, 5);
		Assert.Equal(.5, spring.AngleLimit);
		Assert.Equal(1, spring.Flags >> IasPhysicsMigration.GroupShift & 15);
		Assert.NotEqual(0, spring.Flags & IasPhysicsMigration.ChainStart);
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
