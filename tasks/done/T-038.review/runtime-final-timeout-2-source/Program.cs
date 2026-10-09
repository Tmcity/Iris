using System.Globalization;
using System.Text.Json;
using System.Windows.Controls;
using Iris.App;
using Iris.App.Resources;
using Iris.Core.Errors;
using Iris.Core.Localization;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Core.Serialization;
using Iris.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

await using var sta = new ReviewSta();
static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
string? savedFile = null;
Paths? savedPaths = null;
var failures = new List<Exception>();
int scenarios = 0;

await Run("fixed selected language across differing thread cultures", async () =>
{
    using var language = AppLanguage.BeginRun();
    AppLanguage.Select("en-US");
    var numeric = CultureInfo.CurrentCulture.Name;
    await Task.Run(() =>
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            Check(UiText.SettingsTitle == "Settings", "App resources followed a worker's ambient culture");
            Check(ErrorMessages.GetDefault(ErrorCode.InvalidArgument) == "Invalid argument", "Core error language differed from App");
            AppLanguage.Select("zh-CN");
            Check(AppLanguage.Name == "en-US", "A later selection changed the current run");
            Check(CultureInfo.CurrentCulture.Name == numeric, "UI selection changed numeric culture");
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    });
});

await WindowCase("real language control joins one history save without live language change", async f =>
{
    savedFile = f.Paths.SettingsFile; savedPaths = f.Paths;
    f.Show(); f.Vm.CurrentPageKey = "general"; f.Window.UpdateLayout();
    var control = (ComboBox)f.Window.FindName("UiLanguageCombo");
    Check(control.SelectedValue as string == "en-US", "Language control does not reflect saved configuration");
    control.SelectedValue = "zh-CN";
    Check(f.Vm.InterfaceLanguage.Value == "zh-CN", "Control did not update the actual language draft");
    f.Vm.History.MaxEntriesText.Value = "345";
    await f.Vm.ExecuteSaveAsync();
    Check(f.Updates == 1 && f.Settings.Current.General.UiLanguage == "zh-CN" && f.Settings.Current.History.MaxEntries == 345, "Joint language/history save was not a single commit");
    Check(!f.Vm.IsDirty && f.Secrets.Writes == 0, "Language save retained a dirty draft or wrote secrets");
    Check(AppLanguage.Name == "en-US" && f.Window.Title == "Iris Settings", "Save changed the running UI language");
    Check(((TextBlock)f.Window.FindName("UiLanguageHint")).Text.Contains("next launch", StringComparison.Ordinal), "Saved language change lacks restart feedback");
    using var json = JsonDocument.Parse(await File.ReadAllTextAsync(savedFile));
    Check(json.RootElement.GetProperty("general").GetProperty("uiLanguage").GetString() == "zh-CN", "Persisted JSON language is wrong");
});

await Run("fresh real JSON load drives the next language selection", async () =>
{
    Check(savedPaths != null && savedFile != null && File.Exists(savedFile), "No persisted file from joint-save probe");
    using var language = AppLanguage.BeginRun();
    var services = new ServiceCollection();
    services.AddLogging(); services.AddSingleton<IAppPaths>(savedPaths!); services.AddSingleton<ISecretStore>(new Secrets());
    services.AddIrisInfrastructure();
    await using var root = services.BuildServiceProvider();
    var settings = root.GetRequiredService<ISettingsService>();
    await settings.LoadAsync();
    AppLanguage.Select(settings.Current.General.UiLanguage);
    Check(AppLanguage.Name == "zh-CN" && UiText.SettingsTitle == "设置", "Next run ignored persisted language");
    Check(ErrorMessages.GetDefault(ErrorCode.InvalidArgument) == "参数无效", "Core did not use the next run's language");
});

await WindowCase("joint conflict preserves language draft and saved language", async f =>
{
    f.Show(); f.Vm.CurrentPageKey = "general";
    ((ComboBox)f.Window.FindName("UiLanguageCombo")).SelectedValue = "zh-CN";
    f.Vm.LogLevel = "Warning";
    await f.Settings.UpdateAsync(s => s with { General = s.General with { LogLevel = "Debug" } });
    await f.Vm.ExecuteSaveAsync();
    Check(f.Updates == 1 && f.Settings.Current.General.UiLanguage == "en-US" && f.Settings.Current.General.LogLevel == "Debug", "Conflicting save overwrote current settings");
    Check(f.Vm.InterfaceLanguage.Value == "zh-CN" && f.Vm.LogLevel == "Warning" && f.Vm.IsDirty, "Conflict lost the user's draft");
    Check(f.Vm.IsConflict && !string.IsNullOrWhiteSpace(f.Vm.StatusMessage)
        && f.Vm.StatusMessage.All(ch => ch < '\u4E00' || ch > '\u9FFF'), "Conflict did not produce localized feedback");
    f.Vm.ExecuteReload();
    Check(f.Vm.InterfaceLanguage.Value == "en-US" && !f.Vm.InterfaceLanguage.IsDirty, "Reload did not reset language draft");
});

await WindowCase("language-only save merges unrelated external changes", async f =>
{
    f.Show(); f.Vm.CurrentPageKey = "general";
    ((ComboBox)f.Window.FindName("UiLanguageCombo")).SelectedValue = "zh-CN";
    await f.Settings.UpdateAsync(s => s with { General = s.General with { LogLevel = "Debug" } });
    await f.Vm.ExecuteSaveAsync();
    Check(f.Updates == 2 && f.Settings.Current.General.UiLanguage == "zh-CN" && f.Settings.Current.General.LogLevel == "Debug", "Language save did not merge the unrelated external field");
    Check(AppLanguage.Name == "en-US" && !f.Vm.IsDirty, "Merged save changed active language or retained dirty state");
});

await WindowCase("actual Core rejects an unsupported target without writing", async f =>
{
    var result = await f.Root.GetRequiredService<ISettingsEditCoordinator>().CommitAsync(new SettingsEditPlan(
        new GeneralSettingsPatch { UiLanguage = new("en-US", "fr-FR") }, []));
    Check(result.Status == SettingsEditStatus.Uncommitted && result.Error?.Code == ErrorCode.InvalidArgument, "Unsupported language target was not rejected");
    Check(f.Updates == 0 && f.Settings.Current.General.UiLanguage == "en-US", "Rejected language target changed persistence");
    Check(result.Error!.Message.Contains("zh-CN", StringComparison.Ordinal), "Language validation lost the allowed-value guidance");
});

if (!args.Contains("--skip-menu", StringComparer.Ordinal))
await WindowCase("actual preset menu and visible add button retain stable saved data", async f =>
{
    f.Show(); f.Vm.CurrentPageKey = "translation"; f.Window.Width = 760; f.Window.Height = 520; f.Window.UpdateLayout();
    string before = JsonSerializer.Serialize(f.Settings.Current, IrisJson.Options);
    string presets = JsonSerializer.Serialize(TranslationProviderPresets.All, IrisJson.Options);
    var button = (Button)f.Window.FindName("AddProfileButton");
    Check(button.IsVisible && button.ActualWidth > 40, "Actual add provider button is not visible");
    var position = button.TranslatePoint(new System.Windows.Point(button.ActualWidth / 2, button.ActualHeight / 2), f.Window);
    var hit = f.Window.InputHitTest(position) as System.Windows.DependencyObject;
    Check(hit != null && (ReferenceEquals(hit, button) || button.IsAncestorOf(hit)), "Add button has no hittable center");
    button.RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
    var menu = button.ContextMenu!;
    try
    {
        var labels = menu.Items.Cast<MenuItem>().ToDictionary(item => ((TranslationProviderPreset)item.Tag).Key, item => (string)item.Header);
        Check(labels.Count == TranslationProviderPresets.All.Count, "Preset menu lost supported providers");
        Check(labels["custom"] == "Custom OpenAI-compatible provider" && labels["ollama"] == "Ollama (local)"
            && labels["gemini"] == "Gemini (OpenAI-compatible endpoint)" && labels["dashscope"] == "Qwen (DashScope)"
            && labels["zhipu"] == "Zhipu GLM", "Stable preset keys did not produce English menu labels");
        Check(JsonSerializer.Serialize(f.Settings.Current, IrisJson.Options) == before && f.Updates == 0,
            "Opening a localized menu changed saved profile data");
        Check(JsonSerializer.Serialize(TranslationProviderPresets.All, IrisJson.Options) == presets, "Preset data was modified for display localization");
    }
    finally { menu.IsOpen = false; }
    await Task.CompletedTask;
});

foreach (var fault in new[] { "host", "logger", "mutex" })
    await Run("production primary language lifetime through " + fault + " cleanup failure", () =>
    {
        PrimaryLifetimeProbe.Verify(fault);
        return Task.CompletedTask;
    });

Console.WriteLine($"Independent language scenarios: {scenarios}; failed: {failures.Count}");
if (failures.Count != 0) Environment.ExitCode = 1;

async Task Run(string name, Func<Task> body)
{
    scenarios++;
    try { await body(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures.Add(ex); Console.WriteLine("FAIL " + name + ": " + ex); }
}

async Task WindowCase(string name, Func<Scope, Task> body)
{
    await Run(name, async () =>
    {
        using var language = AppLanguage.BeginRun(); AppLanguage.Select("en-US");
        var f = new Scope(sta); var errors = new List<Exception>();
        try { await sta.Run(f.Init); await sta.Run(() => body(f)); }
        catch (Exception ex) { errors.Add(ex); }
        finally
        {
            try { await f.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
        }
        if (errors.Count != 0) throw new AggregateException(errors);
    });
}
