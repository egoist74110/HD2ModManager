using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HD2ModManager.Services.Ias;
using HD2ModManager.Views;

namespace HD2ModCore.Tests;

/// <summary>
/// 只渲染「从库里选 Mod 转 IAS」对话框（假数据）并输出 PNG 到 tmp/w30/screens/，不启动管理器。
/// 临时建一个没有 StartupUri 的 Application 来装主题资源，渲染完立即关掉，免得影响别的测试。
/// </summary>
[Collection("WpfApplication")]
public sealed class IasLibraryPickerRenderTests
{
    private static string Repo()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "WpfApp1.sln"))) dir = Path.GetDirectoryName(dir);
        return dir ?? Path.GetTempPath();
    }

    [Fact]
    public void RendersPickerStatesToPng()
    {
        var output = Path.Combine(Repo(), "tmp", "w30", "screens");
        Directory.CreateDirectory(output);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Render(output); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "渲染超时");
        if (failure is not null) throw new InvalidOperationException(failure.ToString());
        Assert.Null(System.Windows.Application.Current);
        foreach (var name in new[] { "picker-default.png", "picker-filtered-vpk.png", "picker-empty.png", "picker-loading.png" })
            Assert.True(new FileInfo(Path.Combine(output, name)).Length > 5000, name);
    }

    private static void Render(string output)
    {
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var file in new[] { "Controls.xaml", "DataTables.xaml", "CardTemplates.xaml" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/HD2ModManager;component/Themes/{file}")
            });
        var covers = MakeCovers(output);
        var candidates = Candidates(covers);

        var dialog = new IasLibraryPickerDialog(candidates);
        var model = dialog.Model;
        model.Toggle(model.CurrentItems[0]);
        model.Toggle(model.CurrentItems[1]);
        model.Toggle(model.OtherItems[0]);
        Save(dialog, Path.Combine(output, "picker-default.png"));

        model.Filter = HD2ModManager.ViewModels.IasLibraryFilter.Vpk;
        Save(dialog, Path.Combine(output, "picker-filtered-vpk.png"));

        model.Filter = HD2ModManager.ViewModels.IasLibraryFilter.All;
        model.SearchText = "没有这个名字";
        Save(dialog, Path.Combine(output, "picker-empty.png"));

        var loading = new IasLibraryPickerDialog(null, CancellationToken.None);
        Save(loading, Path.Combine(output, "picker-loading.png"));

        try
        {
            // WPF 每个 AppDomain 只允许一个 Application；两页共用同一次离屏宿主。
            IasPageViewModelTests.RenderRoleStates(app, Path.Combine(Repo(), "tmp", "w52", "screens"));
        }
        finally
        {
            app.Shutdown();
            Dispatcher.CurrentDispatcher.InvokeShutdown();
            // 没有 Run 过的 Application 不会自己清掉静态引用。
            typeof(System.Windows.Application).GetField("_appInstance",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.SetValue(null, null);
        }
    }

    private static void Save(Window window, string path)
    {
        var root = (FrameworkElement)window.Content;
        var size = new Size(window.Width, window.Height);
        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();
        // 再来一轮：容器第一次生成后尺寸才稳定。
        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        var white = new DrawingVisual();
        using (var context = white.RenderOpen()) context.DrawRectangle(Brushes.White, null, new Rect(size));
        bitmap.Render(white);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static string[] MakeCovers(string output)
    {
        var colors = new[] { "#C9D8F5", "#F5D0D7", "#CFEBD9", "#F3E2B6", "#D9CCF0", "#BFE3EA" };
        var paths = new List<string>();
        var dir = Path.Combine(output, "covers");
        Directory.CreateDirectory(dir);
        for (var i = 0; i < colors.Length; i++)
        {
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                var color = (Color)ColorConverter.ConvertFromString(colors[i]);
                context.DrawRectangle(new LinearGradientBrush(color, Colors.White, 60), null, new Rect(0, 0, 112, 144));
                context.DrawEllipse(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), null, new Point(56, 52), 22, 22);
                context.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), null, new Rect(24, 78, 64, 52), 14, 14);
            }
            var bitmap = new RenderTargetBitmap(112, 144, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var path = Path.Combine(dir, $"cover{i}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
            paths.Add(path);
        }
        return [.. paths];
    }

    private static IasLibraryCandidate[] Candidates(string[] covers) =>
    [
        new("a", "Milfy SR-64", covers[0], "普通 Mod") { ArmorName = "SR-64", OptionCount = 3, InActiveProfile = true },
        new("b", "少前2可露凯婚纱（抖动版，原版礼服配色与很长的名字用来测试两行省略）", covers[1], "求生 VPK")
            { Source = IasLibrarySource.L4d2Vpk, MaterialPackCount = 2, Number = "2606520120", InActiveProfile = true },
        new("c", "Eku", covers[2], "普通 Mod")
            { ArmorName = "DP-8", InActiveProfile = true, UnavailableReason = IasLibraryCandidateReader.AlreadyRoleReason },
        new("d", "尘白-胧嫣原皮换 O-3 甲", covers[3], "普通 Mod") { ArmorName = "O-3", OptionCount = 3, Number = "AR479939" },
        new("e", "泳装", covers[4], "求生 VPK") { Source = IasLibrarySource.L4d2Vpk, MaterialPackCount = 1 },
        new("f", "自定义素体", covers[5], "素体")
            { Source = IasLibrarySource.CustomModel, UnavailableReason = IasLibraryCandidateReader.CustomModelReason },
        new("g", "布莱默顿", null, "普通 Mod") { ArmorName = "DP-8" },
    ];
}
