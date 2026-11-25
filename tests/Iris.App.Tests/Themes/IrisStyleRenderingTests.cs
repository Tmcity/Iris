using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Iris.App.Tests.TestSupport;
using Shouldly;
using Wpf.Ui.Controls;
using Xunit;

namespace Iris.App.Tests.Themes;

[Collection("IrisThemeResources")]
public sealed class IrisStyleRenderingTests
{
    // 与 HistoryRenderingTests.cs:21、LocalizationRenderingTests.cs:34 同款写法：缓存成静态字段。
    // 写成内联的 new JsonSerializerOptions { WriteIndented = true } 会踩 CA1869，而本仓库
    // TreatWarningsAsErrors=true + AnalysisLevel=latest-recommended，CA1869 不在测试项目的 NoWarn 里。
    private static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task StylePanel_IsRenderedForReview(bool dark) => WpfTestRunner.RunAsync(() =>
    {
        // 空串不是 null：写成 ?? 时把 IRIS_T041_EVIDENCE 设成空串不会兜底，Path.Combine("", …) 会把
        // 证据写进进程当前目录（也就是 bin 输出目录），所以先用 IsNullOrEmpty 判空再退回临时目录。
        var evidenceRoot = Environment.GetEnvironmentVariable("IRIS_T041_EVIDENCE");
        var directory = Path.Combine(
            string.IsNullOrEmpty(evidenceRoot) ? Path.GetTempPath() : evidenceRoot,
            $"iris-styles-{(dark ? "dark" : "light")}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        var theme = dark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light;

        var panel = new StackPanel { Margin = new Thickness(16), Width = 420 };
        // TextBlock 必须写全限定名：本文件同时 using 了 Wpf.Ui.Controls 与 System.Windows.Controls，
        // 两个命名空间都有 TextBlock，裸写会 CS0104 编译失败（与 IrisIconButtonStyleTests 同一处理）。
        // 而且这里要的正是基类那个：IrisSectionHeader 等样式的 TargetType 就是它。
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Section header", Style = (Style)Application.Current.FindResource("IrisSectionHeader") });
        panel.Children.Add(new Border
        {
            Style = (Style)Application.Current.FindResource("IrisRowCard"),
            Child = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } },
                Children =
                {
                    new StackPanel
                    {
                        Children =
                        {
                            new System.Windows.Controls.TextBlock { Text = "Row title", Style = (Style)Application.Current.FindResource("IrisRowTitle") },
                            new System.Windows.Controls.TextBlock { Text = "Row description", Style = (Style)Application.Current.FindResource("IrisRowDescription") },
                        },
                    },
                    new CheckBox { Style = (Style)Application.Current.FindResource("IrisSwitchCheckBox"), IsChecked = true, HorizontalAlignment = HorizontalAlignment.Right },
                },
            },
        });
        panel.Children.Add(new Border
        {
            Style = (Style)Application.Current.FindResource("IrisTag"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new System.Windows.Controls.TextBlock { Text = "Tag", Style = (Style)Application.Current.FindResource("IrisTagText") },
        });
        panel.Children.Add(new Wpf.Ui.Controls.Button
        {
            Style = (Style)Application.Current.FindResource("IrisIconButton"),
            Content = "CopySourceFull",
            Icon = new SymbolIcon { Symbol = SymbolRegular.Copy24 },
            HorizontalAlignment = HorizontalAlignment.Left,
        });

        // 渲染的是这个 Border（窗口的客户区），不是窗口本身：窗口的 ActualWidth/ActualHeight 是含非客户区
        // 的 460x320，直接 Render(window) 会在位图右侧和底部留下一条 alpha=0 的边——实测那张位图是 460x320，
        // 内容只占 444x280（右侧 16px、底部 40px 透明）。浅色档看不出来，深色档在查看器里是一条白带，
        // 会被误读成样式缺陷。
        var surface = new Border { Child = panel };

        var window = new Window
        {
            Content = surface,
            Width = 460,
            Height = 300,
            ShowActivated = false,
            ShowInTaskbar = false,
            Left = -20000,
            Top = -20000,
        };
        // 两套字典都合并在窗口这一层，只影响这个窗口的子树：WPF-UI 的 ThemesDictionary 换掉
        // TextFillColor* / SubtleFillColor*（标题与说明文字、标签底、图标按钮），Iris 那本换掉
        // IrisAcrylic*（卡面填充与描边）。不动 Application.Current.Resources，所以不用"改完再还原"。
        //
        // 初版这里只把窗口底色涂黑、不换字典，dark 档渲染出来其实是"浅色主题画在黑底上"：标题、标签、
        // 图标按钮那支近黑画笔合成到纯黑上全部消失，卡面却因为 #66FFFFFF(40% 白) 合成成 #666666 还看得见。
        // 实测那三点 (30,20) / (30,118) / (30,144) 全是 0,0,0 —— 一张"既不像浅色也不像深色"的图，
        // 容易被当成深色主题的样式缺陷。
        window.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = theme });
        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                $"pack://application:,,,/Iris.App;component/Themes/Iris.{(dark ? "Dark" : "Light")}.xaml",
                UriKind.Absolute),
        });
        // 底色用主题自己的 ApplicationBackgroundBrush（实测浅色 250,250,250 / 深色 32,32,32），不写死白/黑：
        // 卡面填充是半透明的，压到应用真实的窗口底色上才看得出"比底色亮一点 + 一圈细边框"。
        // 这一句放在窗口与字典都就位之后：此时 surface 已挂进窗口的逻辑树，解析拿到的是窗口级那本。
        surface.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");

        // 钉住开关 ON 轨道的强调色，且只钉这一个键：UpdateRecoveryRenderingTests 的 finally
        // （tests/Iris.App.Tests/Updates/UpdateRecoveryRenderingTests.cs:115）调 1 参
        // ApplicationThemeManager.Apply(originalTheme)，该重载默认 updateAccent=true，会把 OS 强调色
        // （GetColorizationColor #FF0078D4）派生出的强调色笔刷写进进程级共享的 Application.Current.Resources
        // 并残留下来——app.Resources 的还原救不回来。于是同一份样式在不同进程组成下解析出的
        // ToggleSwitchFillOn 不同（仅本文件 #FF0067C0，宽过滤 Themes + UpdateRecoveryRenderingTests #FF006ABB），
        // 渲染出的哈希随之漂移（逐像素比对：只有那 40x20 开关区域会变，窗口底色不受影响）。
        // 这两个取值是本机生产路径的实测值：ThemeService 生产分支（SafeApplyWpfUiTheme）调的 Apply 同样让
        // updateAccent 取默认值 true，实测浅档 ON 轨道 #FF006ABB、深档 #FF4DB2FF——所以证据图与真机观感一致。
        // 直写 window.Resources（不是 MergedDictionaries 里那两本字典）：直写键的优先级高于合并字典，能压过
        // ThemesDictionary 带来的那个全局派生值。这是测试夹具"钉住不受测的输入"，不是样式改动——
        // ToggleSwitchFillOn 属 WPF-UI，不在 Iris 自有的四个键里。只钉这一个是因为实测只有它漂到画面上。
        var pinnedToggleFill = dark ? Color.FromArgb(0xFF, 0x4D, 0xB2, 0xFF) : Color.FromArgb(0xFF, 0x00, 0x6A, 0xBB);
        window.Resources["ToggleSwitchFillOn"] = new SolidColorBrush(pinnedToggleFill);

        try
        {
            window.Show();
            window.UpdateLayout();

            // 守卫：IrisCard/IrisRowCard 的 Background/BorderBrush 走 DynamicResource，键名拼错
            // 只会静默渲染成无底无框的透明块——图片哈希反映不出这个错误（哈希本来就会变），
            // 所以在这里显式断言。卡面必须真的拿到当前主题那本字典里的那两个键。
            // 底色也要一起断言：ApplicationBackgroundBrush 拼错时 SetResourceReference 只会留下 null，
            // 渲染出来整张图都是 alpha=0——哈希照旧、测试照绿，人复看的是一张空图。
            surface.Background.ShouldBeOfType<SolidColorBrush>();
            var rowCard = (Border)panel.Children[1];
            rowCard.Background.ShouldBeOfType<SolidColorBrush>();
            rowCard.BorderBrush.ShouldBeOfType<SolidColorBrush>();
            ((SolidColorBrush)rowCard.Background).Color.A.ShouldBeGreaterThan((byte)0);
            // 第三类控件：标签那个 Border 的底色也来自字典（IrisTag 的 Background 走 DynamicResource），
            // 一并守住，免得只有卡面被覆盖到。
            var tag = (Border)panel.Children[2];
            tag.Background.ShouldBeOfType<SolidColorBrush>();

            // dark 档必须真的换到深色。区分两档的只有 theme / ThemesDictionary{ Theme } /
            // Iris.{(dark ? "Dark" : "Light")}.xaml 这三处，任一处写死成 Light，dark 用例都照样全绿——
            // 这与初版"涂黑冒充深色"是同一族失败模式。窗口底色（ApplicationBackgroundBrush）实测深色
            // 32,32,32、浅色 250,250,250，断它那把笔刷的三通道之和，一次挡住"字典没换 / 主题写死 /
            // 窗口级 ThemesDictionary 被删"（删掉会退回应用级浅色）。读的是资源解析出的笔刷，不是位图像素。
            var background = (SolidColorBrush)surface.Background;
            var channels = background.Color.R + background.Color.G + background.Color.B;
            if (dark)
            {
                channels.ShouldBeLessThan(3 * 128);
            }
            else
            {
                channels.ShouldBeGreaterThan(3 * 128);
            }

            // 钉住的键必须真的压过合并字典：这条不是同义反复。它守的是"直写 window.Resources 的键
            // 优先级高于 MergedDictionaries"——哪天 WPF-UI 改了合并字典的优先级，这条会红，
            // 而不是让强调色漂移悄悄回来。
            window.FindResource("ToggleSwitchFillOn").ShouldBeOfType<SolidColorBrush>().Color.ShouldBe(pinnedToggleFill);

            var bitmap = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            // 尺寸守卫：ActualWidth/ActualHeight 为 0 时位图是 0x0，照样能编码出一个非空的 PNG 文件。
            bitmap.PixelWidth.ShouldBeGreaterThan(0);
            bitmap.PixelHeight.ShouldBeGreaterThan(0);
            // 内容守卫：空图/纯色图也能生成非空 PNG 让测试全绿，人复看的却是一张空图。取一次像素、
            // 按 16 像素的步长采样（不逐像素比对），断言至少出现两种互不相同的颜色。
            var stride = bitmap.PixelWidth * 4;
            var pixels = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, stride, 0);
            var distinct = new HashSet<uint>();
            for (var offset = 0; offset + 3 < pixels.Length; offset += 4 * 16)
            {
                distinct.Add(BitConverter.ToUInt32(pixels, offset));
            }

            distinct.Count.ShouldBeGreaterThan(1);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(directory, dark ? "styles-dark.png" : "styles-light.png");
            using (var output = File.Create(path))
            {
                encoder.Save(output);
            }

            // 同一个文件只读一次，哈希、字节数与下面的非空断言共用这一份字节。
            var bytes = File.ReadAllBytes(path);
            var manifest = new[] { new { File = Path.GetFileName(path), Bytes = bytes.Length, Width = bitmap.PixelWidth, Height = bitmap.PixelHeight, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) } };
            File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest, ManifestJson));
            bytes.Length.ShouldBeGreaterThan(0);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task ListRowItem_SelectedRowLooksDifferent_AndSelectionStillWorks() => WpfTestRunner.RunAsync(() =>
    {
        var style = (Style)Application.Current.FindResource("IrisListRowItem");
        // 三行内容相同，所以行与行之间的像素差只可能来自状态。
        var rows = Enumerable.Range(0, 3).Select(_ => new System.Windows.Controls.ListBoxItem { Content = "Row" }).ToArray();
        var list = new System.Windows.Controls.ListBox { ItemContainerStyle = style };
        foreach (var row in rows)
        {
            list.Items.Add(row);
        }

        var surface = new Border { Child = list, Padding = new Thickness(16) };
        var window = new Window
        {
            Content = surface,
            Width = 460,
            Height = 300,
            ShowActivated = false,
            ShowInTaskbar = false,
            Left = -20000,
            Top = -20000,
        };
        surface.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");
        try
        {
            window.Show();
            window.UpdateLayout();

            // 样式真的套到了容器上：否则下面比的是 WPF-UI 自带的行样式。
            rows[0].Style.ShouldBeSameAs(style);

            // 选中行为没被模板吞掉，两个方向都通。
            list.SelectedIndex = 0;
            rows[0].IsSelected.ShouldBeTrue();
            rows[1].IsSelected = true;
            list.SelectedIndex.ShouldBe(1);
            list.SelectedIndex = 0;
            rows[1].IsSelected.ShouldBeFalse();
            rows[2].IsEnabled = false;
            window.UpdateLayout();

            // 常态：卡面与描边来自 Iris 的两个键；间距来自主题层的两个键。
            var plainBorder = TemplatePart(rows[1], "RowBorder");
            plainBorder.Background.ShouldBeOfType<SolidColorBrush>().Color.ShouldBe(BrushColor("IrisAcrylicCardFillBrush"));
            plainBorder.BorderBrush.ShouldBeOfType<SolidColorBrush>().Color.ShouldBe(BrushColor("IrisAcrylicCardStrokeBrush"));
            rows[1].Padding.ShouldBe((Thickness)Application.Current.FindResource("IrisRowPadding"));
            rows[1].Margin.ShouldBe((Thickness)Application.Current.FindResource("IrisRowGap"));

            // 选中：状态层有了底色，描边换了一支。未选中：状态层透明。
            TemplatePart(rows[1], "StateLayer").Background.ShouldBeOfType<SolidColorBrush>().Color.A.ShouldBe((byte)0);
            TemplatePart(rows[0], "StateLayer").Background.ShouldBeOfType<SolidColorBrush>().Color.A.ShouldBeGreaterThan((byte)0);
            TemplatePart(rows[0], "RowBorder").BorderBrush.ShouldBeOfType<SolidColorBrush>().Color
                .ShouldBe(BrushColor("TextFillColorSecondaryBrush"));

            // 禁用：整行降不透明度。先断常态是 1，否则 0.5 那条可能只是模板写死的。
            plainBorder.Opacity.ShouldBe(1d);
            TemplatePart(rows[2], "RowBorder").Opacity.ShouldBe(0.5d);

            // 键盘焦点框：FindResource 在键不存在时会抛异常。
            rows[1].FocusVisualStyle.ShouldBeSameAs(Application.Current.FindResource("DefaultControlFocusVisualStyle"));

            // 最后看像素：选中行与未选中行在行内同一位置（左内边距里，没有文字）颜色不同。
            var bitmap = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            bitmap.PixelWidth.ShouldBeGreaterThan(0);
            rows[0].ActualHeight.ShouldBeGreaterThan(20d);
            RowPixel(bitmap, rows[0], surface).ShouldNotBe(RowPixel(bitmap, rows[1], surface));
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    private static Border TemplatePart(System.Windows.Controls.ListBoxItem row, string name) =>
        row.Template.FindName(name, row).ShouldBeOfType<Border>();

    private static Color BrushColor(string key) =>
        Application.Current.FindResource(key).ShouldBeOfType<SolidColorBrush>().Color;

    // 取行内 (6, 高度一半) 处的像素：x=6 落在 16 的左内边距里，避开文字与 1px 描边。
    // 渲染的是 surface（窗口客户区，原点 0,0），所以把行内坐标换算到 surface 上再读。
    private static uint RowPixel(RenderTargetBitmap bitmap, FrameworkElement row, Visual surface)
    {
        var point = row.TransformToAncestor(surface).Transform(new Point(6, row.ActualHeight / 2));
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect((int)point.X, (int)point.Y, 1, 1), pixel, 4, 0);
        return BitConverter.ToUInt32(pixel, 0);
    }
}
