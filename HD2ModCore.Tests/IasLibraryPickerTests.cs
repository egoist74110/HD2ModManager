using System.IO;
using HD2ModManager.Services.Ias;
using HD2ModManager.ViewModels;

namespace HD2ModCore.Tests;

/// <summary>「从库里选 Mod 转 IAS」：候选识别（沙盒目录）与选择器逻辑（筛选、搜索、分组、多选）。</summary>
public sealed class IasLibraryPickerTests : IDisposable
{
    private readonly string _root;

    public IasLibraryPickerTests()
    {
        var repo = AppContext.BaseDirectory;
        while (repo is not null && !File.Exists(Path.Combine(repo, "WpfApp1.sln"))) repo = Path.GetDirectoryName(repo);
        _root = Path.Combine(repo ?? Path.GetTempPath(), "tmp", "w30", "tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Mod(string name, params (string Path, string Content)[] files)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(dir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        return dir;
    }

    private const string CustomModelJson = """{"key":"custom-abc","sourceItemId":null,"pieces":[],"name":"素体甲"}""";

    private const string PatchesJson = """
        {"schema":"hd2mod.patch-groups/1","generatedUtc":"2026-11-21T14:44:35+00:00","patches":[
         {"patchIndex":0,"archiveHex16":"9ba626afa44a3aa3","role":"unit","sourceName":"a.vpk"},
         {"patchIndex":1,"archiveHex16":"9ba626afa44a3aa3","role":"material","sourceName":"a.vpk","optionName":"模型内置材质"},
         {"patchIndex":2,"archiveHex16":"9ba626afa44a3aa3","role":"material","sourceName":"b.vpk","optionName":"油光"},
         {"patchIndex":3,"archiveHex16":"9ba626afa44a3aa3","role":"material","sourceName":"c.vpk","optionName":"蕾丝"}]}
        """;

    [Fact]
    public async Task VpkImportedModIsRecognisedFromSourceVpkAndMaterialOptions()
    {
        var dir = Mod("vpk-mod", ("model.vpk", "x"), ("vpk2patch.json", "{}"),
            ("sectional/custom-model.json", CustomModelJson), ("sectional/patches.json", PatchesJson),
            ("wardrobe.json", """{"targetItemId":4242}"""));

        var facts = await IasLibraryCandidateReader.InspectAsync(dir, CancellationToken.None);

        Assert.Equal(IasLibrarySource.L4d2Vpk, facts.Source);
        Assert.Equal([Path.Combine(dir, "model.vpk")], facts.SourceVpks);
        Assert.True(facts.HasConversionReport);
        // 「模型内置材质」不算材质包。
        Assert.Equal(2, facts.MaterialPackCount);
        Assert.Equal(4242u, facts.WardrobeTargetItemId);
        Assert.Null(IasLibraryCandidateReader.UnavailableReason(facts));
    }

    [Fact]
    public async Task CustomModelWithoutSourceVpkIsRecognisedButNotConvertible()
    {
        var dir = Mod("body-mod", ("Overwrite/custom-model.json", CustomModelJson), ("sectional/patches.json", PatchesJson));

        var facts = await IasLibraryCandidateReader.InspectAsync(dir, CancellationToken.None);

        Assert.Equal(IasLibrarySource.CustomModel, facts.Source);
        Assert.Empty(facts.SourceVpks);
        Assert.False(facts.HasConversionReport);
        Assert.False(facts.HasRootPatches);
        Assert.Equal(IasLibraryCandidateReader.CustomModelReason, IasLibraryCandidateReader.UnavailableReason(facts));
    }

    [Fact]
    public async Task CustomModelThatKeepsItsOriginalRootPatchesIsConvertible()
    {
        var dir = Mod("replaced-mod", ("0123456789abcdef.patch_0", "x"), ("0123456789abcdef.patch_0.gpu_resources", "x"),
            ("Overwrite/custom-model.json", CustomModelJson), ("sectional/0123456789abcdef.patch_0", "y"));

        var facts = await IasLibraryCandidateReader.InspectAsync(dir, CancellationToken.None);

        Assert.Equal(IasLibrarySource.CustomModel, facts.Source);
        Assert.True(facts.HasRootPatches);
        Assert.Null(IasLibraryCandidateReader.UnavailableReason(facts));
    }

    [Fact]
    public async Task ConvertedModWhoseSourceVpkIsGoneExplainsWhy()
    {
        var dir = Mod("gone-vpk", ("vpk2patch.json", "{}"), ("sectional/custom-model.json", CustomModelJson));

        var facts = await IasLibraryCandidateReader.InspectAsync(dir, CancellationToken.None);

        Assert.Equal(IasLibrarySource.CustomModel, facts.Source);
        Assert.True(facts.HasConversionReport);
        Assert.Equal(IasLibraryCandidateReader.MissingVpkReason, IasLibraryCandidateReader.UnavailableReason(facts));
    }

    [Fact]
    public async Task OrdinaryPatchModStaysStandardAndConvertible()
    {
        var dir = Mod("armor-mod", ("0123456789abcdef.patch_0", "x"));

        var facts = await IasLibraryCandidateReader.InspectAsync(dir, CancellationToken.None);

        Assert.Equal(IasLibrarySource.Standard, facts.Source);
        Assert.Empty(facts.SourceVpks);
        Assert.Equal(0, facts.MaterialPackCount);
        Assert.Null(IasLibraryCandidateReader.UnavailableReason(facts));
    }

    [Fact]
    public async Task VolumeVpkFilesAreNotTreatedAsSources()
    {
        var dir = Mod("volume-only", ("pak01_000.vpk", "x"), ("0123456789abcdef.patch_0", "x"));

        var facts = await IasLibraryCandidateReader.InspectAsync(dir, CancellationToken.None);

        Assert.Equal(IasLibrarySource.Standard, facts.Source);
    }

    [Theory]
    [InlineData("2606520120", "2606520120")]
    [InlineData("尘白-胧嫣原皮换O-3甲_AR479939", "AR479939")]
    [InlineData("(抖动)某 Mod_AR173432", "AR173432")]
    [InlineData("10x4x Scope With Large FOV Fixed 0429_AR559003", "AR559003")]
    [InlineData("普通 Mod 名字", null)]
    [InlineData("名字_ARX1", null)]
    [InlineData("", null)]
    public void NumberComesFromTheLibraryFolderNotTheDisplayName(string folder, string? expected) =>
        Assert.Equal(expected, IasLibraryCandidateReader.NumberOf(folder));

    // ---- 选择器逻辑 ----

    private static IasLibraryCandidate[] Sample() =>
    [
        new("std-current", "Milfy SR-64", null, "普通 Mod") { ArmorName = "SR-64", OptionCount = 3, InActiveProfile = true },
        new("vpk-current", "可露凯婚纱", null, "求生 VPK")
            { Source = IasLibrarySource.L4d2Vpk, MaterialPackCount = 2, Number = "2606520120", InActiveProfile = true },
        new("std-other", "胧嫣 O-3", null, "普通 Mod") { ArmorName = "O-3", Number = "AR479939" },
        new("vpk-other", "泳装", null, "求生 VPK") { Source = IasLibrarySource.L4d2Vpk },
        new("body", "素体甲", null, "素体")
            { Source = IasLibrarySource.CustomModel, UnavailableReason = IasLibraryCandidateReader.CustomModelReason },
        new("role", "Eku", null, "普通 Mod") { InActiveProfile = true, UnavailableReason = IasLibraryCandidateReader.AlreadyRoleReason },
    ];

    private static IasLibraryPickerViewModel Loaded()
    {
        var model = new IasLibraryPickerViewModel();
        model.SetCandidates(Sample());
        return model;
    }

    private static string[] Names(IEnumerable<IasLibraryPickerItem> items) => items.Select(item => item.Name).ToArray();

    [Fact]
    public void StartsLoadingThenGroupsCurrentConfigurationBeforeTheRestOfTheLibrary()
    {
        var model = new IasLibraryPickerViewModel();
        Assert.True(model.IsLoading);
        Assert.False(model.ShowList);
        Assert.False(model.IsEmpty);

        model.SetCandidates(Sample());

        Assert.False(model.IsLoading);
        Assert.True(model.ShowList);
        Assert.Equal(["Milfy SR-64", "可露凯婚纱", "Eku"], Names(model.CurrentItems));
        Assert.Equal(["胧嫣 O-3", "泳装", "素体甲"], Names(model.OtherItems));
        Assert.Equal("3", model.CurrentCountText);
        Assert.Equal("3", model.OtherCountText);
    }

    [Theory]
    [InlineData(IasLibraryFilter.All, 6)]
    [InlineData(IasLibraryFilter.Current, 3)]
    [InlineData(IasLibraryFilter.Standard, 3)]
    [InlineData(IasLibraryFilter.Vpk, 2)]
    [InlineData(IasLibraryFilter.Body, 1)]
    public void SegmentFilterNarrowsBothGroups(IasLibraryFilter filter, int expected)
    {
        var model = Loaded();

        model.SetFilterCommand.Execute(filter);

        Assert.Equal(expected, model.CurrentItems.Count + model.OtherItems.Count);
    }

    [Fact]
    public void EmptyGroupsAreHiddenAndCurrentFilterLeavesOnlyTheCurrentGroup()
    {
        var model = Loaded();
        model.Filter = IasLibraryFilter.Current;
        Assert.True(model.HasCurrent);
        Assert.False(model.HasOther);

        model.Filter = IasLibraryFilter.Body;
        Assert.False(model.HasCurrent);
        Assert.True(model.HasOther);
    }

    [Theory]
    [InlineData("milfy", new[] { "Milfy SR-64" })]
    [InlineData("MILFY", new[] { "Milfy SR-64" })]
    [InlineData("2606520120", new[] { "可露凯婚纱" })]
    [InlineData("ar4799", new[] { "胧嫣 O-3" })]
    public void SearchMatchesNameAndNumber(string query, string[] expected)
    {
        var model = Loaded();

        model.SearchText = query;

        Assert.Equal(expected, Names(model.CurrentItems.Concat(model.OtherItems)));
    }

    [Fact]
    public void SearchDoesNotMatchSourceOrMetaText()
    {
        var model = Loaded();

        model.SearchText = "求生";
        Assert.True(model.IsEmpty);
        model.SearchText = "SR-64 ·";
        Assert.True(model.IsEmpty);
        model.SearchText = "";
        Assert.False(model.IsEmpty);
    }

    [Fact]
    public void NoMatchShowsEmptyStateAndFilterAndSearchCombine()
    {
        var model = Loaded();
        model.SearchText = "不存在的名字";
        Assert.True(model.IsEmpty);
        Assert.False(model.ShowList);

        model.SearchText = "婚纱";
        model.Filter = IasLibraryFilter.Vpk;
        Assert.Equal(["可露凯婚纱"], Names(model.CurrentItems.Concat(model.OtherItems)));
        model.Filter = IasLibraryFilter.Standard;
        Assert.True(model.IsEmpty);
    }

    [Fact]
    public void ClickingCardsTogglesMultiSelectAndUpdatesFooterAndButtonText()
    {
        var model = Loaded();
        Assert.Equal("点卡片选择，可以多选", model.SummaryText);
        Assert.Equal("转为 IAS 并安装", model.ConfirmText);
        Assert.False(model.CanConfirm);

        model.ToggleCommand.Execute(model.CurrentItems[0]);
        Assert.Equal("已选 1 个", model.SummaryText);
        Assert.Equal("转为 IAS 并安装", model.ConfirmText);
        Assert.True(model.CanConfirm);

        model.ToggleCommand.Execute(model.OtherItems[0]);
        model.ToggleCommand.Execute(model.OtherItems[1]);
        Assert.Equal("已选 3 个", model.SummaryText);
        Assert.Equal("转为 IAS 并安装 (3)", model.ConfirmText);
        Assert.Equal(["std-current", "std-other", "vpk-other"], model.SelectedCandidates.Select(item => item.ModId));

        model.ToggleCommand.Execute(model.CurrentItems[0]);
        Assert.Equal("已选 2 个", model.SummaryText);
        Assert.Equal(["std-other", "vpk-other"], model.SelectedCandidates.Select(item => item.ModId));
    }

    [Fact]
    public void UnavailableCardsCannotBeSelected()
    {
        var model = Loaded();
        var role = model.CurrentItems.Single(item => item.Name == "Eku");
        var body = model.OtherItems.Single(item => item.Name == "素体甲");

        model.ToggleCommand.Execute(role);
        model.ToggleCommand.Execute(body);

        Assert.False(role.IsSelected);
        Assert.False(body.IsSelected);
        Assert.Equal(0, model.SelectedCount);
        Assert.False(model.CanConfirm);
        Assert.Equal("已经是 IAS 角色", role.UnavailableReason);
    }

    [Fact]
    public void SelectionSurvivesFilterAndSearchChangesAndAvatarsAreCappedAtFour()
    {
        var model = new IasLibraryPickerViewModel();
        model.SetCandidates(Enumerable.Range(1, 6).Select(i => new IasLibraryCandidate($"m{i}", $"Mod {i}", null, "普通 Mod")).ToArray());
        foreach (var item in model.OtherItems.ToArray()) model.Toggle(item);

        Assert.Equal(6, model.SelectedCount);
        Assert.Equal(4, model.Avatars.Count);
        Assert.Equal(["Mod 1", "Mod 2", "Mod 3", "Mod 4"], Names(model.Avatars));

        model.SearchText = "Mod 5";
        model.Filter = IasLibraryFilter.Vpk;
        Assert.Equal(6, model.SelectedCount);
        Assert.Equal(6, model.SelectedCandidates.Count);
        Assert.Equal("转为 IAS 并安装 (6)", model.ConfirmText);
    }

    [Fact]
    public void LoadFailureIsShownInsteadOfTheList()
    {
        var model = new IasLibraryPickerViewModel();

        model.SetLoadFailed("读取 Mod 库失败：测试");

        Assert.True(model.HasLoadError);
        Assert.False(model.IsLoading);
        Assert.False(model.ShowList);
        Assert.False(model.IsEmpty);
        Assert.False(model.CanConfirm);
    }

    [Fact]
    public void CardTextShowsArmorPacksAndOptionsOnlyWhenKnown()
    {
        Assert.Equal("套在 DP-8 · 2 个材质包", new IasLibraryCandidate("a", "A", null, "")
            { ArmorName = "DP-8", MaterialPackCount = 2 }.MetaText);
        Assert.Equal("套在 O-3 · 3 个选项", new IasLibraryCandidate("a", "A", null, "")
            { ArmorName = "O-3", OptionCount = 3, MaterialPackCount = 0 }.MetaText);
        var unknown = new IasLibraryCandidate("a", "A", null, "");
        Assert.Equal("", unknown.MetaText);
        Assert.False(unknown.HasMeta);
        Assert.Equal("普通 Mod", unknown.SourceText);
        Assert.Equal("求生 VPK", (unknown with { Source = IasLibrarySource.L4d2Vpk }).SourceText);
        Assert.Equal("素体", (unknown with { Source = IasLibrarySource.CustomModel }).SourceText);
    }
}
