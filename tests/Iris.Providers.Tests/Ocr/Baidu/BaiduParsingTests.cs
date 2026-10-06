using System.Text.Json;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Settings;
using Shouldly;
using static Iris.Providers.Tests.Ocr.Baidu.BaiduTestScope;

namespace Iris.Providers.Tests.Ocr.Baidu;

public sealed class BaiduParsingTests
{
    [Theory]
    [InlineData("{}")][InlineData("null")][InlineData("{\"words_result\":null}")][InlineData("{\"words_result\":false}")]
    [InlineData("{\"words_result\":[{}]}")][InlineData("{\"words_result\":[{\"words\":null}]}")]
    [InlineData("{\"words_result\":[{\"words\":4}]}")][InlineData("{\"words_result\":[],\"WORDS_RESULT\":[]}")]
    [InlineData("{\"words_result\":[],\"words_result_num\":1}")][InlineData("{\"error_code\":false,\"words_result\":[]}")]
    [InlineData("{\"words_result\":[],\"log_id\":\"bad\"}")]
    [InlineData("{\"words_result\":[{\"words\":\"x\",\"Words\":\"y\"}]}")]
    [InlineData("{\"words_result\":[{\"words\":\"x\",\"location\":{\"left\":0,\"top\":0,\"width\":1}}]}")]
    [InlineData("{\"words_result\":[{\"words\":\"x\",\"location\":{\"left\":-1,\"top\":0,\"width\":1,\"height\":1}}]}")]
    [InlineData("{\"words_result\":[{\"words\":\"x\",\"location\":{\"left\":2147483647,\"top\":0,\"width\":1,\"height\":1}}]}")]
    [InlineData("{\"words_result\":[{\"words\":\"x\",\"probability\":{\"average\":1.1}}]}")]
    [InlineData("{\"words_result\":[{\"words\":\"x\",\"probability\":{\"average\":-0.1}}]}")]
    [InlineData("{\"words_result\":[{\"words\":\"x\",\"probability\":{\"average\":\"NaN\"}}]}")]
    [InlineData("{\"words_result\":[{\"words\":\"x\"}],\"paragraphs_result\":[{\"words_result_idx\":[]}]}")]
    [InlineData("{\"words_result\":[{\"words\":\"x\"}],\"paragraphs_result\":[{\"words_result_idx\":[1]}]}")]
    [InlineData("{\"words_result\":[{\"words\":\"x\"}],\"paragraphs_result\":[{\"words_result_idx\":[0,0]}]}")]
    [InlineData("{\"words_result\":[{\"words\":\"x\"}],\"paragraphs_result\":[{\"words_result_idx\":[0]},{\"words_result_idx\":[0]}]}")]
    [InlineData("{\"words_result\":[],\"paragraphs_result\":[],\"paragraphs_result_num\":1}")]
    public Task MalformedKnownFields_AreRejected(string json) => Run(async f =>
    {
        f.Respond = (r, _) => Task.FromResult(IsToken(r) ? f.Token() : f.Json(json));
        var error = await Should.ThrowAsync<IrisException>(() => f.Recognize(expected: typeof(IrisException)));
        error.Code.ShouldBe(ErrorCode.InvalidResponse); error.InnerException.ShouldBeNull(); error.Detail!.ShouldNotContain("words_result");
    }, Profile(BaiduOcrMode.General));

    [Fact]
    public Task Paragraphs_SortIndicesKeepOmittedLinesAndMergeBoundsConfidence() => Run(async f =>
    {
        var words = new object[]
        {
            new { words = "inter-", location = new { left = 0, top = 0, width = 5, height = 5 }, probability = new { average = .2 } },
            new { words = "national", location = new { left = 10, top = 10, width = 5, height = 5 }, probability = new { average = .8 } },
            new { words = "omitted" }, new { words = "中" }, new { words = "文" }, new { words = "  " }
        };
        string json = JsonSerializer.Serialize(new { words_result = words, words_result_num = 6, paragraphs_result = new[] { new { words_result_idx = new[] { 4, 3 } }, new { words_result_idx = new[] { 1, 0 } } }, paragraphs_result_num = 2, direction = 3 });
        f.Respond = (r, _) => Task.FromResult(IsToken(r) ? f.Token() : f.Json(json));
        var result = await f.Recognize(); result.Lines.Select(l => l.Text).ShouldBe(new[] { "international", "omitted", "中文", "  " });
        result.Lines[0].Bounds.ShouldBe(new PixelRect(0, 0, 15, 15)); result.Lines[0].Confidence.ShouldBe(.5);
        result.Lines[2].Bounds.ShouldBeNull(); result.Lines[2].Confidence.ShouldBeNull(); result.DetectedLanguage.ShouldBeNull();
    }, Profile(BaiduOcrMode.General));

    [Theory]
    [InlineData("中", "文", "中文")][InlineData("inter-", "national", "international")]
    [InlineData("Hello.", "Next", "Hello.\nNext")][InlineData("- first", "- second", "- first\n- second")]
    public Task ParagraphText_UsesEstablishedSoftLineBreakRules(string first, string second, string expected) => Run(async f =>
    {
        var json = JsonSerializer.Serialize(new { words_result = new[] { new { words = first }, new { words = second } }, paragraphs_result = new[] { new { words_result_idx = new[] { 0, 1 } } } });
        f.Respond = (r, _) => Task.FromResult(IsToken(r) ? f.Token() : f.Json(json));
        (await f.Recognize()).Lines.Single().Text.ShouldBe(expected);
    });

    [Fact]
    public Task ParagraphDisabledAndBasicMode_PreserveRawLinesAndIgnorePositionExtension() => Run(async f =>
    {
        f.Respond = (r, _) => Task.FromResult(IsToken(r) ? f.Token() : f.Json("{\"WORDS_RESULT\":[{\"WORDS\":\"x\",\"location\":false},{\"words\":\"y\"}],\"paragraphs_result\":[{\"words_result_idx\":[0,1]}],\"unknown\":1,\"UNKNOWN\":2}"));
        var result = await f.Recognize(); result.Lines.Select(l => l.Text).ShouldBe(new[] { "x", "y" }); result.Lines.ShouldAllBe(l => l.Bounds == null);
    }, Profile(paragraph: false));

    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)]
    public Task ResponseBudgetsAndDepth_AreBounded(int kind) => Run(async f =>
    {
        string json = kind switch
        {
            0 => new string(' ', 256 * 1024 + 1),
            1 => "{\"words_result\":[" + string.Join(',', Enumerable.Repeat("{\"words\":\"x\"}", 20001)) + "]}",
            2 => "{\"words_result\":[{\"words\":\"" + new string('x', 1000001) + "\"}]}",
            _ => "{\"words_result\":[],\"unknown\":" + new string('[', 33) + "0" + new string(']', 33) + "}"
        };
        f.Respond = (r, _) => Task.FromResult(IsToken(r) && kind != 0 ? f.Token() : f.Json(json));
        (await Should.ThrowAsync<IrisException>(() => f.Recognize(expected: typeof(IrisException)))).Code.ShouldBe(ErrorCode.InvalidResponse);
    });
}
