using System.Windows;
using System.Windows.Controls;
using Iris.App.History;
using Iris.App.Hosting;
using Iris.App.Tray;
using Iris.Core.Actions;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using static Iris.App.Tests.History.HistoryTestScope;

namespace Iris.App.Tests.History;

[Collection("AppShutdownerSharedState")]
public sealed class HistoryWindowTests
{
    [Fact]
    public Task LazyHostEntryAndTray_ShowOneRealWindowAndQueryOnlySummaries() => Run(async f =>
    {
        f.Presenter.ActiveSession.ShouldBeNull(); f.Repository.Queries.ShouldBeEmpty(); f.Repository.Gets.ShouldBeEmpty();
        f.ProviderFactory.Requests.ShouldBeEmpty(); f.Secrets.Reads.ShouldBe(0);
        f.ProviderFactory.Created.ShouldBe(0);
        await f.Repository.Inner.AddAsync(Entry("owned history"));
        await f.Host.Services.GetRequiredService<IActionDispatcher>().DispatchAsync(IrisAction.OpenHistory, new(ActionTrigger.TrayMenu));
        await f.Vm.QueryTask; var session = f.Session;
        f.Window.IsVisible.ShouldBeTrue(); f.Vm.Rows.Count.ShouldBe(1); f.Repository.Gets.ShouldBeEmpty();
        var tray = f.Host.Services.GetRequiredService<TrayIconController>();
        typeof(TrayIconController).GetMethod("CreateContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(tray, null);
        tray.HistoryMenuItem!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await tray.LastOpenHistoryTask!; await f.Vm.QueryTask;
        f.Session.ShouldBeSameAs(session); f.Repository.Gets.ShouldBeEmpty(); f.ProviderFactory.Requests.ShouldBeEmpty();
        await Task.Run(() => f.Presenter.ShowAsync()); f.Session.ShouldBeSameAs(session);
        f.ProviderFactory.Created.ShouldBe(0);
    });

    [Fact]
    public Task ActualControls_SelectAndCopyReadOnlyFullText() => Run(async f =>
    {
        string source = new('y', 600); await f.Repository.Inner.AddAsync(Entry(source)); await f.ShowAsync();
        ((ListBox)f.Window.FindName("HistoryList")).SelectedItem = f.Vm.Rows[0]; await f.Vm.DetailTask;
        var text = (TextBox)f.Window.FindName("SourceTextBox"); text.IsReadOnly.ShouldBeTrue(); text.Text.ShouldBe(source);
        var copy = (Button)f.Window.FindName("CopySourceButton"); copy.BringIntoView(); f.Window.UpdateLayout();
        copy.IsEnabled.ShouldBeTrue(); copy.Command.Execute(null);
        await ((Iris.App.Settings.AsyncRelayCommand)copy.Command).ExecutionTask!;
        f.ClipboardWrites.Single().ShouldBe(source);
        var clear = (Button)f.Window.FindName("ClearButton"); clear.Command.Execute(null); f.Window.UpdateLayout();
        ((Border)f.Window.FindName("ConfirmationPanel")).IsVisible.ShouldBeTrue();
        ((CheckBox)f.Window.FindName("KeepFavoritesCheckBox")).IsChecked = false;
        var confirm = (Button)f.Window.FindName("ConfirmButton"); confirm.Command.Execute(null);
        await ((Iris.App.Settings.AsyncRelayCommand)confirm.Command).ExecutionTask!; await f.Vm.QueryTask;
        f.Vm.IsEmpty.ShouldBeTrue(); f.Repository.Writes.Single().Value.ShouldBeFalse();
    });

    [Theory][InlineData("中文")][InlineData("%")][InlineData("_")][InlineData("OR")][InlineData("\"quoted\"")]
    public Task SqliteLiteralSearch_MatchesTranslationsWithoutFetchingDetails(string keyword) => Run(async f =>
    {
        await f.Repository.Inner.AddAsync(Entry("source that contains no query"));
        await f.Repository.Inner.AddAsync(Entry("unrelated") with { Results = [] });
        await f.ShowAsync(); f.Vm.Keyword = keyword;
        f.Time.Advance(TimeSpan.FromMilliseconds(299)); f.Repository.Queries.Count.ShouldBe(1);
        f.Time.Advance(TimeSpan.FromMilliseconds(1)); await f.Vm.QueryTask;
        f.Vm.Rows.Count.ShouldBe(1); f.Vm.Rows[0].Preview.ShouldBe("source that contains no query"); f.Repository.Gets.ShouldBeEmpty();
    });

    [Fact]
    public Task InvalidSearchIsSafeAndRecoverable() => Run(async f =>
    {
        await f.Repository.Inner.AddAsync(Entry("recover")); await f.ShowAsync();
        f.Vm.Keyword = "private\0keyword"; f.Time.Advance(TimeSpan.FromMilliseconds(300)); await f.Vm.QueryTask;
        f.Vm.ListError.ShouldBe(UiText.HistoryKeywordError); f.Vm.ListError!.ShouldNotContain("private");
        f.Vm.Keyword = "recover"; f.Time.Advance(TimeSpan.FromMilliseconds(300)); await f.Vm.QueryTask;
        f.Vm.ListError.ShouldBeNull(); f.Vm.Rows.Count.ShouldBe(1);
    });

    [Fact]
    public Task DetailsAndCopies_UseFullTextAndOrderedHistoricalResults() => Run(async f =>
    {
        string source = string.Concat(Enumerable.Repeat("多行 source 🙂\n", 60));
        await f.Repository.Inner.AddAsync(Entry(source)); await f.ShowAsync();
        f.Vm.Rows[0].Preview.Length.ShouldBeLessThan(source.Length); f.Repository.Gets.ShouldBeEmpty();
        await f.SelectAsync(); f.Vm.Detail!.SourceText.ShouldBe(source);
        f.Vm.Detail.Results.Select(r => r.ProviderId).ShouldBe(new[] { "old-a", "old-b" });
        f.Vm.Detail.Results[0].Model.ShouldBe("old-model-a"); f.Vm.Detail.Results[1].Truncated.ShouldBeTrue();
        await f.Vm.CopySourceAsync(); await f.Vm.CopyResultAsync("old-b");
        f.ClipboardWrites.ToArray().ShouldBe(new[] { source, "Second result\n多行译文\n[结果已截断]" });
        f.Repository.Gets.Count.ShouldBe(3); f.ProviderFactory.Requests.ShouldBeEmpty();
    });

    [Fact]
    public Task Retranslate_UsesActualPresenterAndCurrentProviderWithFullSource() => Run(async f =>
    {
        string source = new('x', 500); long id = await f.Repository.Inner.AddAsync(Entry(source, favorite: true));
        await f.ShowAsync(); await f.SelectAsync();
        f.Settings.External(s => s with { Translation = s.Translation with { Providers = [s.Translation.Providers[0] with { Model = "latest-model" }] } });
        await f.Vm.RetranslateAsync(); await f.ProviderFactory.Called.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.ProviderFactory.Requests.Count.ShouldBe(1);
        var call = f.ProviderFactory.Requests.Single(); call.Model.ShouldBe("latest-model"); call.Request.Text.ShouldBe(source);
        (await f.Repository.Inner.GetAsync(id))!.IsFavorite.ShouldBeTrue(); f.Repository.Gets.Count.ShouldBe(2);
        f.Vm.Status.ShouldBe(UiText.HistoryTranslated);
    });

    [Fact]
    public Task DeleteConfirmation_FixesIdAndCancelDoesNotWrite() => Run(async f =>
    {
        long first = await f.Repository.Inner.AddAsync(Entry("first")); long second = await f.Repository.Inner.AddAsync(Entry("second"));
        await f.ShowAsync(); await f.SelectAsync(); f.Vm.SelectedRow!.Id.ShouldBe(second);
        f.Vm.RequestDelete(); f.Vm.CancelConfirmation(); f.Repository.Writes.ShouldBeEmpty();
        f.Vm.RequestDelete(); await f.SelectAsync(1); f.Vm.SelectedRow!.Id.ShouldBe(first);
        await f.Vm.ConfirmAsync(); await f.Vm.QueryTask;
        (await f.Repository.Inner.GetAsync(second)).ShouldBeNull(); (await f.Repository.Inner.GetAsync(first)).ShouldNotBeNull();
        f.Repository.Writes.Single().Id.ShouldBe(second);
    });

    [Theory][InlineData(true)][InlineData(false)]
    public Task Clear_UsesConfirmedScopeAndKeepsFavoritesByDefault(bool keep) => Run(async f =>
    {
        await f.Repository.Inner.AddAsync(Entry("favorite", favorite: true)); await f.Repository.Inner.AddAsync(Entry("ordinary"));
        await f.ShowAsync(); f.Vm.RequestClear(); f.Vm.KeepFavorites.ShouldBeTrue();
        f.Vm.CancelConfirmation(); f.Repository.Writes.ShouldBeEmpty();
        f.Vm.RequestClear(); f.Vm.KeepFavorites = keep; await f.Vm.ConfirmAsync(); await f.Vm.QueryTask;
        f.Vm.TotalCount.ShouldBe(keep ? 1 : 0); f.Repository.Writes.Single().Value.ShouldBe(keep);
        if (keep) f.Vm.Rows.Single().Summary.IsFavorite.ShouldBeTrue();
    });

    [Theory][InlineData(false)][InlineData(true)]
    public Task LastPageMutation_FallsBackOnceAndInvalidatesDetails(bool favorites) => Run(async f =>
    {
        for (int i = 0; i < 51; i++) await f.Repository.Inner.AddAsync(Entry("entry-" + i, favorites));
        await f.ShowAsync(); if (favorites) { f.Vm.FavoritesOnly = true; await f.Vm.QueryTask; }
        await f.Vm.RequestQueryAsync(1); f.Vm.Rows.Count.ShouldBe(1); await f.SelectAsync();
        int before = f.Repository.Queries.Count;
        if (favorites) await f.Vm.ToggleFavoriteAsync();
        else { f.Vm.RequestDelete(); await f.Vm.ConfirmAsync(); }
        await f.Vm.QueryTask; f.Vm.PageIndex.ShouldBe(0); f.Vm.TotalCount.ShouldBe(50); f.Vm.Rows.Count.ShouldBe(50);
        f.Repository.Queries.Count.ShouldBe(before + 2); f.Vm.Detail.ShouldBeNull();
    });

    [Fact]
    public Task MissingEntryAndWriteFailure_PreserveClipboardAndRecover() => Run(async f =>
    {
        long id = await f.Repository.Inner.AddAsync(Entry("record")); await f.ShowAsync(); await f.SelectAsync();
        f.Repository.BeforeWrite = () => throw new IOException("private database path");
        await f.Vm.ToggleFavoriteAsync(); f.Vm.Status.ShouldBe(UiText.HistoryStorageError);
        (await f.Repository.Inner.GetAsync(id))!.IsFavorite.ShouldBeFalse();
        f.Repository.BeforeWrite = null; await f.Vm.ToggleFavoriteAsync(); await f.Vm.QueryTask; await f.SelectAsync();
        await f.Repository.Inner.DeleteAsync(id); await f.Vm.CopySourceAsync(); await f.Vm.QueryTask;
        f.Vm.Status.ShouldBe(UiText.HistoryMissing); f.ClipboardWrites.ShouldBeEmpty(); f.Vm.IsEmpty.ShouldBeTrue();
    });
}
