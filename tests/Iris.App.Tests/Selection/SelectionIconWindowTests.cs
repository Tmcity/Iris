using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Iris.App.Selection;
using Iris.App.Services;
using Iris.App.Tests.TestSupport;
using Iris.App.Translation;
using Iris.Core.Settings;
using Iris.Core.Platform;
using NSubstitute;
using Shouldly;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;

namespace Iris.App.Tests.Selection;

[Collection("AppShutdownerSharedState")]
public sealed class SelectionIconWindowTests
{
    [Fact]
    public async Task SelectionIconWindow_HasTransparentHitTestSurfaceAndThemedRoundedCard()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
                var native = new SelectionIconNative(
                    Substitute.For<ISelectionIconSystem>(),
                    Substitute.For<INativeWindowPlacementAdapter>());
            var window = new SelectionIconWindow(native)
            {
                Left = -20000,
                Top = -20000,
                ShowActivated = false,
            };

            try
            {
                window.Width.ShouldBe(24);
                window.Height.ShouldBe(24);
                window.AllowsTransparency.ShouldBeTrue();
                window.WindowStyle.ShouldBe(WindowStyle.None);
                window.ShowActivated.ShouldBeFalse();
                window.ShowInTaskbar.ShouldBeFalse();
                window.Topmost.ShouldBeTrue();
                window.Title.ShouldBe(UiText.MouseIconTranslate);

                var bg = window.Background.ShouldBeOfType<SolidColorBrush>();
                bg.Color.A.ShouldBe((byte)1);

                var button = window.TranslateButton.ShouldBeOfType<Button>();
                button.ToolTip.ShouldBe(UiText.MouseIconTranslate);
                AutomationProperties.GetName(button).ShouldBe(UiText.MouseIconTranslate);

                var icon = button.Content.ShouldBeOfType<SymbolIcon>();
                icon.Symbol.ShouldBe(SymbolRegular.Translate16);
                button.Content.ShouldNotBeOfType<string>();

                window.Show();
                await Task.Yield();

                foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
                {
                    var appTheme = theme == AppTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
                    ApplicationThemeManager.Apply(appTheme, Wpf.Ui.Controls.WindowBackdropType.None);
                    ThemeService.ApplyIrisThemeDictionary(appTheme);
                    window.UpdateLayout();
                    button.ApplyTemplate();
                if (PresentationSource.FromVisual(window) is System.Windows.Interop.HwndSource source)
                    source.CompositionTarget.BackgroundColor.A.ShouldBe((byte)0);

                    icon.FontSize.ShouldBe(15);
                    ((SolidColorBrush)window.Background).Color.A.ShouldBe((byte)1);
                    var cardBorder = (button.Template.FindName("CardBorder", button) as Border)
                        ?? FindVisualChild<Border>(button);
                    cardBorder.ShouldNotBeNull();
                    cardBorder.CornerRadius.ShouldBe(new CornerRadius(6));
                    cardBorder.Effect.ShouldBeNull();

                    var cardBg = cardBorder.Background.ShouldBeOfType<SolidColorBrush>();
                    cardBg.Color.A.ShouldBe((byte)255);
                }

                int clickedCount = 0;
                window.Clicked += (_, _) => clickedCount++;
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                clickedCount.ShouldBe(1);
            }
            finally
            {
                ApplicationThemeManager.Apply(ApplicationTheme.Light);
                    ThemeService.ApplyIrisThemeDictionary(ApplicationTheme.Light);
                window.Close();
            }
        });
    }

    private static T? FindVisualChild<T>(DependencyObject parent, string? name = null) where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed && (name is null || typed.Name == name))
                return typed;
            var nested = FindVisualChild<T>(child, name);
            if (nested is not null)
                return nested;
        }
        return null;
    }
}
