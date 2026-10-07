using Iris.Core.Settings;

internal static class PersistenceChecks
{
    internal static async Task Run()
    {
        foreach (string stage in new[] { "stage", "settings", "conflict", "apply", "retire", "success" })
        {
            using var area = new TempArea(); var secrets = new MemorySecrets(); var oldId = ProxyFormat.Id();
            var initial = new ProxyConfig(ProxyMode.Custom, ProxyFormat.Clean("http://proxy-a.example.test:8080"), false, oldId);
            secrets.Set(ProxyFormat.Key(oldId), ProxyFormat.Serialize(initial.CustomProxyUrl, new("synthetic-A", "synthetic-secret-A")));
            var store = new ProposalStore(area.Paths.SettingsFile); await store.InitializeAsync(initial); var observer = new PrototypeObserver(); await observer.ApplyAsync(initial);
            var editor = new ProposedEditor(store, secrets, observer); var gate = new Gate();
            editor.AfterStage = gate.Pause;
            if (stage == "stage") secrets.FailSet = true;
            if (stage == "settings") store.FailWrite = true;
            if (stage == "conflict") store.BeforeMutate = () => store.External(initial with { CustomProxyUrl = ProxyFormat.Clean("http://proxy-c.example.test:8080") });
            if (stage == "apply") observer.Fail = true;
            if (stage == "retire") secrets.FailRemove = true;
            var patch = new NetworkPatch(Url: new(initial.CustomProxyUrl, "http://proxy-b.example.test:8080"), Credentials: new(initial.CustomProxyUrl, oldId, CredentialEditKind.Replace, new("synthetic-B", "synthetic-secret-B")));
            var commit = editor.CommitAsync(patch);
            try
            {
                if (stage != "stage")
                {
                    await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var observed = await Task.Run(() => (store.Current.CustomProxyUrl, ProxyFormat.Resolve(store.Current, secrets)?.UserName));
                    Check.That(observed.CustomProxyUrl == initial.CustomProxyUrl && observed.UserName == "synthetic-A", "concurrent reader must retain old coherent pair during staged write");
                }
            }
            finally { gate.Open(); await commit; }
            var result = await commit;
            if (stage is "stage" or "settings" or "conflict")
            {
                Check.That(result.Status == SettingsEditStatus.Uncommitted && secrets.Values.ContainsKey(ProxyFormat.Key(oldId)) && secrets.Values.Count == 1, "precommit failure must keep old credentials and clean unused stage");
                Check.That(stage != "conflict" || result.IsConflict, "commit-time conflict classification");
            }
            else
            {
                Check.That(store.Current.CustomProxyUrl!.Contains("proxy-b.") && ProxyFormat.Resolve(store.Current, secrets)!.UserName == "synthetic-B", "published pair B consistent");
                if (stage is "apply" or "retire")
                {
                    Check.That(result.Status == SettingsEditStatus.ApplyPending && secrets.Values.ContainsKey(ProxyFormat.Key(oldId)), "pending must retain old reference");
                    if (stage == "apply")
                    {
                        bool blocked = false; try { observer.RequireCurrent(store.Current); } catch (IOException) { blocked = true; }
                        Check.That(blocked, "apply failure may not route new request with stale configuration");
                    }
                    int writes = store.Writes, sets = secrets.Sets; observer.Fail = false; secrets.FailRemove = false;
                    Check.That(await editor.RetryAsync(result.PendingApply!) && await editor.RetryAsync(result.PendingApply!), "receipt retry and idempotence");
                    Check.That(store.Writes == writes && secrets.Sets == sets, "retry cannot stage or write settings again");
                }
                Check.That(!secrets.Values.ContainsKey(ProxyFormat.Key(oldId)), "retire unused original reference");
                Check.That(!File.ReadAllText(area.Paths.SettingsFile).Contains("synthetic-secret") && !File.ReadAllText(area.Paths.SettingsFile + ".bak").Contains("synthetic-secret"), "settings and backup have references only");
            }
            Check.Pass("proposal versioned transaction " + stage + ": concurrentReader=" + (stage == "stage" ? "not-entered" : "passed") + " rollbackOrReceipt=true realAtomicJson=true");
        }
        await LatestFieldsAndClear();
        await CaptureRetirementBoundary();
    }
    private static async Task LatestFieldsAndClear()
    {
        using var area = new TempArea(); var secrets = new MemorySecrets(); var id = ProxyFormat.Id(); var initial = new ProxyConfig(ProxyMode.Custom, ProxyFormat.Clean("http://proxy-a.example.test:8080"), false, id);
        secrets.Set(ProxyFormat.Key(id), ProxyFormat.Serialize(initial.CustomProxyUrl, new("synthetic-A", "synthetic-secret")));
        var store = new ProposalStore(area.Paths.SettingsFile); await store.InitializeAsync(initial); var observer = new PrototypeObserver(); var editor = new ProposedEditor(store, secrets, observer);
        var unsafeKeep = await editor.CommitAsync(new(Url: new(initial.CustomProxyUrl, "http://proxy-b.example.test:8080")));
        Check.That(unsafeKeep.Status == SettingsEditStatus.Uncommitted && secrets.Values.Count == 1, "URL change cannot implicitly forward old credentials");
        store.BeforeMutate = () => store.External(store.Current with { BypassProxyOnLocal = true });
        var clear = await editor.CommitAsync(new(Credentials: new(initial.CustomProxyUrl, id, CredentialEditKind.Clear)));
        Check.That(clear.Status == SettingsEditStatus.Applied && store.Current.BypassProxyOnLocal && store.Current.ProxyCredentialSetId != null, "field merge and explicit empty reference");
        secrets.Set(ProxyFormat.LegacyKey, "stale legacy placeholder");
        Check.That(ProxyFormat.Resolve(store.Current, secrets) == null, "Clear cannot fall back to fixed legacy key");
        Check.Pass("proposal address ownership, latest-field merge and Clear never falls back to fixed legacy key");
    }
    private static async Task CaptureRetirementBoundary()
    {
        using var area = new TempArea(); var secrets = new MemorySecrets(); string oldId = ProxyFormat.Id();
        var a = new ProxyConfig(ProxyMode.Custom, ProxyFormat.Clean("http://proxy.example.test:8080"), true, oldId);
        secrets.Set(ProxyFormat.Key(oldId), ProxyFormat.Serialize(a.CustomProxyUrl, new("synthetic-A", "synthetic-secret")));
        var store = new ProposalStore(area.Paths.SettingsFile); await store.InitializeAsync(a); var observer = new PrototypeObserver(); var editor = new ProposedEditor(store, secrets, observer);
        var oldSnapshot = await editor.CaptureAsync(); Check.That((await editor.CaptureAsync()).Version == oldSnapshot.Version, "stable managed snapshot generation");
        var gate = new Gate(); editor.AfterStage = gate.Pause;
        var commit = editor.CommitAsync(new(Credentials: new(a.CustomProxyUrl, oldId, CredentialEditKind.Replace, new("synthetic-B", "synthetic-secret"))));
        Task<ManagedSnapshot>? captured = null;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); captured = editor.CaptureAsync();
            Check.That(!captured.IsCompleted, "capture shares short commit gate and cannot race retired-key removal");
        }
        finally { gate.Open(); await commit; if (captured != null) await captured; }
        Check.That(oldSnapshot.Credentials!.UserName == "synthetic-A" && !secrets.Values.ContainsKey(ProxyFormat.Key(oldId)), "old managed values remain usable after key retirement, no snapshot lease needed");
        var newSnapshot = await captured!;
        Check.That(newSnapshot.Credentials!.UserName == "synthetic-B" && newSnapshot.Version != oldSnapshot.Version, "new capture gets B generation");
        secrets.Values.Clear();
        var bypass = await editor.CaptureAsync(new Uri("http://localhost"));
        Check.That(bypass.Credentials == null, "explicit local bypass does not need broken remote proxy credentials");
        Check.Pass("proposal capture/retire shared gate: old values survive key deletion, new captures get new generation, local bypass needs no proxy credential lease");
    }
}
