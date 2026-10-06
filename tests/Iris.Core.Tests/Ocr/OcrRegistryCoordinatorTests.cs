using Iris.Core.DependencyInjection;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using static Iris.Core.Tests.Ocr.OcrRegistryTestScope;

namespace Iris.Core.Tests.Ocr;

public sealed class OcrRegistryCoordinatorTests
{
    [Fact]
    public Task ActualCoordinator_OcrBarrierFailureKeepsCommittedSettingsAndOldSecretUntilRetry() => Run(async f =>
    {
        const string oldSet = "cs-00000000000000000000000000000001";
        var translation = new TranslationProviderProfile
        { Id = "tp-existing", DisplayName = "Existing", Kind = TranslationProviderKind.OpenAICompatible, BaseUrl = "https://example.test", Model = "model", CredentialSetId = oldSet };
        f.SecretStore.Values[SecretKeys.CredentialSet(oldSet)] = "{\"format\":\"iris-provider-credentials\",\"version\":1,\"apiKey\":\"old-key\",\"secretKey\":null}";
        f.Settings.Value = Config(Profile()) with { Translation = new() { Providers = [translation] } };
        await f.Refresh(); var oldOcr = f.Providers.Created.Single(); oldOcr.FailDispose = true;
        // OCR UI is not implemented: simulate an accepted OCR settings change at the storage boundary.
        f.Settings.OnUpdate = s => s with { Ocr = s.Ocr with { Providers = [Profile(name: "new")] } };
        var templates = Substitute.For<IPromptTemplateProvider>(); templates.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);
        var coordinator = new SettingsEditCoordinator(f.Settings, f.SecretStore, Substitute.For<ITranslationProviderRegistry>(),
            [new OcrProviderConfigurationObserver(f.Registry)], templates, TimeProvider.System, NullLogger<SettingsEditCoordinator>.Instance);
        var plan = new SettingsEditPlan(new GeneralSettingsPatch(),
            [new(TranslationProfileChangeKind.Delete, translation, null, new(CredentialEditKind.Keep))],
            new TranslationOrderChange([translation.Id], []));
        var committed = await coordinator.CommitAsync(plan, default);
        committed.Status.ShouldBe(SettingsEditStatus.ApplyPending); committed.PendingApply.ShouldNotBeNull();
        f.Settings.Value.Translation.Providers.ShouldBeEmpty(); f.Settings.Value.Ocr.Providers.Single().DisplayName.ShouldBe("new");
        f.SecretStore.Values.ContainsKey(SecretKeys.CredentialSet(oldSet)).ShouldBeTrue();
        oldOcr.Disposed.ShouldBeFalse(); int sets = f.SecretStore.Sets; int updates = f.Settings.Updates;
        oldOcr.FailDispose = false;
        (await coordinator.RetryApplyAsync(committed.PendingApply, default)).IsComplete.ShouldBeTrue();
        f.Settings.Updates.ShouldBe(updates); f.SecretStore.Sets.ShouldBe(sets);
        f.SecretStore.Values.ContainsKey(SecretKeys.CredentialSet(oldSet)).ShouldBeFalse(); oldOcr.Disposed.ShouldBeTrue();
        (await f.Acquire()).Providers.Single().Profile.DisplayName.ShouldBe("new");
    });
}
