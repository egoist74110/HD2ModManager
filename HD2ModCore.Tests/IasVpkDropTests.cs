using HD2ModCore.Ias;
using HD2ModManager.Services.Ias;
using Xunit;
using static HD2SourceModel.Tests.SourceFormatFixtures;

namespace HD2ModCore.Tests;

public sealed class IasVpkDropTests : IDisposable
{
    private readonly DirectoryInfo scratch = Directory.CreateTempSubdirectory("ias-vpk-tests-");

    [Fact]
    public async Task MaterialOnlyDropPromptsForAModelWithoutAttemptingConversion()
    {
        using var workspace = new IasDropWorkspace();
        var material = WriteVpk("textures", new VpkFileSpec("vmt", "materials/fixture", "surface", [], "VertexLitGeneric {}"u8.ToArray()));
        var input = await workspace.InspectAsync(material, default);
        var warnings = new List<string>();
        var plan = IasModuleService.PlanDroppedVpks(input.Vpks, warnings);

        Assert.Empty(plan.Groups);
        var result = await IasModuleService.AddDropConversionsAsync([], 0, warnings, plan.NoModelsMessage,
            (_, _) => throw new InvalidOperationException("不应转换单独的材质包"), default);
        Assert.False(result.Success);
        Assert.Contains("只有材质包，没有角色模型", result.Message);
        Assert.Contains("textures.vpk", Assert.Single(result.Warnings));
    }

    [Fact]
    public async Task ModelsFromSeparateDropItemsAreDeduplicatedAndShareTheirMaterialSource()
    {
        using var workspace = new IasDropWorkspace();
        var bill = WriteModel("bill", 3);
        var coach = WriteModel("coach", 3);
        var material = WriteVpk("textures",
            new("vmt", "materials/models/fixture", "surface", [], "VertexLitGeneric { $basetexture models/fixture/surface }"u8.ToArray()),
            new("vtf", "materials/models/fixture", "surface", [], [1]));
        var component = WriteVpk("hide",
            new VpkFileSpec("vmt", "materials/fixture", "surface", [], "VertexLitGeneric { $no_draw 1 }"u8.ToArray()));
        var inputs = await Task.WhenAll(new[] { coach, material, bill, component }.Select(path => workspace.InspectAsync(path, default)));
        var warnings = new List<string>();

        var plan = IasModuleService.PlanDroppedVpks(inputs.SelectMany(input => input.Vpks).ToArray(), warnings);

        var group = Assert.Single(plan.Groups);
        Assert.Equal(bill, group.ModelVpkPath);
        Assert.Equal(material, Assert.Single(group.MaterialVpkPaths));
        Assert.Contains(warnings, warning => warning.Contains("coach.vpk") && warning.Contains("同一个模型"));
        Assert.Contains(warnings, warning => warning.Contains("hide.vpk") && warning.Contains("$no_draw"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedFirstModelDoesNotStopTheNextGroup(bool secondSucceeds)
    {
        var first = WriteModel("first", 3);
        var second = WriteModel("second", 4);
        var warnings = new List<string>();
        var plan = IasModuleService.PlanDroppedVpks([second, first], warnings);
        var groups = plan.Groups.Select(group => new IasModuleService.DropConversion(group.ModelVpkPath,
            IasDropItemKind.L4d2Vpk, group.DisplayName, [], group.MaterialVpkPaths)).ToArray();
        var visited = new List<string>();

        var result = await IasModuleService.AddDropConversionsAsync(groups, 0, warnings, plan.NoModelsMessage,
            (group, _) =>
            {
                visited.Add(group.Path);
                return group.Path == second && secondSucceeds
                    ? Task.FromResult<IReadOnlyList<string>>(["第二组已写入"])
                    : Task.FromException<IReadOnlyList<string>>(new InvalidDataException("合成失败"));
            }, default);

        Assert.Equal(new[] { first, second }, visited);
        Assert.Equal(secondSucceeds, result.Success);
        Assert.Contains(result.Warnings, warning => warning.Contains("first.vpk") && warning.Contains("合成失败"));
        if (secondSucceeds)
            Assert.Contains("第二组已写入", result.Warnings);
        else
        {
            Assert.Contains("first.vpk", result.Message);
            Assert.Contains(result.Warnings, warning => warning.Contains("second.vpk") && warning.Contains("合成失败"));
        }
    }

    private string WriteModel(string name, int vertexCount)
    {
        BoneSpec[] bones = [
            new("ValveBiped.Bip01_Pelvis", -1), new("ValveBiped.Bip01_Spine", 0), new("ValveBiped.Bip01_Head1", 1),
            new("ValveBiped.Bip01_L_Thigh", 0), new("ValveBiped.Bip01_L_UpperArm", 1)
        ];
        var vertex = new VertexSpec([1, 0, 0], [0, 0, 0], new(0, 0, 0), new(0, 1, 0), 0, 0);
        var vertices = Enumerable.Repeat(vertex, vertexCount).ToArray();
        var mdl = BuildMdl(name, 49, bones,
            [new BodyPartSpec("body", [new ModelSpec(name, vertexCount, 0, [new MeshSpec(0, vertexCount, 0)])])],
            ["models/fixture/surface"]);
        var vvd = BuildVvd(vertices);
        var vtx = BuildVtx(7, 25, [BodyPart(SingleMesh(Group([0, 1, 2], [0, 1, 2])))]);
        return WriteVpk(name, new("mdl", "models/survivors", name, [], mdl), new("vvd", "models/survivors", name, [], vvd),
            new("vtx", "models/survivors", name + ".dx90", [], vtx));
    }

    private string WriteVpk(string name, params VpkFileSpec[] files)
    {
        var path = Path.Combine(scratch.FullName, name + ".vpk");
        File.WriteAllBytes(path, BuildVpk(1, 18, files));
        return path;
    }

    public void Dispose() => scratch.Delete(recursive: true);
}
