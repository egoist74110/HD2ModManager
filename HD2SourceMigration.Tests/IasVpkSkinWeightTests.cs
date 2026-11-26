using System.Buffers.Binary;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using HD2SourceMigration.IasConversion;

namespace HD2SourceMigration.Tests;

public sealed class IasVpkSkinWeightTests
{
	[Theory]
	[InlineData(10800438u, 29u, 35u)]
	[InlineData(10800437u, 25u, 31u)]
	public void 接缝两侧以四槽半精度保留相同权重(uint version, uint packedFormat, uint halfFormat)
	{
		var indexFormat = halfFormat == 35 ? 28u : 24u;
		var packed = new UnitStreamInfo(0, 0, 0, 2, 0, 0, 8, 0, 0, 0, 0, 0, 0, 0,
			[new(6, "bone_index", indexFormat, "vec4_uint8", 0, 0, 4),
			 new(7, "bone_weight", packedFormat, "vec4_1010102", 0, 0, 4)]);
		var half = packed with
		{
			Index = 1, VertexStride = 12,
			Components = [packed.Components[0], packed.Components[1] with { Format = halfFormat, FormatName = "vec4_half", Size = 8 }],
		};
		var culling = packed with { Index = 2 };
		var scalar = packed with
		{
			Index = 3,
			Components = [packed.Components[0], packed.Components[1] with { Format = 0, FormatName = "float" }],
		};
		UnitMeshInfo Mesh(int index, int lod) => new(index, 0, 0, lod, 0, (uint)index, 0, 0, 0, 0,
			UnitMeshSemanticInfo.Empty(lod, index), [], []);
		var donor = new UnitMeshModel(version, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			UnitCustomizationInfo.Empty, [], [packed, half, culling, scalar], [Mesh(0, 0), Mesh(1, 0), Mesh(2, -1), Mesh(3, 0)], [], [], []);
		var streams = IasNativeMesh.UseFourWeightStreams(donor, onlyPacked: true).Streams;
		Assert.Same(half, streams[1]);
		Assert.Same(culling, streams[2]);
		Assert.Same(scalar, streams[3]);
		Assert.Equal(halfFormat, streams[0].Components[1].Format);

		// 同一接缝两侧的调色板槽号可以不同。第四槽代表随布料摆动的物理骨。
		UnitRawVertexRecord Vertex(uint i, uint[] indices, float[] weights) => new(i, [],
			[new(6, "bone_index", indexFormat, "vec4_uint8", 0, [], indices, []),
			 new(7, "bone_weight", halfFormat, "vec4_half", 0, weights, [], [])]);
		var a = Vertex(0, [11, 1, 2, 3, 4], [.5f, 8f, 4f, 3f, 1f]);
		var b = Vertex(0, [8, 5, 7, 12, 6], [1f, 3f, 8f, .5f, 4f]);
		var staticNeighbor = Vertex(1, [1, 0, 0, 0], [1f, 0f, 0f, 0f]);
		var movingNeighbor = Vertex(2, [4, 1, 0, 0], [.25f, .75f, 0f, 0f]);
		var triangle = new UnitTriangleIndices(0, 1, 2);
		UnitRawMeshData Raw(int stream, UnitRawVertexRecord seam) =>
			new(stream, 0, 0, (uint)stream, [new(0, 0, [triangle])], [triangle], [seam, staticNeighbor, movingNeighbor]);
		var encoder = new CanonicalMeshPreparation();
		var lossy = encoder.TryPrepare(Raw(0, a), packed);
		Assert.True(lossy.IsValid);
		var packedWeights = BinaryPrimitives.ReadUInt32LittleEndian(lossy.Mesh!.Vertices[0].Data.AsSpan(4));
		Assert.Equal(0u, packedWeights >> 30);

		var left = encoder.TryPrepare(Raw(0, a), streams[0]);
		var right = encoder.TryPrepare(Raw(1, b), streams[1]);
		Assert.True(left.IsValid);
		Assert.True(right.IsValid);
		var leftBytes = left.Mesh!.Vertices[0].Data;
		var rightBytes = right.Mesh!.Vertices[0].Data;
		Assert.Equal(leftBytes[4..], rightBytes[4..]);
		Assert.Equal((byte)4, leftBytes[3]);
		Assert.Equal((byte)8, rightBytes[3]);
		float[] Weights(UnitRawVertexRecord vertex) => Enumerable.Range(0, 4).Select(i =>
			(float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(vertex.Data.AsSpan(4 + 2 * i)))).ToArray();
		var weights = Weights(left.Mesh.Vertices[0]);
		Assert.Equal(1f, weights.Sum());
		Assert.Equal(.0625f, weights[3]);
		Assert.Equal(triangle, Assert.Single(left.Mesh.Triangles));

		// 物理骨移动 8 cm 时，接缝处的两份顶点同步移动 5 mm，
		// 同一三角形的静态顶点不动，另一端按四分之一的影响移动 20 mm。
		const float swing = .08f;
		Assert.Equal(.005f, Weights(right.Mesh.Vertices[0])[3] * swing);
		Assert.Equal(0f, Weights(left.Mesh.Vertices[1])[3] * swing);
		Assert.Equal(.02f, Weights(left.Mesh.Vertices[2])[1] * swing);
	}
}
