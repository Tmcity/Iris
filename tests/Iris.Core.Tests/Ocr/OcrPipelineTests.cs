using Iris.Core.Errors;
using Iris.Core.Ocr;
using Shouldly;
using static Iris.Core.Tests.Ocr.OcrPipelineTestScope;

namespace Iris.Core.Tests.Ocr;

public sealed class OcrPipelineTests
{
    [Fact]
    public Task BothUnconfigured_NeverPrepareOrRecognize() => Run(async f =>
    {
        f.Main.Configured = f.Fallback.Configured = false;
        (await Should.ThrowAsync<IrisException>(() => f.Start(typeof(IrisException)))).Code.ShouldBe(ErrorCode.NotConfigured);
        f.Prepared.ShouldBeEmpty(); f.Main.Requests.ShouldBeEmpty(); f.Fallback.Requests.ShouldBeEmpty();
    });
    [Fact]
    public Task UnexpectedExceptionDoesNotFallbackOrExposeMessage() => Run(async f =>
    {
        f.Main.Recognize = (_, _) => throw new IOException("private text");
        var error = await Should.ThrowAsync<IrisException>(() => f.Start(typeof(IrisException)));
        error.Code.ShouldBe(ErrorCode.Unknown); error.ToString().ShouldNotContain("private"); error.InnerException.ShouldBeNull();
        f.Fallback.Requests.ShouldBeEmpty();
    });
    public static IEnumerable<object[]> Errors => Enum.GetValues<ErrorCode>().SelectMany(code => Enumerable.Range(0, 3).Select(stage => new object[] { code, stage }));
    [Theory][MemberData(nameof(Errors))]
    public Task ExactErrorSet_AppliesToConstraintsPreparationAndRecognition(ErrorCode code, int stage) => Run(async f =>
    {
        bool fallback = code is ErrorCode.NotConfigured or ErrorCode.NetworkUnavailable or ErrorCode.Timeout or ErrorCode.ProviderUnavailable or ErrorCode.QuotaExceeded;
        var error = new IrisException(code, "safe", providerId: "main");
        if (stage == 0) f.Main.ConstraintError = error;
        if (stage == 1) f.Prepare = (image, limits, _) => ReferenceEquals(limits, f.Main.Limits) ? Task.FromException<Iris.Core.Imaging.ImageData>(error) : Task.FromResult(image);
        if (stage == 2) f.Main.Recognize = (_, _) => Task.FromException<OcrResult>(error);
        if (fallback) { var result = await f.Start(); result.UsedFallback.ShouldBeTrue(); result.Raw.ShouldBeSameAs(f.Fallback.Result); }
        else (await Should.ThrowAsync<IrisException>(() => f.Start(typeof(IrisException)))).ShouldBeSameAs(error);
        f.Fallback.Requests.Count.ShouldBe(fallback ? 1 : 0); f.Lease.Calls.ShouldBe(1);
    });
    [Theory][InlineData("missing")][InlineData("MAIN")][InlineData("unconfigured")]
    public Task MissingExplicitIdAndUnconfiguredMain_UseOnlyFallback(string mode) => Run(async f =>
    {
        if (mode == "unconfigured") f.Main.Configured = false;
        var result = await f.Start(id: mode == "unconfigured" ? null : mode);
        result.UsedFallback.ShouldBeTrue(); f.Main.Requests.ShouldBeEmpty(); f.Prepared.Count.ShouldBe(1);
    });
    [Theory][InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)]
    public Task SameOrMissingFallback_IsNeverRetried(int same) => Run(async f =>
    {
        var main = f.Lease.Providers[0]; var other = f.Lease.Providers[1];
        if (same == 0) f.Lease.Settings = f.Lease.Settings with { Ocr = f.Lease.Settings.Ocr with { FallbackProviderId = "absent" } };
        if (same == 1) f.Lease.Providers = [main, other with { Provider = main.Provider }];
        if (same == 2) f.Lease.Providers = [main, other with { InstanceId = main.InstanceId }];
        if (same == 3) f.Lease.Settings = f.Lease.Settings with { Ocr = f.Lease.Settings.Ocr with { FallbackProviderId = "main" } };
        f.Main.Recognize = (_, _) => throw new IrisException(ErrorCode.Timeout, "safe");
        await Should.ThrowAsync<IrisException>(() => f.Start(typeof(IrisException))); f.Fallback.Requests.ShouldBeEmpty(); f.Prepared.Count.ShouldBe(1);
    });
    [Fact]
    public Task FallbackPreparesOriginalWithDifferentConstraints_AndPreservesRaw() => Run(async f =>
    {
        f.Main.Recognize = (_, _) => throw new IrisException(ErrorCode.Timeout, "safe");
        f.Fallback.Limits = f.Fallback.Limits with { MaxLongSide = 80 };
        var converted = f.Image with { Bytes = [9] };
        f.Prepare = (_, _, _) => Task.FromResult(converted);
        var result = await f.Start(); f.Prepared.Count.ShouldBe(2);
        f.Prepared.ShouldAllBe(p => ReferenceEquals(p.Image, f.Image));
        f.Prepared[0].Constraints.ShouldBeSameAs(f.Main.Limits); f.Prepared[1].Constraints.ShouldBeSameAs(f.Fallback.Limits);
        result.Raw.ShouldBeSameAs(f.Fallback.Result); result.Text.ShouldBe("你好"); result.UsedFallback.ShouldBeTrue();
        f.Fallback.Requests.Single().LanguageHint.ShouldBe("ja");
    });
    [Theory][InlineData("")][InlineData(" \t\n")]
    public Task NoText_IsOutsideFallback(string text) => Run(async f =>
    {
        f.Main.Result = f.Main.Result with { Lines = [new(text)] };
        (await Should.ThrowAsync<IrisException>(() => f.Start(typeof(IrisException)))).Code.ShouldBe(ErrorCode.OcrNoTextFound);
        f.Fallback.Requests.ShouldBeEmpty();
    });
    [Fact]
    public Task BackupFailureKeepsItsClassificationAndId() => Run(async f =>
    {
        f.Main.Configured = false; var error = new IrisException(ErrorCode.PermissionDenied, "safe", providerId: "fallback");
        f.Fallback.Recognize = (_, _) => throw error;
        (await Should.ThrowAsync<IrisException>(() => f.Start(typeof(IrisException)))).ShouldBeSameAs(error);
    });
}
