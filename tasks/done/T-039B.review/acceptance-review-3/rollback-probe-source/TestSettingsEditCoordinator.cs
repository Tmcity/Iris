using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Platform;
using Iris.Core.Settings;
using Iris.Core.Translation;

namespace Iris.App.Tests.Settings;

internal sealed class TestSettingsEditCoordinator : ISettingsEditCoordinator
{
        public IOcrDraftTestSession CreateOcrDraftTest(OcrProviderDraft draft) => throw new NotSupportedException();

    private readonly ISettingsService? _settingsService;

    public Func<SettingsEditPlan, CancellationToken, Task<SettingsEditResult>>? OnCommitAsync { get; set; }
    public Func<ISettingsApplyReceipt, CancellationToken, Task<SettingsApplyResult>>? OnRetryApplyAsync { get; set; }
    public Func<TranslationProviderDraft, CancellationToken, Task<ProviderTestResult>>? OnTestDraftAsync { get; set; }
    public Func<TranslationProviderDraft, CancellationToken, Task<IReadOnlyList<string>>>? OnListDraftModelsAsync { get; set; }

    public TestSettingsEditCoordinator(ISettingsService? settingsService = null)
    {
        _settingsService = settingsService;
    }

    public async Task<SettingsEditResult> CommitAsync(SettingsEditPlan plan, CancellationToken cancellationToken = default)
    {
        if (OnCommitAsync != null)
        {
            return await OnCommitAsync(plan, cancellationToken).ConfigureAwait(false);
        }

        if (_settingsService != null && plan.General != null)
        {
            var p = plan.General;
            try
            {
                await _settingsService.UpdateAsync(current =>
                {
                    var g = current.General;
                    if (p.Theme != null && g.Theme != p.Theme.Base && g.Theme != p.Theme.Target)
                    {
                        throw new InvalidOperationException("Theme conflict");
                    }
                    if (p.LaunchAtStartup != null && g.LaunchAtStartup != p.LaunchAtStartup.Base && g.LaunchAtStartup != p.LaunchAtStartup.Target)
                    {
                        throw new InvalidOperationException("LaunchAtStartup conflict");
                    }
                    if (p.ShowStartupNotification != null && g.ShowStartupNotification != p.ShowStartupNotification.Base && g.ShowStartupNotification != p.ShowStartupNotification.Target)
                    {
                        throw new InvalidOperationException("ShowStartupNotification conflict");
                    }
                    if (p.LogLevel != null && !string.Equals(g.LogLevel, p.LogLevel.Base, StringComparison.OrdinalIgnoreCase) && !string.Equals(g.LogLevel, p.LogLevel.Target, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("LogLevel conflict");
                    }

                    var updated = g with
                    {
                        Theme = p.Theme != null ? p.Theme.Target : g.Theme,
                        LaunchAtStartup = p.LaunchAtStartup != null ? p.LaunchAtStartup.Target : g.LaunchAtStartup,
                        ShowStartupNotification = p.ShowStartupNotification != null ? p.ShowStartupNotification.Target : g.ShowStartupNotification,
                        LogLevel = p.LogLevel != null ? p.LogLevel.Target : g.LogLevel,
                    };
                    return current with { General = updated };
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                bool isConflict = ex.Message.Contains("conflict", StringComparison.OrdinalIgnoreCase);
                return new SettingsEditResult(SettingsEditStatus.Uncommitted, IsConflict: isConflict, Error: new ErrorInfo(ErrorCode.Unknown, ex.Message));
            }
        }

        return new SettingsEditResult(SettingsEditStatus.Applied);
    }

    public Task<SettingsApplyResult> RetryApplyAsync(ISettingsApplyReceipt receipt, CancellationToken cancellationToken = default)
    {
        return OnRetryApplyAsync != null
            ? OnRetryApplyAsync(receipt, cancellationToken)
            : Task.FromResult(new SettingsApplyResult(true));
    }

    public Task<ProviderTestResult> TestDraftAsync(TranslationProviderDraft draft, CancellationToken cancellationToken = default)
    {
        return OnTestDraftAsync != null
            ? OnTestDraftAsync(draft, cancellationToken)
            : Task.FromResult(new ProviderTestResult(true, TimeSpan.FromMilliseconds(10), "ok"));
    }

    public Task<IReadOnlyList<string>> ListDraftModelsAsync(TranslationProviderDraft draft, CancellationToken cancellationToken = default)
    {
        return OnListDraftModelsAsync != null
            ? OnListDraftModelsAsync(draft, cancellationToken)
            : Task.FromResult<IReadOnlyList<string>>([]);
    }
}
