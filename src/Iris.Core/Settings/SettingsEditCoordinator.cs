using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Languages;
using Iris.Core.Ocr;
using Iris.Core.Security;
using Iris.Core.Translation;
using Microsoft.Extensions.Logging;

namespace Iris.Core.Settings;

/// <summary>
/// Core 配置发布协调器实现（specs/PROVIDER_EDITING.md、specs/PROVIDER_REGISTRY.md §2.2）。
/// 负责捕获、输入校验、凭据 staging、一次原子发布、观察者刷新屏障及旧凭据清理。
/// </summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Singleton service; SemaphoreSlim does not allocate unmanaged resources unless AvailableWaitHandle is accessed.")]
internal sealed partial class SettingsEditCoordinator : ISettingsEditCoordinator, IDisposable
{
    private static string ConflictMessage => global::Iris.Core.Localization.CoreText.Get("Text_646cfa7d6429");

    private static readonly HashSet<string> ValidLogLevels = new(StringComparer.Ordinal)
    {
        "Verbose", "Debug", "Information", "Warning", "Error"
    };

    private static readonly HashSet<string> SensitiveHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Proxy-Authorization", "x-api-key", "api-key"
    };

    private static readonly HashSet<string> ValidAnthropicEfforts = new(StringComparer.Ordinal)
    {
        "low", "medium", "high", "xhigh", "max"
    };

    [GeneratedRegex(@"\Atp-[0-9a-f]{12}\z")]
    private static partial Regex NewProfileIdRegex();

    [GeneratedRegex(@"\Acs-[0-9a-f]{32}\z")]
    private static partial Regex CredentialSetIdRegex();

    private readonly ISettingsService _settingsService;
    private readonly ISecretStore _secretStore;
    private readonly ITranslationProviderRegistry _providerRegistry;
    private readonly IOcrProviderRegistry? _ocrProviderRegistry;
    private readonly IEnumerable<IProviderConfigurationObserver> _configurationObservers;
    private readonly IPromptTemplateProvider _promptTemplateProvider;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SettingsEditCoordinator> _logger;
    private readonly IHotkeyBindingPolicy _hotkeyBindingPolicy;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Guid _instanceId = Guid.NewGuid();

    public SettingsEditCoordinator(
        ISettingsService settingsService,
        ISecretStore secretStore,
        ITranslationProviderRegistry providerRegistry,
        IEnumerable<IProviderConfigurationObserver> configurationObservers,
        IPromptTemplateProvider promptTemplateProvider,
        TimeProvider timeProvider,
        ILogger<SettingsEditCoordinator> logger,
        IHotkeyBindingPolicy? hotkeyBindingPolicy = null,
        IOcrProviderRegistry? ocrProviderRegistry = null,
        IEnumerable<Iris.Core.Network.INetworkConfigurationObserver>? networkObservers = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
        _ocrProviderRegistry = ocrProviderRegistry;
        _networkObservers = networkObservers ?? [];
        _configurationObservers = configurationObservers ?? throw new ArgumentNullException(nameof(configurationObservers));
        _promptTemplateProvider = promptTemplateProvider ?? throw new ArgumentNullException(nameof(promptTemplateProvider));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _hotkeyBindingPolicy = hotkeyBindingPolicy ?? new HotkeyBindingPolicy();
    }

    public void Dispose()
    {
        if (_networkSubscribed) _secretStore.Changed -= OnNetworkSecretChanged;
    }

    public async Task<SettingsEditResult> CommitAsync(SettingsEditPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // 1. 同步结构与参数前置校验（拒绝 null 元素与未定义枚举）
        var structureError = ValidatePlanStructure(plan);
        if (structureError != null)
        {
            return new SettingsEditResult(SettingsEditStatus.Uncommitted, IsConflict: false, Error: structureError);
        }

        // 2. 同步防御性捕获并克隆所有输入集合（按 Kind 补齐必需默认 Options）
        var capturedPlan = ClonePlan(plan);
        var historyError = ValidateHistory(capturedPlan.History);
        if (historyError != null) return new SettingsEditResult(SettingsEditStatus.Uncommitted, Error: historyError);
        var selectionError = ValidateSelection(capturedPlan.Selection);
        if (selectionError != null) return new SettingsEditResult(SettingsEditStatus.Uncommitted, Error: selectionError);
        capturedPlan = capturedPlan with { Selection = NormalizeSelection(capturedPlan.Selection), Ocr = NormalizeOcrPatch(capturedPlan.Ocr) };

        // 3. 无操作检查（不制造无谓设置版本）
        if (IsNoOp(capturedPlan))
        {
            return new SettingsEditResult(SettingsEditStatus.Applied);
        }

        // 4. 同步前置校验（基于当前快照的静态和语义校验）
        if (_settingsService.IsReadOnly) return new(SettingsEditStatus.Uncommitted, Error: Iris.Core.Network.NetworkProxyCodec.StorageFailure().ToErrorInfo());
        var currentSnapshot = _settingsService.Current;
        var networkError = ValidateNetwork(capturedPlan.Network, currentSnapshot.Network);
        if (networkError != null) return new(SettingsEditStatus.Uncommitted, Error: networkError);
        var validationError = ValidatePlan(capturedPlan, currentSnapshot);
        if (validationError != null)
        {
            return new SettingsEditResult(SettingsEditStatus.Uncommitted, IsConflict: false, Error: validationError);
        }

        // 5. 取得单一异步串行权
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var stagedSetIds = new List<string>();
        var stagedNetworkIds = new List<string>();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 6. 前置冲突初检并准备凭据 staging 意图
            if (_settingsService.IsReadOnly) return new(SettingsEditStatus.Uncommitted, Error: Iris.Core.Network.NetworkProxyCodec.StorageFailure().ToErrorInfo());
            var latest = _settingsService.Current;
            if (ValidateNetwork(capturedPlan.Network, latest.Network) is { } latestNetworkError) return new(SettingsEditStatus.Uncommitted, Error: latestNetworkError);
            var stagingPlan = PrepareStagingAndCheckConflicts(capturedPlan, latest);
            if (stagingPlan.ConflictError != null)
            {
                return new SettingsEditResult(SettingsEditStatus.Uncommitted, IsConflict: true, Error: stagingPlan.ConflictError);
            }

            var networkCredentialError = await ValidateKeptNetworkCredentialsAsync(capturedPlan.Network, latest.Network, cancellationToken).ConfigureAwait(false);
            if (networkCredentialError != null) return new(SettingsEditStatus.Uncommitted, Error: networkCredentialError);

            // 7. 执行凭据 staging
            try
            {
                if (stagingPlan.NetworkSetId is { } networkId)
                {
                    await Task.Run(() =>
                    {
                        try { _secretStore.Set(SecretKeys.ProxyCredentialSet(networkId), stagingPlan.NetworkSetValue!); }
                        catch { throw Iris.Core.Network.NetworkProxyCodec.StorageFailure(); }
                    }, cancellationToken).ConfigureAwait(false);
                    stagedNetworkIds.Add(networkId);
                }
                foreach (var (newSetId, apiKey, secretKey) in stagingPlan.CredentialsToStage)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var json = SerializeCredentialSet(apiKey, secretKey);
                    await Task.Run(() => _secretStore.Set(SecretKeys.CredentialSet(newSetId), json), cancellationToken).ConfigureAwait(false);
                    stagedSetIds.Add(newSetId);
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                await RollbackStagedCredentialsAsync(stagedSetIds).ConfigureAwait(false);
                await CleanupNetworkCandidatesAsync(stagedNetworkIds).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                await RollbackStagedCredentialsAsync(stagedSetIds).ConfigureAwait(false);
                await CleanupNetworkCandidatesAsync(stagedNetworkIds).ConfigureAwait(false);
                LogSecretStoreSetFailed(_logger, ex.GetType().Name);
                var err = MapStorageException(ex);
                return new SettingsEditResult(SettingsEditStatus.Uncommitted, IsConflict: false, Error: err);
            }

            // 8. 一次 UpdateAsync 原子提交（在 mutate 内重新做冲突判断）
            // 注意：cancellationToken 一直有效到 UpdateAsync 正常返回！
            try
            {
                await _settingsService.UpdateAsync(cur =>
                {
                    return MutateSettings(cur, capturedPlan, stagingPlan.NewCredentialSetMap, stagingPlan.NetworkSetId);
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await RollbackStagedCredentialsAsync(stagedSetIds).ConfigureAwait(false);
                await CleanupNetworkCandidatesAsync(stagedNetworkIds).ConfigureAwait(false);
                throw;
            }
            catch (StagingConflictException scEx)
            {
                await RollbackStagedCredentialsAsync(stagedSetIds).ConfigureAwait(false);
                await CleanupNetworkCandidatesAsync(stagedNetworkIds).ConfigureAwait(false);
                return new SettingsEditResult(SettingsEditStatus.Uncommitted, IsConflict: true, Error: scEx.ErrorInfo);
            }
            catch (Exception ex)
            {
                await RollbackStagedCredentialsAsync(stagedSetIds).ConfigureAwait(false);
                await CleanupNetworkCandidatesAsync(stagedNetworkIds).ConfigureAwait(false);
                LogSettingsUpdateFailed(_logger, ex.GetType().Name);
                var err = MapStorageException(ex);
                return new SettingsEditResult(SettingsEditStatus.Uncommitted, IsConflict: false, Error: err);
            }

            // 提交点（Commit Point）已达成！后续阶段忽略 caller 迟到取消。
            var commitId = Guid.NewGuid();
            var receipt = new SettingsApplyReceipt(
                commitId,
                _instanceId,
                stagingPlan.CandidateSetIds,
                stagingPlan.CandidateLegacyProfileIds)
            {
                RequiresNetwork = HasNetworkEdits(capturedPlan.Network),
                RequiresProviders = capturedPlan.TranslationChanges.Count != 0 || capturedPlan.TranslationOrder != null
                    || HasOcrEdits(capturedPlan) || HasScreenshotEdits(capturedPlan.Screenshot),
                RemainingNetworkCandidates = stagingPlan.NetworkCandidates
            };

            // 若无 Provider 变更且无排序改动（仅 General 变更），无需观察者屏障和凭据清理
            if (!receipt.RequiresProviders && !receipt.RequiresNetwork)
            {
                return new SettingsEditResult(SettingsEditStatus.Applied);
            }

            // 9. 观察者刷新屏障
            var observerSuccess = await RefreshReceiptObserversAsync(receipt).ConfigureAwait(false);
            if (!observerSuccess)
            {
                receipt.ObserversCompleted = false;
                return new SettingsEditResult(
                    SettingsEditStatus.ApplyPending,
                    IsConflict: false,
                    Error: new ErrorInfo(receipt.NetworkFailed ? ErrorCode.NetworkUnavailable : ErrorCode.ProviderUnavailable,
                        receipt.RequiresNetwork ? global::Iris.Core.Localization.CoreText.Get("Text_5a6eb355f5d0")
                            : global::Iris.Core.Localization.CoreText.Get("Text_8ebb1568643a")),
                    PendingApply: receipt);
            }

            receipt.ObserversCompleted = true;

            // 10. 旧凭据清理（已通过屏障）
            var cleanupSuccess = await CleanupReceiptCandidatesAsync(receipt).ConfigureAwait(false);
            if (!cleanupSuccess)
            {
                return new SettingsEditResult(
                    SettingsEditStatus.ApplyPending,
                    IsConflict: false,
                    Error: new ErrorInfo(ErrorCode.StorageFailure, global::Iris.Core.Localization.CoreText.Get("Text_823a12477db7")),
                    PendingApply: receipt);
            }

            return new SettingsEditResult(SettingsEditStatus.Applied);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SettingsApplyResult> RetryApplyAsync(ISettingsApplyReceipt receipt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        if (receipt is not SettingsApplyReceipt ourReceipt || ourReceipt.CoordinatorInstanceId != _instanceId)
        {
            throw new ArgumentException("无效或未知的回执对象", nameof(receipt));
        }

        if (ourReceipt.IsComplete)
        {
            return new SettingsApplyResult(true);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ourReceipt.IsComplete)
            {
                return new SettingsApplyResult(true);
            }

            // 重新刷新观察者屏障：每次未完成重试必须重新建立当前屏障
            var observerSuccess = await RefreshReceiptObserversAsync(ourReceipt).ConfigureAwait(false);
            if (!observerSuccess)
            {
                ourReceipt.ObserversCompleted = false;
                return new SettingsApplyResult(false, new ErrorInfo(ourReceipt.NetworkFailed ? ErrorCode.NetworkUnavailable : ErrorCode.ProviderUnavailable,
                    ourReceipt.NetworkFailed ? global::Iris.Core.Localization.CoreText.Get("Text_99ba5ea63b92") : global::Iris.Core.Localization.CoreText.Get("Text_b08a81be972d")));
            }

            ourReceipt.ObserversCompleted = true;

            // 清理剩余候选
            var cleanupSuccess = await CleanupReceiptCandidatesAsync(ourReceipt).ConfigureAwait(false);
            if (!cleanupSuccess)
            {
                return new SettingsApplyResult(false, new ErrorInfo(ErrorCode.StorageFailure, global::Iris.Core.Localization.CoreText.Get("Text_2fb7d75c324d")));
            }

            return new SettingsApplyResult(true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProviderTestResult> TestDraftAsync(TranslationProviderDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        cancellationToken.ThrowIfCancellationRequested();

        // 1. 结构与参数前置校验
        var structureError = ValidateDraftStructure(draft);
        if (structureError != null)
        {
            return new ProviderTestResult(
                false,
                TimeSpan.Zero,
                Error: structureError);
        }

        // 2. 深度克隆并补齐默认 Options
        var capturedDraft = CloneDraft(draft);

        // 3. 校验草稿（包含翻译测试要求的模型与生成参数校验）
        AppSettings? currentSettings = null;
        if (capturedDraft.Profile.PromptTemplateId != null)
        {
            try
            {
                currentSettings = _settingsService.Current;
            }
            catch (Exception ex)
            {
                return new ProviderTestResult(
                    false,
                    TimeSpan.Zero,
                    Error: new ErrorInfo(ErrorCode.Unknown, global::Iris.Core.Localization.CoreText.Get("Text_13f5554c85ea"), Detail: ex.GetType().Name, ProviderId: capturedDraft.Profile.Id));
            }
        }

        var draftError = ValidateDraft(capturedDraft, isTranslationTest: true, currentSettings: currentSettings);
        if (draftError != null)
        {
            return new ProviderTestResult(
                false,
                TimeSpan.Zero,
                Error: draftError);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 4. 解析独立凭据（含 Keep 的当前基线比对与后台线程读取）
        ProviderCredentials credentials;
        try
        {
            var res = await ResolveDraftCredentialsAsync(capturedDraft, cancellationToken).ConfigureAwait(false);
            if (res.Error != null)
            {
                return new ProviderTestResult(
                    false,
                    TimeSpan.Zero,
                    Error: res.Error);
            }

            credentials = res.Credentials ?? ProviderCredentials.None;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ProviderTestResult(
                false,
                TimeSpan.Zero,
                Error: new ErrorInfo(ErrorCode.Unknown, global::Iris.Core.Localization.CoreText.Get("Text_0ee8cdd68ce4"), Detail: ex.GetType().Name, ProviderId: capturedDraft.Profile.Id));
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 5. 创建瞬态快照并在其作用域内调用实际测试
        try
        {
            var snapshot = await _providerRegistry.CreateTransientAsync(capturedDraft.Profile, credentials, cancellationToken).ConfigureAwait(false);
            try
            {
                var reg = snapshot.Providers.Count > 0 ? snapshot.Providers[0] : null;
                if (reg == null)
                {
                    return new ProviderTestResult(
                        false,
                        TimeSpan.Zero,
                        Error: new ErrorInfo(ErrorCode.NotConfigured, ErrorMessages.GetDefault(ErrorCode.NotConfigured), Detail: global::Iris.Core.Localization.CoreText.Get("Text_e9867e8a22d2"), ProviderId: capturedDraft.Profile.Id));
                }

                cancellationToken.ThrowIfCancellationRequested();
                return await reg.Provider.TestAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                snapshot.Dispose();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IrisException irisEx)
        {
            return new ProviderTestResult(
                false,
                TimeSpan.Zero,
                Error: irisEx.ToErrorInfo());
        }
        catch (Exception ex)
        {
            return new ProviderTestResult(
                false,
                TimeSpan.Zero,
                Error: new ErrorInfo(ErrorCode.ProviderUnavailable, global::Iris.Core.Localization.CoreText.Get("Text_c9e24bc3378d"), Detail: ex.GetType().Name, ProviderId: capturedDraft.Profile.Id));
        }
    }

    public async Task<IReadOnlyList<string>> ListDraftModelsAsync(TranslationProviderDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // 1. 结构与参数前置校验
            var structureError = ValidateDraftStructure(draft);
            if (structureError != null)
            {
                throw new IrisException(
                    structureError.Code,
                    structureError.Message,
                    innerException: null,
                    retryable: false,
                    providerId: draft.Profile?.Id ?? string.Empty,
                    detail: structureError.Detail);
            }

            // 2. 深度克隆并补齐默认 Options
            var capturedDraft = CloneDraft(draft);

            // 3. 校验草稿（模型列表不要求模型/Prompt/生成参数）
            var draftError = ValidateDraft(capturedDraft, isTranslationTest: false, currentSettings: null);
            if (draftError != null)
            {
                throw new IrisException(
                    draftError.Code,
                    draftError.Message,
                    innerException: null,
                    retryable: false,
                    providerId: capturedDraft.Profile.Id,
                    detail: draftError.Detail);
            }

            // 4. 解析独立凭据
            DraftCredentialsResult res;
            try
            {
                res = await ResolveDraftCredentialsAsync(capturedDraft, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new IrisException(
                    ErrorCode.Unknown,
                    global::Iris.Core.Localization.CoreText.Get("Text_0ee8cdd68ce4"),
                    innerException: null,
                    retryable: false,
                    providerId: capturedDraft.Profile.Id,
                    detail: ex.GetType().Name);
            }

            if (res.Error != null)
            {
                throw new IrisException(
                    res.Error.Code,
                    res.Error.Message,
                    innerException: null,
                    retryable: false,
                    providerId: capturedDraft.Profile.Id,
                    detail: res.Error.Detail);
            }

            var credentials = res.Credentials ?? ProviderCredentials.None;

            // 5. 创建瞬态快照并在其作用域内调用实际模型获取
            var snapshot = await _providerRegistry.CreateTransientAsync(capturedDraft.Profile, credentials, cancellationToken).ConfigureAwait(false);
            try
            {
                var reg = snapshot.Providers.Count > 0 ? snapshot.Providers[0] : null;
                if (reg == null)
                {
                    throw new IrisException(
                        ErrorCode.NotConfigured,
                        ErrorMessages.GetDefault(ErrorCode.NotConfigured),
                        innerException: null,
                        retryable: false,
                        providerId: capturedDraft.Profile.Id,
                        detail: global::Iris.Core.Localization.CoreText.Get("Text_e9867e8a22d2"));
                }

                if (reg.Provider is not IModelCatalogSource catalogSource)
                {
                    throw new IrisException(
                        ErrorCode.NotConfigured,
                        global::Iris.Core.Localization.CoreText.Get("Text_d42544701705"),
                        innerException: null,
                        retryable: false,
                        providerId: capturedDraft.Profile.Id);
                }

                return await catalogSource.ListModelsAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                snapshot.Dispose();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IrisException irisEx)
        {
            if (irisEx.InnerException != null)
            {
                throw new IrisException(
                    irisEx.Code,
                    irisEx.Message,
                    innerException: null,
                    httpStatus: irisEx.HttpStatus,
                    retryable: irisEx.Retryable,
                    providerId: irisEx.ProviderId ?? draft.Profile?.Id ?? string.Empty,
                    detail: irisEx.Detail);
            }
            throw;
        }
        catch (Exception ex)
        {
            throw new IrisException(
                ErrorCode.ProviderUnavailable,
                global::Iris.Core.Localization.CoreText.Get("Text_813bec599861"),
                innerException: null,
                retryable: false,
                providerId: draft.Profile?.Id ?? string.Empty,
                detail: ex.GetType().Name);
        }
    }

    private sealed record DraftCredentialsResult(ProviderCredentials? Credentials, ErrorInfo? Error = null);

    private async Task<DraftCredentialsResult> ResolveDraftCredentialsAsync(TranslationProviderDraft draft, CancellationToken cancellationToken)
    {
        switch (draft.Credentials.Kind)
        {
            case CredentialEditKind.Replace:
                return new DraftCredentialsResult(draft.Credentials.Replacement ?? ProviderCredentials.None);

            case CredentialEditKind.Clear:
                return new DraftCredentialsResult(ProviderCredentials.None);

            case CredentialEditKind.Keep:
                if (draft.BaseProfile == null)
                {
                    // 新草稿没有 base，Keep 对应空凭据
                    return new DraftCredentialsResult(ProviderCredentials.None);
                }

                // 已有草稿 Keep：在短串行锁内核对基线配置并捕获当前 Profile，并持有锁直到读取完成
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    AppSettings current;
                    try
                    {
                        current = _settingsService.Current;
                    }
                    catch (Exception ex)
                    {
                        return new DraftCredentialsResult(null, new ErrorInfo(ErrorCode.Unknown, global::Iris.Core.Localization.CoreText.Get("Text_13f5554c85ea"), Detail: ex.GetType().Name, ProviderId: draft.Profile.Id));
                    }

                    var match = current.Translation.Providers.FirstOrDefault(p => string.Equals(p.Id, draft.BaseProfile.Id, StringComparison.Ordinal));
                    if (match == null)
                    {
                        return new DraftCredentialsResult(null, new ErrorInfo(ErrorCode.Unknown, global::Iris.Core.Localization.CoreText.Get("Text_c5d4b8310a81"), Detail: "BaseProfileNotFound", ProviderId: draft.Profile.Id));
                    }

                    if (!IsProfileDeepEqual(draft.BaseProfile, match))
                    {
                        return new DraftCredentialsResult(null, new ErrorInfo(ErrorCode.Unknown, ConflictMessage, Detail: "BaseProfileConflict", ProviderId: draft.Profile.Id));
                    }

                    if (!string.Equals(draft.Profile.Id, draft.BaseProfile.Id, StringComparison.Ordinal))
                    {
                        return new DraftCredentialsResult(null, new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_c867164793e4"), Detail: "DraftIdMismatch", ProviderId: draft.Profile.Id));
                    }

                    if (!string.Equals(draft.Profile.CredentialSetId, draft.BaseProfile.CredentialSetId, StringComparison.Ordinal))
                    {
                        return new DraftCredentialsResult(null, new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_5692606726ab"), Detail: "CredentialSetIdMismatch", ProviderId: draft.Profile.Id));
                    }

                    var snapshotProfile = CloneProfile(match);

                    // 在后台线程执行凭据解析，避免在调用线程/UI 线程阻塞执行 SecretStore I/O
                    var resolveTask = Task.Run(() => ProviderCredentialResolver.Resolve(snapshotProfile, _secretStore, _logger), CancellationToken.None);
                    CredentialResolutionResult resolveResult;
                    try
                    {
                        resolveResult = await resolveTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // 取消已开始的同步 I/O 时观察它退出后再释放所有权
                        try
                        {
                            await resolveTask.ConfigureAwait(false);
                        }
                        catch
                        {
                            // 忽略后台任务的非取消异常
                        }
                        throw;
                    }
                    catch (Exception ex)
                    {
                        return new DraftCredentialsResult(null, new ErrorInfo(ErrorCode.StorageFailure, ErrorMessages.GetDefault(ErrorCode.StorageFailure), Detail: ex.GetType().Name, ProviderId: draft.Profile.Id));
                    }

                    if (resolveResult.IsExplicitUnconfigured || resolveResult.Credentials == null)
                    {
                        return new DraftCredentialsResult(null, new ErrorInfo(ErrorCode.NotConfigured, ErrorMessages.GetDefault(ErrorCode.NotConfigured), Detail: global::Iris.Core.Localization.CoreText.Get("Text_cbf370d77d1b"), ProviderId: draft.Profile.Id));
                    }

                    return new DraftCredentialsResult(resolveResult.Credentials);
                }
                finally
                {
                    _gate.Release();
                }

            default:
                return new DraftCredentialsResult(null, new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_3b5f7f06db59")));
        }
    }

    private async Task<bool> RefreshObserversAsync()
    {
        var observers = _configurationObservers.ToList();
        if (observers.Count == 0)
        {
            return true;
        }

        var tasks = observers.Select(async obs =>
        {
            try
            {
                await obs.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                LogObserverRefreshFailed(_logger, obs.GetType().Name, ex.GetType().Name);
                return false;
            }
        });

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.All(r => r);
    }

    private async Task<bool> CleanupReceiptCandidatesAsync(SettingsApplyReceipt receipt)
    {
        var allCleaned = await CleanupNetworkCandidatesAsync(receipt.RemainingNetworkCandidates).ConfigureAwait(false);

        // 清理凭据集候选
        var candidateSetIds = receipt.RemainingCandidateSetIds.ToList();
        foreach (var candidateId in candidateSetIds)
        {
            if (!CredentialSetIdRegex().IsMatch(candidateId))
            {
                receipt.RemainingCandidateSetIds.Remove(candidateId);
                continue;
            }

            if (IsSetReferenced(_settingsService.Current, candidateId))
            {
                // 仍有其他 Profile 引用，不能删除
                receipt.RemainingCandidateSetIds.Remove(candidateId);
                continue;
            }

            try
            {
                await Task.Run(() => _secretStore.Remove(SecretKeys.CredentialSet(candidateId)), CancellationToken.None).ConfigureAwait(false);
                receipt.RemainingCandidateSetIds.Remove(candidateId);
            }
            catch (Exception ex)
            {
                LogCandidateCleanupFailed(_logger, candidateId, ex.GetType().Name);
                allCleaned = false;
            }
        }

        // 清理 legacy 候选
        var candidateLegacyIds = receipt.RemainingCandidateLegacyProfileIds.ToList();
        foreach (var profileId in candidateLegacyIds)
        {
            if (IsLegacyReferenced(_settingsService.Current, profileId))
            {
                // 仍有相同 ID 的 legacy 使用者，不能删除
                receipt.RemainingCandidateLegacyProfileIds.Remove(profileId);
                continue;
            }

            try
            {
                await Task.Run(() =>
                {
                    _secretStore.Remove(SecretKeys.ApiKey(profileId));
                    _secretStore.Remove(SecretKeys.SecretKey(profileId));
                }, CancellationToken.None).ConfigureAwait(false);
                receipt.RemainingCandidateLegacyProfileIds.Remove(profileId);
            }
            catch (Exception ex)
            {
                LogCandidateCleanupFailed(_logger, profileId, ex.GetType().Name);
                allCleaned = false;
            }
        }

        return allCleaned;
    }

    private async Task RollbackStagedCredentialsAsync(List<string> stagedSetIds)
    {
        foreach (var id in stagedSetIds)
        {
            if (IsSetReferenced(_settingsService.Current, id))
            {
                continue;
            }

            try
            {
                await Task.Run(() => _secretStore.Remove(SecretKeys.CredentialSet(id)), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogRollbackFailed(_logger, id, ex.GetType().Name);
            }
        }
    }

    private StagingPlan PrepareStagingAndCheckConflicts(SettingsEditPlan plan, AppSettings current)
    {
        var credentialsToStage = new List<(string NewSetId, string? ApiKey, string? SecretKey)>();
        var newCredentialSetMap = new Dictionary<CredentialTarget, string?>();
        var candidateSetIds = new List<string>();
        var candidateLegacyProfileIds = new List<string>();

        // 1. General 补丁冲突检查
        if (plan.General != null)
        {
            if (HasGeneralConflict(plan.General, current.General))
            {
                return StagingPlan.Conflict(new ErrorInfo(ErrorCode.Unknown, ConflictMessage));
            }
        }

        // 2. Profile Changes 冲突初检与凭据规划
        if (HasSelectionConflict(plan.Selection, current.Selection) || HasHistoryConflict(plan.History, current.History))
            return StagingPlan.Conflict(new ErrorInfo(ErrorCode.Unknown, ConflictMessage));
        var currentProfilesMap = current.Translation.Providers.ToDictionary(p => p.Id, StringComparer.Ordinal);
        foreach (var change in plan.TranslationChanges)
        {
            switch (change.Kind)
            {
                case TranslationProfileChangeKind.Add:
                    if (currentProfilesMap.ContainsKey(change.TargetProfile!.Id) ||
                        current.Translation.Providers.Any(p => string.Equals(p.Id, change.TargetProfile.Id, StringComparison.OrdinalIgnoreCase)))
                    {
                        return StagingPlan.Conflict(new ErrorInfo(ErrorCode.Unknown, ConflictMessage));
                    }

                    // 凭据 staging 规则
                    var newSetIdForAdd = "cs-" + Guid.NewGuid().ToString("N");
                    string? addApiKey = null;
                    string? addSecretKey = null;
                    if (change.Credentials.Kind == CredentialEditKind.Replace && change.Credentials.Replacement != null)
                    {
                        addApiKey = change.Credentials.Replacement.ApiKey;
                        addSecretKey = change.Credentials.Replacement.SecretKey;
                    }
                    credentialsToStage.Add((newSetIdForAdd, addApiKey, addSecretKey));
                    newCredentialSetMap[new(false, change.TargetProfile.Id)] = newSetIdForAdd;
                    break;

                case TranslationProfileChangeKind.Update:
                    if (!currentProfilesMap.TryGetValue(change.BaseProfile!.Id, out var existingProfile))
                    {
                        return StagingPlan.Conflict(new ErrorInfo(ErrorCode.Unknown, ConflictMessage));
                    }

                    if (!IsProfileDeepEqual(change.BaseProfile, existingProfile))
                    {
                        return StagingPlan.Conflict(new ErrorInfo(ErrorCode.Unknown, ConflictMessage));
                    }

                    if (change.Credentials.Kind == CredentialEditKind.Keep)
                    {
                        // 沿用原有凭据引用
                        newCredentialSetMap[new(false, change.TargetProfile!.Id)] = existingProfile.CredentialSetId!;
                    }
                    else if (change.Credentials.Kind == CredentialEditKind.Replace || change.Credentials.Kind == CredentialEditKind.Clear)
                    {
                        var newSetIdForUpdate = "cs-" + Guid.NewGuid().ToString("N");
                        string? updateApiKey = null;
                        string? updateSecretKey = null;
                        if (change.Credentials.Kind == CredentialEditKind.Replace && change.Credentials.Replacement != null)
                        {
                            updateApiKey = change.Credentials.Replacement.ApiKey;
                            updateSecretKey = change.Credentials.Replacement.SecretKey;
                        }
                        credentialsToStage.Add((newSetIdForUpdate, updateApiKey, updateSecretKey));
                        newCredentialSetMap[new(false, change.TargetProfile!.Id)] = newSetIdForUpdate;

                        if (existingProfile.CredentialSetId != null)
                        {
                            if (CredentialSetIdRegex().IsMatch(existingProfile.CredentialSetId))
                            {
                                candidateSetIds.Add(existingProfile.CredentialSetId);
                            }
                        }
                        else
                        {
                            candidateLegacyProfileIds.Add(existingProfile.Id);
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException($"未知的凭据编辑类型: {change.Credentials.Kind}");
                    }
                    break;

                case TranslationProfileChangeKind.Delete:
                    if (!currentProfilesMap.TryGetValue(change.BaseProfile!.Id, out var profileToDelete))
                    {
                        return StagingPlan.Conflict(new ErrorInfo(ErrorCode.Unknown, ConflictMessage));
                    }

                    if (!IsProfileDeepEqual(change.BaseProfile, profileToDelete))
                    {
                        return StagingPlan.Conflict(new ErrorInfo(ErrorCode.Unknown, ConflictMessage));
                    }

                    if (profileToDelete.CredentialSetId != null)
                    {
                        if (CredentialSetIdRegex().IsMatch(profileToDelete.CredentialSetId))
                        {
                            candidateSetIds.Add(profileToDelete.CredentialSetId);
                        }
                    }
                    else
                    {
                        candidateLegacyProfileIds.Add(profileToDelete.Id);
                    }
                    break;
            }
        }

        // 3. 排序变化冲突初检
        if (plan.TranslationOrder != null)
        {
            var currentOrderedIds = current.Translation.Providers
                .OrderBy(p => p.Order)
                .Select(p => p.Id)
                .ToList();

            if (!currentOrderedIds.SequenceEqual(plan.TranslationOrder.BaseIds, StringComparer.Ordinal))
            {
                return StagingPlan.Conflict(new ErrorInfo(ErrorCode.Unknown, ConflictMessage));
            }
        }

        // 4. Hotkey 补丁冲突初检
        if (plan.HotkeyChanges.Count > 0)
        {
            var candidateHotkeys = new Dictionary<Iris.Core.Actions.IrisAction, HotkeyBinding>(current.Hotkeys);
            foreach (var change in plan.HotkeyChanges)
            {
                var currentBinding = current.Hotkeys.TryGetValue(change.Action, out var cb) ? cb : null;
                _hotkeyBindingPolicy.TryNormalize(change.TargetBinding, out var normalizedTarget);
                if (normalizedTarget == null ||
                    (!AreBindingsEqual(currentBinding, change.BaseBinding) &&
                     !AreBindingsEqual(currentBinding, normalizedTarget)))
                {
                    return StagingPlan.Conflict(new ErrorInfo(ErrorCode.Unknown, ConflictMessage));
                }

                candidateHotkeys[change.Action] = normalizedTarget;
            }

            var duplicateErr = CheckCandidateHotkeyDuplicates(plan.HotkeyChanges, candidateHotkeys);
            if (duplicateErr != null)
            {
                return StagingPlan.Conflict(new ErrorInfo(ErrorCode.Unknown, ConflictMessage));
            }
        }

        var staging = new StagingPlan(credentialsToStage, newCredentialSetMap, candidateSetIds, candidateLegacyProfileIds);
        return PrepareNetworkStaging(plan, current, PrepareOcrStaging(plan, current, staging));
    }

    private AppSettings MutateSettings(
        AppSettings current,
        SettingsEditPlan plan,
        Dictionary<CredentialTarget, string?> newCredentialSetMap,
        string? networkSetId)
    {
        // 1. General 补丁应用与冲突复验
        var newGeneral = current.General;
        if (plan.General != null)
        {
            if (HasGeneralConflict(plan.General, current.General))
            {
                throw new StagingConflictException(new ErrorInfo(ErrorCode.Unknown, ConflictMessage));
            }

            var g = current.General;
            var theme = plan.General.Theme != null ? plan.General.Theme.Target : g.Theme;
            var launch = plan.General.LaunchAtStartup != null ? plan.General.LaunchAtStartup.Target : g.LaunchAtStartup;
            var notification = plan.General.ShowStartupNotification != null ? plan.General.ShowStartupNotification.Target : g.ShowStartupNotification;
            var logLevel = plan.General.LogLevel != null ? plan.General.LogLevel.Target : g.LogLevel;
            var uiLanguage = plan.General.UiLanguage != null ? plan.General.UiLanguage.Target : g.UiLanguage;

            if (theme != g.Theme || launch != g.LaunchAtStartup || notification != g.ShowStartupNotification || logLevel != g.LogLevel || uiLanguage != g.UiLanguage)
            {
                newGeneral = g with
                {
                    Theme = theme,
                    LaunchAtStartup = launch,
                    ShowStartupNotification = notification,
                    LogLevel = logLevel,
                    UiLanguage = uiLanguage
                };
            }
        }

        // 2. Profile Changes 应用与冲突复验
        var newTranslation = current.Translation;
        if (plan.TranslationChanges.Count > 0 || plan.TranslationOrder != null)
        {
            var profilesMap = current.Translation.Providers.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var addedProfiles = new List<TranslationProviderProfile>();
        var updatedProfiles = new Dictionary<string, TranslationProviderProfile>(StringComparer.Ordinal);
        var deletedProfileIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var change in plan.TranslationChanges)
        {
            switch (change.Kind)
            {
                case TranslationProfileChangeKind.Add:
                    if (profilesMap.ContainsKey(change.TargetProfile!.Id) ||
                        current.Translation.Providers.Any(p => string.Equals(p.Id, change.TargetProfile.Id, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new StagingConflictException(new ErrorInfo(ErrorCode.Unknown, ConflictMessage));
                    }

                    var setIdForAdd = newCredentialSetMap[new(false, change.TargetProfile.Id)];
                    var newProfile = change.TargetProfile with { CredentialSetId = setIdForAdd };
                    addedProfiles.Add(newProfile);
                    break;

                case TranslationProfileChangeKind.Update:
                    if (!profilesMap.TryGetValue(change.BaseProfile!.Id, out var existing))
                    {
                        throw new StagingConflictException(new ErrorInfo(ErrorCode.Unknown, ConflictMessage));
                    }

                    if (!IsProfileDeepEqual(change.BaseProfile, existing))
                    {
                        throw new StagingConflictException(new ErrorInfo(ErrorCode.Unknown, ConflictMessage));
                    }

                    string? updatedSetId = existing.CredentialSetId;
                    if (newCredentialSetMap.TryGetValue(new(false, change.TargetProfile!.Id), out var newSetId))
                    {
                        updatedSetId = newSetId;
                    }

                    var updatedProfile = change.TargetProfile with
                    {
                        CredentialSetId = updatedSetId,
                        Order = existing.Order
                    };
                    updatedProfiles[updatedProfile.Id] = updatedProfile;
                    break;

                case TranslationProfileChangeKind.Delete:
                    if (!profilesMap.TryGetValue(change.BaseProfile!.Id, out var toDelete))
                    {
                        throw new StagingConflictException(new ErrorInfo(ErrorCode.Unknown, ConflictMessage));
                    }

                    if (!IsProfileDeepEqual(change.BaseProfile, toDelete))
                    {
                        throw new StagingConflictException(new ErrorInfo(ErrorCode.Unknown, ConflictMessage));
                    }

                    deletedProfileIds.Add(toDelete.Id);
                    break;
            }
        }

        // 3. 构建新的 Profile 列表
        var resultProfiles = new List<TranslationProviderProfile>();
        foreach (var p in current.Translation.Providers)
        {
            if (deletedProfileIds.Contains(p.Id))
            {
                continue;
            }

            if (updatedProfiles.TryGetValue(p.Id, out var up))
            {
                resultProfiles.Add(up);
            }
            else
            {
                resultProfiles.Add(p);
            }
        }
        resultProfiles.AddRange(addedProfiles);

        // 4. 排序应用与冲突复验
        if (plan.TranslationOrder != null)
        {
            var currentOrderedIds = current.Translation.Providers
                .OrderBy(p => p.Order)
                .Select(p => p.Id)
                .ToList();

            if (!currentOrderedIds.SequenceEqual(plan.TranslationOrder.BaseIds, StringComparer.Ordinal))
            {
                throw new StagingConflictException(new ErrorInfo(ErrorCode.Unknown, ConflictMessage));
            }

            var targetIdToIndex = plan.TranslationOrder.TargetIds
                .Select((id, idx) => (id, idx))
                .ToDictionary(x => x.id, x => x.idx, StringComparer.Ordinal);

            resultProfiles = resultProfiles
                .Select(p => p with { Order = targetIdToIndex[p.Id] })
                .OrderBy(p => p.Order)
                .ToList();
        }

        // 5. 校验目标 PromptTemplateId 是否依然存在
        foreach (var p in resultProfiles)
        {
            if (p.PromptTemplateId != null && !TemplateExists(p.PromptTemplateId, current))
            {
                throw new StagingConflictException(new ErrorInfo(ErrorCode.Unknown, ConflictMessage));
            }
        }

            newTranslation = current.Translation with
            {
                Providers = resultProfiles
            };
        }

        // 6. Hotkeys 补丁应用与冲突复验
        var newHotkeys = current.Hotkeys;
        if (plan.HotkeyChanges.Count > 0)
        {
            var candidateHotkeys = new Dictionary<Iris.Core.Actions.IrisAction, HotkeyBinding>(current.Hotkeys);
            foreach (var change in plan.HotkeyChanges)
            {
                var currentBinding = current.Hotkeys.TryGetValue(change.Action, out var cb) ? cb : null;
                _hotkeyBindingPolicy.TryNormalize(change.TargetBinding, out var normalizedTarget);
                if (normalizedTarget == null ||
                    (!AreBindingsEqual(currentBinding, change.BaseBinding) &&
                     !AreBindingsEqual(currentBinding, normalizedTarget)))
                {
                    throw new StagingConflictException(new ErrorInfo(ErrorCode.Unknown, ConflictMessage));
                }

                candidateHotkeys[change.Action] = normalizedTarget;
            }

            var duplicateErr = CheckCandidateHotkeyDuplicates(plan.HotkeyChanges, candidateHotkeys);
            if (duplicateErr != null)
            {
                throw new StagingConflictException(new ErrorInfo(ErrorCode.Unknown, ConflictMessage));
            }

            if (!AreHotkeyDictionariesEqual(current.Hotkeys, candidateHotkeys))
            {
                newHotkeys = candidateHotkeys;
            }
        }

        var newSelection = ApplySelection(plan.Selection, current.Selection);
        var newHistory = ApplyHistory(plan.History, current.History);
        var newNetwork = ApplyNetwork(plan.Network, current.Network, networkSetId);
        var newOcr = ApplyOcr(plan, current.Ocr, newCredentialSetMap);
        var newScreenshot = ApplyScreenshot(plan.Screenshot, current.Screenshot);
        if (ReferenceEquals(newNetwork, current.Network) && ReferenceEquals(newHistory, current.History) && ReferenceEquals(newOcr, current.Ocr) && ReferenceEquals(newScreenshot, current.Screenshot) &&
            ReferenceEquals(newSelection, current.Selection) && ReferenceEquals(newGeneral, current.General) &&
            ReferenceEquals(newTranslation, current.Translation) &&
            ReferenceEquals(newHotkeys, current.Hotkeys))
        {
            return current;
        }

        return current with
        {
            General = newGeneral,
            Translation = newTranslation,
            Hotkeys = newHotkeys,
            Selection = newSelection,
            History = newHistory,
            Network = newNetwork,
            Ocr = newOcr, Screenshot = newScreenshot
        };
    }

    private ErrorInfo? CheckCandidateHotkeyDuplicates(
        IReadOnlyList<HotkeyBindingChange> changes,
        Dictionary<Iris.Core.Actions.IrisAction, HotkeyBinding> candidateHotkeys)
    {
        foreach (var change in changes)
        {
            if (!candidateHotkeys.TryGetValue(change.Action, out var targetBinding))
            {
                continue;
            }

            if (!_hotkeyBindingPolicy.TryNormalize(targetBinding, out var targetNorm) || !targetNorm.Enabled)
            {
                continue;
            }

            foreach (var (otherAction, otherBinding) in candidateHotkeys)
            {
                if (otherAction == change.Action)
                {
                    continue;
                }

                if (_hotkeyBindingPolicy.TryNormalize(otherBinding, out var otherNorm) && otherNorm.Enabled)
                {
                    if (otherNorm.Modifiers == targetNorm.Modifiers &&
                        string.Equals(otherNorm.Key, targetNorm.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_dde435b819e2"));
                    }
                }
            }
        }

        return null;
    }

    private static bool AreBindingsEqual(HotkeyBinding? a, HotkeyBinding? b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;

        bool aDisabledEmpty = !a.Enabled && string.IsNullOrWhiteSpace(a.Key);
        bool bDisabledEmpty = !b.Enabled && string.IsNullOrWhiteSpace(b.Key);

        if (aDisabledEmpty && bDisabledEmpty)
        {
            return true;
        }

        if (aDisabledEmpty || bDisabledEmpty)
        {
            return false;
        }

        return a.Enabled == b.Enabled &&
               a.Modifiers == b.Modifiers &&
               string.Equals(a.Key.Trim(), b.Key.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool AreHotkeyDictionariesEqual(
        IReadOnlyDictionary<Iris.Core.Actions.IrisAction, HotkeyBinding> a,
        Dictionary<Iris.Core.Actions.IrisAction, HotkeyBinding> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;

        foreach (var (action, bindingA) in a)
        {
            if (!b.TryGetValue(action, out var bindingB))
            {
                return false;
            }

            if (!AreBindingsEqual(bindingA, bindingB))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TemplateExists(string templateId, AppSettings settings)
    {
        if (string.Equals(BuiltInPrompts.DefaultTemplateId, templateId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return settings.Translation.CustomPrompts.Any(t => string.Equals(t.Id, templateId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasGeneralConflict(GeneralSettingsPatch patch, GeneralSettings current)
    {
        if (patch.UiLanguage is { } language && current.UiLanguage != language.Base && current.UiLanguage != language.Target) return true;
        if (patch.Theme != null)
        {
            if (current.Theme != patch.Theme.Base && current.Theme != patch.Theme.Target)
            {
                return true;
            }
        }

        if (patch.LaunchAtStartup != null)
        {
            if (current.LaunchAtStartup != patch.LaunchAtStartup.Base && current.LaunchAtStartup != patch.LaunchAtStartup.Target)
            {
                return true;
            }
        }

        if (patch.ShowStartupNotification != null)
        {
            if (current.ShowStartupNotification != patch.ShowStartupNotification.Base && current.ShowStartupNotification != patch.ShowStartupNotification.Target)
            {
                return true;
            }
        }

        if (patch.LogLevel != null)
        {
            if (!string.Equals(current.LogLevel, patch.LogLevel.Base, StringComparison.Ordinal) &&
                !string.Equals(current.LogLevel, patch.LogLevel.Target, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsProfileDeepEqual(TranslationProviderProfile? a, TranslationProviderProfile? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;

        if (!string.Equals(a.Id, b.Id, StringComparison.Ordinal)) return false;
        if (a.Enabled != b.Enabled) return false;
        if (!string.Equals(a.DisplayName, b.DisplayName, StringComparison.Ordinal)) return false;
        if (a.Kind != b.Kind) return false;
        if (!string.Equals(a.BaseUrl, b.BaseUrl, StringComparison.Ordinal)) return false;
        if (!string.Equals(a.Model, b.Model, StringComparison.Ordinal)) return false;
        if (a.TimeoutSeconds != b.TimeoutSeconds) return false;
        if (a.MaxOutputTokens != b.MaxOutputTokens) return false;
        if (!Nullable.Equals(a.Temperature, b.Temperature)) return false;
        if (!string.Equals(a.PromptTemplateId, b.PromptTemplateId, StringComparison.Ordinal)) return false;
        if (!string.Equals(a.CredentialSetId, b.CredentialSetId, StringComparison.Ordinal)) return false;

        // OpenAIOptions
        if (a.OpenAIOptions == null && b.OpenAIOptions != null) return false;
        if (a.OpenAIOptions != null && b.OpenAIOptions == null) return false;
        if (a.OpenAIOptions != null && b.OpenAIOptions != null)
        {
            var oa = a.OpenAIOptions;
            var ob = b.OpenAIOptions;
            if (oa.Stream != ob.Stream) return false;
            if (oa.IncludeStreamUsage != ob.IncludeStreamUsage) return false;
            if (oa.MaxTokensField != ob.MaxTokensField) return false;
            if (oa.StripThinkTags != ob.StripThinkTags) return false;
            if (!string.Equals(oa.ChatCompletionsPath, ob.ChatCompletionsPath, StringComparison.Ordinal)) return false;

            if (oa.ExtraHeaders == null && ob.ExtraHeaders != null) return false;
            if (oa.ExtraHeaders != null && ob.ExtraHeaders == null) return false;
            if (oa.ExtraHeaders != null && ob.ExtraHeaders != null)
            {
                if (oa.ExtraHeaders.Count != ob.ExtraHeaders.Count) return false;
                if (!oa.ExtraHeaders.SequenceEqual(ob.ExtraHeaders)) return false;
            }
        }

        // AnthropicOptions
        if (a.AnthropicOptions == null && b.AnthropicOptions != null) return false;
        if (a.AnthropicOptions != null && b.AnthropicOptions == null) return false;
        if (a.AnthropicOptions != null && b.AnthropicOptions != null)
        {
            var aa = a.AnthropicOptions;
            var ab = b.AnthropicOptions;
            if (!string.Equals(aa.Effort, ab.Effort, StringComparison.Ordinal)) return false;
            if (aa.RefusalFallback != ab.RefusalFallback) return false;
        }

        return true;
    }

    private ErrorInfo? ValidatePlan(SettingsEditPlan plan, AppSettings current)
    {
        // 1. General 校验
        if (plan.General != null)
        {
            var g = plan.General;
            if (g.UiLanguage is { Target: not "zh-CN" and not "en-US" })
                return new ErrorInfo(ErrorCode.InvalidArgument, Iris.Core.Localization.CoreText.Get("InvalidUiLanguage"));
            if (g.Theme != null)
            {
                if (!Enum.IsDefined(g.Theme.Target) ||
                    (g.Theme.Base != default && !Enum.IsDefined(g.Theme.Base)))
                {
                    return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_c95d52323038"));
                }
            }

            if (g.LogLevel != null)
            {
                if (!ValidLogLevels.Contains(g.LogLevel.Target))
                {
                    return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_ed4a1fe3a942"));
                }
                if (g.LogLevel.Base != null && !ValidLogLevels.Contains(g.LogLevel.Base))
                {
                    return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_9195679f1083"));
                }
            }
        }

        // 2. 检查 Profile ID 唯一性与重复操作
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in plan.TranslationChanges)
        {
            var id = change.Kind == TranslationProfileChangeKind.Add ? change.TargetProfile?.Id : change.BaseProfile?.Id;
            if (string.IsNullOrWhiteSpace(id) || !seenIds.Add(id))
            {
                return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_7372225545c3"));
            }
        }

        // 3. 校验每个 Profile Change
        var hasAddOrDelete = false;
        foreach (var change in plan.TranslationChanges)
        {
            if (!Enum.IsDefined(change.Kind))
            {
                return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_2d66d07d7d5f"));
            }

            switch (change.Kind)
            {
                case TranslationProfileChangeKind.Add:
                    hasAddOrDelete = true;
                    if (change.BaseProfile != null)
                    {
                        return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_1fbf1d53df1f"));
                    }
                    if (change.TargetProfile == null)
                    {
                        return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_88bb88e055cf"));
                    }
                    if (change.TargetProfile.CredentialSetId != null)
                    {
                        return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_5d3ac474e977"));
                    }
                    if (!NewProfileIdRegex().IsMatch(change.TargetProfile.Id))
                    {
                        return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_e9d0be5babc8"));
                    }

                    var addErr = ValidateProfile(change.TargetProfile, current);
                    if (addErr != null) return addErr;
                    break;

                case TranslationProfileChangeKind.Update:
                    if (change.BaseProfile == null)
                    {
                        return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_c39d89be8a24"));
                    }
                    if (change.TargetProfile == null)
                    {
                        return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_6567ec3e5ce4"));
                    }
                    if (!string.Equals(change.BaseProfile.Id, change.TargetProfile.Id, StringComparison.Ordinal))
                    {
                        return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_6b77c983c377"));
                    }
                    if (!string.Equals(change.BaseProfile.CredentialSetId, change.TargetProfile.CredentialSetId, StringComparison.Ordinal))
                    {
                        return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_bb89e4a6d141"));
                    }

                    var updateErr = ValidateProfile(change.TargetProfile, current);
                    if (updateErr != null) return updateErr;
                    break;

                case TranslationProfileChangeKind.Delete:
                    hasAddOrDelete = true;
                    if (change.BaseProfile == null)
                    {
                        return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_8c2bba494229"));
                    }
                    if (change.TargetProfile != null)
                    {
                        return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_bdf5648ed234"));
                    }
                    if (change.Credentials.Kind != CredentialEditKind.Keep)
                    {
                        return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_fa9b5ba9833d"));
                    }
                    break;
            }

            // 凭据意图校验
            if (!Enum.IsDefined(change.Credentials.Kind))
            {
                return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_3b5f7f06db59"));
            }

            if (change.Credentials.Kind == CredentialEditKind.Replace)
            {
                if (change.Credentials.Replacement == null)
                {
                    return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_8757324638ce"));
                }
            }
            else if (change.Credentials.Kind == CredentialEditKind.Keep || change.Credentials.Kind == CredentialEditKind.Clear)
            {
                if (change.Credentials.Replacement != null)
                {
                    return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_e53237193fe8"));
                }
            }
        }

        // 4. 排序意图校验
        if (hasAddOrDelete && plan.TranslationOrder == null)
        {
            return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_bb066abe0093"));
        }

        if (plan.TranslationOrder != null)
        {
            var order = plan.TranslationOrder;
            if (order.BaseIds == null || order.TargetIds == null)
            {
                return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_31e97af4e348"));
            }

            // 检查 TargetIds 无重复
            var targetIdSet = new HashSet<string>(order.TargetIds, StringComparer.Ordinal);
            if (targetIdSet.Count != order.TargetIds.Count)
            {
                return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_fc1a8545b8a8"));
            }

            // 计算理论上的目标 ID 集合
            var expectedIds = new HashSet<string>(current.Translation.Providers.Select(p => p.Id), StringComparer.Ordinal);
            foreach (var change in plan.TranslationChanges)
            {
                if (change.Kind == TranslationProfileChangeKind.Add)
                {
                    expectedIds.Add(change.TargetProfile!.Id);
                }
                else if (change.Kind == TranslationProfileChangeKind.Delete)
                {
                    expectedIds.Remove(change.BaseProfile!.Id);
                }
            }

            if (!targetIdSet.SetEquals(expectedIds))
            {
                return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_cdfa48d9be39"));
            }
        }

        // 4. HotkeyChanges 校验与候选字典显式目标重复检测
        if (plan.HotkeyChanges.Count > 0)
        {
            // (a) 规范化目标绑定校验
            foreach (var change in plan.HotkeyChanges)
            {
                if (!_hotkeyBindingPolicy.TryNormalize(change.TargetBinding, out _))
                {
                    return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_539074888c60"));
                }
            }

            // (b) 候选字典显式启用目标重复检测
            var candidateHotkeys = new Dictionary<Iris.Core.Actions.IrisAction, HotkeyBinding>(current.Hotkeys);
            foreach (var change in plan.HotkeyChanges)
            {
                _hotkeyBindingPolicy.TryNormalize(change.TargetBinding, out var norm);
                candidateHotkeys[change.Action] = norm!;
            }

            var duplicateErr = CheckCandidateHotkeyDuplicates(plan.HotkeyChanges, candidateHotkeys);
            if (duplicateErr != null)
            {
                return duplicateErr;
            }
        }

        return ValidateOcrPlan(plan, current);
    }

    private static ErrorInfo? ValidateProfile(TranslationProviderProfile profile, AppSettings current)
    {
        if (!Enum.IsDefined(profile.Kind))
        {
            return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_88de0b75493c"));
        }

        if (string.IsNullOrWhiteSpace(profile.DisplayName))
        {
            return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_0d2c568b6d8e"));
        }

        if (profile.TimeoutSeconds < 5 || profile.TimeoutSeconds > 600)
        {
            return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_794ad749ed51"));
        }

        if (profile.MaxOutputTokens.HasValue && profile.MaxOutputTokens.Value <= 0)
        {
            return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_7591b5823ffe"));
        }

        if (profile.Temperature.HasValue)
        {
            var t = profile.Temperature.Value;
            if (double.IsNaN(t) || double.IsInfinity(t) || t < 0.0 || t > 2.0)
            {
                return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_fdef32da198a"));
            }
        }

        // Enabled 校验
        if (profile.Enabled)
        {
            if (string.IsNullOrWhiteSpace(profile.Model))
            {
                return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_e3c6bebfa1ee"));
            }

            if (profile.Kind == TranslationProviderKind.OpenAICompatible)
            {
                if (string.IsNullOrWhiteSpace(profile.BaseUrl) ||
                    !Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_3584a90d73bd"));
                }
            }
            else if (profile.Kind == TranslationProviderKind.Anthropic)
            {
                if (!string.IsNullOrWhiteSpace(profile.BaseUrl))
                {
                    if (!Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var uri) ||
                        (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    {
                        return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_47c83176d03c"));
                    }
                }
            }
        }
        else
        {
            // Disabled 校验：若填写了 BaseUrl，必须合法
            if (!string.IsNullOrWhiteSpace(profile.BaseUrl))
            {
                if (!Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_19eecf82e5b9"));
                }
            }
        }

        // OpenAIOptions 校验
        if (profile.OpenAIOptions != null)
        {
            var opt = profile.OpenAIOptions;
            if (!Enum.IsDefined(opt.MaxTokensField))
            {
                return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_b70890fbafd4"));
            }

            if (string.IsNullOrWhiteSpace(opt.ChatCompletionsPath))
            {
                return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_887484964876"));
            }

            var path = opt.ChatCompletionsPath;
            if (path.StartsWith("//", StringComparison.Ordinal) ||
                Uri.TryCreate(path, UriKind.Absolute, out _) ||
                path.Contains('?') || path.Contains('#'))
            {
                return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_c073d46e8eda"));
            }

            if (opt.ExtraHeaders != null)
            {
                foreach (var (headerName, headerValue) in opt.ExtraHeaders)
                {
                    if (SensitiveHeaderNames.Contains(headerName))
                    {
                        return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_aaca1618c7ec"));
                    }

                    if (!IsValidHttpToken(headerName))
                    {
                        return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_9daf1297c3be"));
                    }

                    if (headerValue != null && (headerValue.Contains('\r') || headerValue.Contains('\n')))
                    {
                        return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_e9971e3aa748"));
                    }
                }
            }
        }

        // AnthropicOptions 校验
        if (profile.AnthropicOptions != null)
        {
            var opt = profile.AnthropicOptions;
            if (opt.Effort != null && !ValidAnthropicEfforts.Contains(opt.Effort))
            {
                return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_2d9d1cba16ff"));
            }

            if (profile.Model != null && profile.Model.Contains("haiku", StringComparison.OrdinalIgnoreCase) && opt.Effort != null)
            {
                return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_1e0f1d55206a"));
            }
        }

        // PromptTemplateId 校验
        if (profile.PromptTemplateId != null && !TemplateExists(profile.PromptTemplateId, current))
        {
            return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_c2439708babf"));
        }

        return null;
    }

    private static ErrorInfo? ValidatePlanStructure(SettingsEditPlan plan)
    {
        if (plan.TranslationChanges == null)
        {
            return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_92be3cc3d9d9"));
        }

        foreach (var change in plan.TranslationChanges)
        {
            if (change == null)
            {
                return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_81f20e09b3ea"));
            }

            if (!Enum.IsDefined(change.Kind))
            {
                return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_487ecf7b7200"));
            }

            if (change.Credentials == null)
            {
                return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_c132242b9cc8"));
            }

            if (!Enum.IsDefined(change.Credentials.Kind))
            {
                return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_3b5f7f06db59"));
            }

            switch (change.Kind)
            {
                case TranslationProfileChangeKind.Add:
                    if (change.TargetProfile == null)
                    {
                        return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_88bb88e055cf"));
                    }
                    if (change.BaseProfile != null)
                    {
                        return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_1fbf1d53df1f"));
                    }
                    break;

                case TranslationProfileChangeKind.Update:
                    if (change.BaseProfile == null)
                    {
                        return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_c39d89be8a24"));
                    }
                    if (change.TargetProfile == null)
                    {
                        return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_6567ec3e5ce4"));
                    }
                    break;

                case TranslationProfileChangeKind.Delete:
                    if (change.BaseProfile == null)
                    {
                        return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_8c2bba494229"));
                    }
                    if (change.TargetProfile != null)
                    {
                        return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_bdf5648ed234"));
                    }
                    break;
            }
        }

        if (plan.TranslationOrder != null)
        {
            if (plan.TranslationOrder.BaseIds == null || plan.TranslationOrder.TargetIds == null)
            {
                return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_31e97af4e348"));
            }

            if (plan.TranslationOrder.BaseIds.Any(id => id == null) || plan.TranslationOrder.TargetIds.Any(id => id == null))
            {
                return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_7096f3d36409"));
            }
        }

        if (plan.HotkeyChanges == null)
        {
            return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_7177a14b6ebc"));
        }

        var seenHotkeyActions = new HashSet<Iris.Core.Actions.IrisAction>();
        foreach (var change in plan.HotkeyChanges)
        {
            if (change == null)
            {
                return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_7cc45bbeb7ba"));
            }

            if (!Enum.IsDefined(change.Action))
            {
                return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_839b70cf1003"));
            }

            if (change.TargetBinding == null)
            {
                return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_890ebd5ae96d"));
            }

            if (!seenHotkeyActions.Add(change.Action))
            {
                return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_85653ba72bf5"));
            }
        }

        return ValidateOcrPlanStructure(plan);
    }

    private static ErrorInfo? ValidateDraftStructure(TranslationProviderDraft draft)
    {
        if (draft.Profile == null)
        {
            return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_c4d61c3b7e92"));
        }

        if (string.IsNullOrWhiteSpace(draft.Profile.Id))
        {
            return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_5cee7d9c04a4"));
        }

        if (draft.Credentials == null)
        {
            return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_5c6f7f20d814"));
        }

        if (!Enum.IsDefined(draft.Credentials.Kind))
        {
            return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_3b5f7f06db59"));
        }

        if (!Enum.IsDefined(draft.Profile.Kind))
        {
            return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_2e61f01ce8da"));
        }

        return null;
    }

    private static ErrorInfo? ValidateDraft(TranslationProviderDraft draft, bool isTranslationTest, AppSettings? currentSettings)
    {
        var p = draft.Profile;

        if (string.IsNullOrWhiteSpace(p.Id))
        {
            return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_5cee7d9c04a4"));
        }

        if (draft.BaseProfile == null)
        {
            if (!NewProfileIdRegex().IsMatch(p.Id))
            {
                return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_e9d0be5babc8"));
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(draft.BaseProfile.Id))
            {
                return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_f7f84688d00e"));
            }

            if (!string.Equals(p.Id, draft.BaseProfile.Id, StringComparison.Ordinal))
            {
                return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_c867164793e4"));
            }

            if (!string.Equals(p.CredentialSetId, draft.BaseProfile.CredentialSetId, StringComparison.Ordinal))
            {
                return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_5692606726ab"));
            }
        }

        if (!Enum.IsDefined(p.Kind))
        {
            return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_88de0b75493c"));
        }

        if (string.IsNullOrWhiteSpace(p.DisplayName))
        {
            return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_0d2c568b6d8e"));
        }

        if (p.TimeoutSeconds < 5 || p.TimeoutSeconds > 600)
        {
            return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_794ad749ed51"));
        }

        // 凭据意图校验
        if (draft.Credentials.Kind == CredentialEditKind.Replace)
        {
            if (draft.Credentials.Replacement == null)
            {
                return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_8757324638ce"));
            }

            if (draft.BaseProfile == null && p.CredentialSetId != null)
            {
                return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_820b7f28048d"));
            }
        }
        else if (draft.Credentials.Kind == CredentialEditKind.Keep)
        {
            if (draft.Credentials.Replacement != null)
            {
                return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_1a8fdaa1d433"));
            }

            // 只有在 Keep 模式下才依赖既有 CredentialSetId 格式有效性
            if (p.CredentialSetId != null && !CredentialSetIdRegex().IsMatch(p.CredentialSetId))
            {
                return new ErrorInfo(ErrorCode.NotConfigured, ErrorMessages.GetDefault(ErrorCode.NotConfigured), Detail: global::Iris.Core.Localization.CoreText.Get("Text_f64a52bd3dfc"), ProviderId: p.Id);
            }

            // New draft 不得指定持久引用
            if (draft.BaseProfile == null && p.CredentialSetId != null)
            {
                return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_820b7f28048d"));
            }
        }
        else if (draft.Credentials.Kind == CredentialEditKind.Clear)
        {
            if (draft.Credentials.Replacement != null)
            {
                return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_7938de18d1ca"));
            }

            if (draft.BaseProfile == null && p.CredentialSetId != null)
            {
                return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_820b7f28048d"));
            }
        }

        // BaseUrl 校验
        if (p.Kind == TranslationProviderKind.OpenAICompatible)
        {
            if (string.IsNullOrWhiteSpace(p.BaseUrl) ||
                !Uri.TryCreate(p.BaseUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_3584a90d73bd"));
            }
        }
        else if (p.Kind == TranslationProviderKind.Anthropic)
        {
            if (!string.IsNullOrWhiteSpace(p.BaseUrl))
            {
                if (!Uri.TryCreate(p.BaseUrl, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_47c83176d03c"));
                }
            }
        }

        // Options 校验：请求头属于两类请求共用的安全约束
        if (p.Kind == TranslationProviderKind.OpenAICompatible && p.OpenAIOptions?.ExtraHeaders != null)
        {
            foreach (var (headerName, headerValue) in p.OpenAIOptions.ExtraHeaders)
            {
                if (SensitiveHeaderNames.Contains(headerName))
                {
                    return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_aaca1618c7ec"));
                }

                if (!IsValidHttpToken(headerName))
                {
                    return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_9daf1297c3be"));
                }

                if (headerValue != null && (headerValue.Contains('\r') || headerValue.Contains('\n')))
                {
                    return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_e9971e3aa748"));
                }
            }
        }

        // 仅在翻译测试时校验模型、生成参数及不参与模型发现的 Options 字段
        if (isTranslationTest)
        {
            if (p.Kind == TranslationProviderKind.OpenAICompatible && p.OpenAIOptions != null)
            {
                var opt = p.OpenAIOptions;
                if (!Enum.IsDefined(opt.MaxTokensField))
                {
                    return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_b70890fbafd4"));
                }

                if (string.IsNullOrWhiteSpace(opt.ChatCompletionsPath))
                {
                    return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_887484964876"));
                }

                var path = opt.ChatCompletionsPath;
                if (path.StartsWith("//", StringComparison.Ordinal) ||
                    Uri.TryCreate(path, UriKind.Absolute, out _) ||
                    path.Contains('?') || path.Contains('#'))
                {
                    return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_c073d46e8eda"));
                }
            }

            if (p.Kind == TranslationProviderKind.Anthropic && p.AnthropicOptions != null)
            {
                var opt = p.AnthropicOptions;
                if (opt.Effort != null && !ValidAnthropicEfforts.Contains(opt.Effort))
                {
                    return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_2d9d1cba16ff"));
                }

                if (p.Model != null && p.Model.Contains("haiku", StringComparison.OrdinalIgnoreCase) && opt.Effort != null)
                {
                    return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_1e0f1d55206a"));
                }
            }

            if (string.IsNullOrWhiteSpace(p.Model))
            {
                return new ErrorInfo(ErrorCode.NotConfigured, ErrorMessages.GetDefault(ErrorCode.NotConfigured), Detail: global::Iris.Core.Localization.CoreText.Get("Text_eb11d05aba48"), ProviderId: p.Id);
            }

            if (p.MaxOutputTokens.HasValue && p.MaxOutputTokens.Value <= 0)
            {
                return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_7591b5823ffe"));
            }

            if (p.Temperature.HasValue)
            {
                var t = p.Temperature.Value;
                if (double.IsNaN(t) || double.IsInfinity(t) || t < 0.0 || t > 2.0)
                {
                    return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_fdef32da198a"));
                }
            }

            if (p.PromptTemplateId != null)
            {
                if (currentSettings != null && !TemplateExists(p.PromptTemplateId, currentSettings))
                {
                    return new ErrorInfo(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_c2439708babf"), ProviderId: p.Id);
                }
            }
        }

        return null;
    }

    private static TranslationProviderProfile WithDefaultOptions(TranslationProviderProfile profile)
    {
        if (profile.Kind == TranslationProviderKind.OpenAICompatible)
        {
            var openAi = profile.OpenAIOptions ?? new OpenAICompatibleOptions();
            var headers = openAi.ExtraHeaders != null
                ? new Dictionary<string, string>(openAi.ExtraHeaders, StringComparer.Ordinal)
                : null;
            return profile with
            {
                OpenAIOptions = openAi with { ExtraHeaders = headers }
            };
        }
        else if (profile.Kind == TranslationProviderKind.Anthropic)
        {
            var anthropic = profile.AnthropicOptions ?? new AnthropicOptions();
            return profile with
            {
                AnthropicOptions = anthropic
            };
        }

        return profile;
    }

    private static bool IsValidHttpToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;

        foreach (var c in token)
        {
            if (c > 127 || char.IsControl(c) || char.IsWhiteSpace(c))
            {
                return false;
            }

            // RFC 7230 分隔符检查
            switch (c)
            {
                case '(': case ')': case '<': case '>': case '@':
                case ',': case ';': case ':': case '\\': case '"':
                case '/': case '[': case ']': case '?': case '=':
                case '{': case '}':
                    return false;
            }
        }

        return true;
    }

    private static bool IsNoOp(SettingsEditPlan plan)
    {
        if (plan.TranslationChanges.Count > 0) return false;
        if (plan.TranslationOrder != null) return false;
        if (plan.HotkeyChanges.Count > 0) return false;
        if (HasSelectionEdits(plan.Selection)) return false;
        if (HasHistoryEdits(plan.History)) return false;
        if (HasNetworkEdits(plan.Network)) return false;
        if (HasOcrEdits(plan) || HasScreenshotEdits(plan.Screenshot)) return false;

        if (plan.General != null)
        {
            var g = plan.General;
            if (g.Theme != null) return false;
            if (g.LaunchAtStartup != null) return false;
            if (g.ShowStartupNotification != null) return false;
            if (g.LogLevel != null) return false;
            if (g.UiLanguage != null) return false;
        }

        return true;
    }

    private static string SerializeCredentialSet(string? apiKey, string? secretKey)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("format", "iris-provider-credentials");
            writer.WriteNumber("version", 1);
            if (apiKey != null)
            {
                writer.WriteString("apiKey", apiKey);
            }
            else
            {
                writer.WriteNull("apiKey");
            }
            if (secretKey != null)
            {
                writer.WriteString("secretKey", secretKey);
            }
            else
            {
                writer.WriteNull("secretKey");
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static SettingsEditPlan ClonePlan(SettingsEditPlan plan)
    {
        var general = plan.General is { } g ? g with
        { UiLanguage = g.UiLanguage is { } language ? new(language.Base, language.Target) : null } : null!;

        var changes = new List<TranslationProfileChange>();
        if (plan.TranslationChanges != null)
        {
            foreach (var ch in plan.TranslationChanges)
            {
                if (ch == null) continue;
                var baseProf = ch.BaseProfile != null ? CloneProfile(ch.BaseProfile) : null;
                var targetProf = ch.TargetProfile != null ? WithDefaultOptions(CloneProfile(ch.TargetProfile)) : null;
                var creds = ch.Credentials != null
                    ? ch.Credentials with
                    {
                        Replacement = ch.Credentials.Replacement != null
                            ? new ProviderCredentials(ch.Credentials.Replacement.ApiKey, ch.Credentials.Replacement.SecretKey)
                            : null
                    }
                    : new ProviderCredentialEdit(CredentialEditKind.Keep);

                changes.Add(new TranslationProfileChange(ch.Kind, baseProf, targetProf, creds));
            }
        }

        TranslationOrderChange? order = null;
        if (plan.TranslationOrder != null)
        {
            order = new TranslationOrderChange(
                plan.TranslationOrder.BaseIds?.ToList() ?? [],
                plan.TranslationOrder.TargetIds?.ToList() ?? []);
        }

        var hotkeyChanges = new List<HotkeyBindingChange>();
        if (plan.HotkeyChanges != null)
        {
            foreach (var ch in plan.HotkeyChanges)
            {
                if (ch == null) continue;
                var baseBinding = ch.BaseBinding != null ? ch.BaseBinding with { } : null;
                var targetBinding = ch.TargetBinding != null ? ch.TargetBinding with { } : null!;
                hotkeyChanges.Add(new HotkeyBindingChange(ch.Action, baseBinding, targetBinding));
            }
        }

        return new SettingsEditPlan(general, changes, order)
        {
            HotkeyChanges = hotkeyChanges,
            Selection = CloneSelection(plan.Selection),
            History = plan.History is { } history ? history with { } : null,
            Network = plan.Network,
            Ocr = plan.Ocr is { } ocr ? ocr with { } : null,
            Screenshot = plan.Screenshot is { } screenshot ? screenshot with { } : null,
            OcrChanges = plan.OcrChanges.Select(CloneOcrChange).ToArray()
        };
    }

    private static TranslationProviderDraft CloneDraft(TranslationProviderDraft draft)
    {
        var prof = WithDefaultOptions(CloneProfile(draft.Profile));
        var baseProf = draft.BaseProfile != null ? CloneProfile(draft.BaseProfile) : null;
        var creds = draft.Credentials != null
            ? draft.Credentials with
            {
                Replacement = draft.Credentials.Replacement != null
                    ? new ProviderCredentials(draft.Credentials.Replacement.ApiKey, draft.Credentials.Replacement.SecretKey)
                    : null
            }
            : new ProviderCredentialEdit(CredentialEditKind.Keep);

        return new TranslationProviderDraft(prof, baseProf, creds);
    }

    private static TranslationProviderProfile CloneProfile(TranslationProviderProfile profile)
    {
        OpenAICompatibleOptions? openAi = null;
        if (profile.OpenAIOptions != null)
        {
            var headers = profile.OpenAIOptions.ExtraHeaders != null
                ? new Dictionary<string, string>(profile.OpenAIOptions.ExtraHeaders, StringComparer.Ordinal)
                : null;
            openAi = profile.OpenAIOptions with { ExtraHeaders = headers };
        }

        AnthropicOptions? anthropic = profile.AnthropicOptions;

        return profile with
        {
            OpenAIOptions = openAi,
            AnthropicOptions = anthropic
        };
    }

    private static ErrorInfo MapStorageException(Exception ex)
    {
        if (ex is IrisException irisEx)
        {
            return irisEx.ToErrorInfo();
        }

        return new ErrorInfo(ErrorCode.StorageFailure, ErrorMessages.GetDefault(ErrorCode.StorageFailure), Detail: ex.GetType().Name);
    }

    private sealed class StagingPlan(
        List<(string NewSetId, string? ApiKey, string? SecretKey)> credentialsToStage,
        Dictionary<CredentialTarget, string?> newCredentialSetMap,
        List<string> candidateSetIds,
        List<string> candidateLegacyProfileIds,
        ErrorInfo? conflictError = null)
    {
        public List<(string NewSetId, string? ApiKey, string? SecretKey)> CredentialsToStage { get; } = credentialsToStage;
        public Dictionary<CredentialTarget, string?> NewCredentialSetMap { get; } = newCredentialSetMap;
        public List<string> CandidateSetIds { get; } = candidateSetIds;
        public List<string> CandidateLegacyProfileIds { get; } = candidateLegacyProfileIds;
        public ErrorInfo? ConflictError { get; } = conflictError;

        public string? NetworkSetId { get; set; }
        public string? NetworkSetValue { get; set; }
        public List<string> NetworkCandidates { get; } = [];

        public static StagingPlan Conflict(ErrorInfo error) => new([], new(), [], [], error);
    }

    private sealed class StagingConflictException(ErrorInfo errorInfo) : Exception
    {
        public ErrorInfo ErrorInfo { get; } = errorInfo;
    }

    private sealed class SettingsApplyReceipt : ISettingsApplyReceipt
    {
        public Guid CommitId { get; }
        public Guid CoordinatorInstanceId { get; }
        public List<string> RemainingCandidateSetIds { get; }
        public List<string> RemainingCandidateLegacyProfileIds { get; }
        public bool RequiresProviders { get; init; }
        public bool RequiresNetwork { get; init; }
        public bool NetworkFailed { get; set; }
        public List<string> RemainingNetworkCandidates { get; init; } = [];
        public bool ObserversCompleted { get; set; }
        public bool IsComplete => ObserversCompleted && RemainingNetworkCandidates.Count == 0 && RemainingCandidateSetIds.Count == 0 && RemainingCandidateLegacyProfileIds.Count == 0;

        public SettingsApplyReceipt(
            Guid commitId,
            Guid coordinatorInstanceId,
            IEnumerable<string> candidateSetIds,
            IEnumerable<string> candidateLegacyProfileIds,
            bool observersCompleted = false)
        {
            CommitId = commitId;
            CoordinatorInstanceId = coordinatorInstanceId;
            RemainingCandidateSetIds = candidateSetIds.Distinct(StringComparer.Ordinal).ToList();
            RemainingCandidateLegacyProfileIds = candidateLegacyProfileIds.Distinct(StringComparer.Ordinal).ToList();
            ObserversCompleted = observersCompleted;
        }

        public override string ToString() => $"SettingsApplyReceipt {{ CommitId = {CommitId} }}";
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Failed to rollback staged credential {CredentialSetId}: {ExceptionType}")]
    private static partial void LogRollbackFailed(ILogger logger, string credentialSetId, string exceptionType);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Failed to cleanup candidate {CandidateId}: {ExceptionType}")]
    private static partial void LogCandidateCleanupFailed(ILogger logger, string candidateId, string exceptionType);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Configuration observer {ObserverName} refresh failed: {ExceptionType}")]
    private static partial void LogObserverRefreshFailed(ILogger logger, string observerName, string exceptionType);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Failed to set secret in store: {ExceptionType}")]
    private static partial void LogSecretStoreSetFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Failed to update settings in service: {ExceptionType}")]
    private static partial void LogSettingsUpdateFailed(ILogger logger, string exceptionType);
}
