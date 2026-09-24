using System.Text.Json;
using Iris.Providers.Http;
using Iris.Providers.Ocr.Baidu.Wire;
using Iris.Providers.Translation.OpenAICompatible.Wire;
using Shouldly;
using Xunit;

namespace Iris.Providers.Tests.Wire;

public class WireSerializationTests
{
    [Fact]
    public void ChatCompletionRequest_Serialize_OmitsNulls()
    {
        var request = new ChatCompletionRequest
        {
            Model = "gpt-4o",
            Messages = [new ChatMessage(ChatRoles.User, "Hello")],
            Stream = true
        };

        var json = JsonSerializer.Serialize(request, ProviderJson.Options);
        json.ShouldNotContain("temperature");
        json.ShouldNotContain("max_tokens");
        json.ShouldNotContain("max_completion_tokens");
        json.ShouldNotContain("stream_options");
    }

    [Fact]
    public void ChatCompletionChunk_Deserialize_ReadsDeltaAndReasoning()
    {
        var json = "{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"你好\",\"reasoning_content\":\"思考\"},\"finish_reason\":null}]}";
        var chunk = JsonSerializer.Deserialize<ChatCompletionChunk>(json, ProviderJson.Options);

        chunk.ShouldNotBeNull();
        chunk.Choices.ShouldNotBeNull();
        chunk.Choices.Count.ShouldBe(1);
        chunk.Choices[0].Delta.ShouldNotBeNull();
        chunk.Choices[0].Delta!.Content.ShouldBe("你好");
        chunk.Choices[0].Delta!.ReasoningContent.ShouldBe("思考");
    }

    [Fact]
    public void ErrorEnvelope_Code_AcceptsStringOrNumber()
    {
        var jsonStringCode = "{\"error\":{\"message\":\"quota exceeded\",\"type\":\"insufficient_quota\",\"code\":\"insufficient_quota\"}}";
        var env1 = JsonSerializer.Deserialize<ErrorEnvelope>(jsonStringCode, ProviderJson.Options);
        env1.ShouldNotBeNull();
        env1.Error.ShouldNotBeNull();
        env1.Error!.Code.HasValue.ShouldBeTrue();
        env1.Error.Code!.Value.GetString().ShouldBe("insufficient_quota");

        var jsonNumberCode = "{\"error\":{\"message\":\"rate limited\",\"type\":\"rate_limit_error\",\"code\":429}}";
        var env2 = JsonSerializer.Deserialize<ErrorEnvelope>(jsonNumberCode, ProviderJson.Options);
        env2.ShouldNotBeNull();
        env2.Error.ShouldNotBeNull();
        env2.Error!.Code.HasValue.ShouldBeTrue();
        env2.Error.Code!.Value.GetInt32().ShouldBe(429);
    }

    [Fact]
    public void BaiduOcrResponse_Deserialize_ErrorAndSuccess()
    {
        var errorJson = "{\"error_code\":110,\"error_msg\":\"Access token invalid or no longer valid\",\"log_id\":3141592653589793238}";
        var errResp = JsonSerializer.Deserialize<BaiduOcrResponse>(errorJson, ProviderJson.Options);
        errResp.ShouldNotBeNull();
        errResp.ErrorCode.ShouldBe(110);
        errResp.ErrorMsg.ShouldBe("Access token invalid or no longer valid");
        errResp.LogId.ShouldBe(3141592653589793238UL);

        var successJson = """
        {
            "log_id": 1234567890,
            "words_result_num": 2,
            "words_result": [
                {
                    "words": "第一行",
                    "location": { "left": 10, "top": 20, "width": 100, "height": 30 }
                },
                {
                    "words": "第二行"
                }
            ]
        }
        """;
        var successResp = JsonSerializer.Deserialize<BaiduOcrResponse>(successJson, ProviderJson.Options);
        successResp.ShouldNotBeNull();
        successResp.WordsResultNum.ShouldBe(2);
        successResp.WordsResult.ShouldNotBeNull();
        successResp.WordsResult.Count.ShouldBe(2);
        successResp.WordsResult[0].Words.ShouldBe("第一行");
        successResp.WordsResult[0].Location.ShouldNotBeNull();
        successResp.WordsResult[0].Location!.Left.ShouldBe(10);
        successResp.WordsResult[0].Location!.Top.ShouldBe(20);
        successResp.WordsResult[0].Location!.Width.ShouldBe(100);
        successResp.WordsResult[0].Location!.Height.ShouldBe(30);
        successResp.WordsResult[1].Words.ShouldBe("第二行");
        successResp.WordsResult[1].Location.ShouldBeNull();
    }
}
