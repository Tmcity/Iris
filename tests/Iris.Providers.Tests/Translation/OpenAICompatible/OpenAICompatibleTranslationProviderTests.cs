using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Languages;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Iris.Providers.Http;
using Iris.Providers.Translation.OpenAICompatible;
using Iris.Providers.Translation.OpenAICompatible.Wire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using RichardSzalay.MockHttp;
using Shouldly;
using Xunit;

namespace Iris.Providers.Tests.Translation.OpenAICompatible;

public sealed class OpenAICompatibleTranslationProviderTests
{
    private static string LoadFixture(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        return File.ReadAllText(path);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance", Justification = "Test helper accepts interface")]
    private static OpenAICompatibleTranslationProvider CreateProvider(
        HttpMessageHandler httpHandler,
        TranslationProviderProfile? profile = null,
        ProviderCredentials? credentials = null,
        FakeTimeProvider? timeProvider = null,
        IPromptTemplateProvider? promptTemplates = null,
        IPromptRenderer? promptRenderer = null,
        ISettingsService? settings = null,
        ILoggerFactory? loggerFactory = null,
        int timeoutSeconds = 60)
    {
        profile ??= new TranslationProviderProfile
        {
            Id = "test-openai",
            DisplayName = "Test OpenAI",
            Kind = TranslationProviderKind.OpenAICompatible,
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o",
            TimeoutSeconds = timeoutSeconds,
            OpenAIOptions = new OpenAICompatibleOptions
            {
                Stream = true,
                StripThinkTags = true,
            },
        };

        credentials ??= new ProviderCredentials("sk-test-1234567890");
        timeProvider ??= new FakeTimeProvider();

        var httpClient = new HttpClient(httpHandler);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(HttpClientNames.Llm).Returns(httpClient);

        if (promptTemplates == null)
        {
            promptTemplates = Substitute.For<IPromptTemplateProvider>();
            promptTemplates.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);
        }

        if (promptRenderer == null)
        {
            promptRenderer = Substitute.For<IPromptRenderer>();
            promptRenderer.Render(Arg.Any<PromptTemplate>(), Arg.Any<ProviderTranslateRequest>())
                .Returns(call =>
                {
                    var req = call.Arg<ProviderTranslateRequest>();
                    return new RenderedPrompt("Translate text.", req.Text);
                });
        }

        if (settings == null)
        {
            settings = Substitute.For<ISettingsService>();
            settings.Current.Returns(new AppSettings());
        }

        if (loggerFactory == null)
        {
            var mockLoggerFactory = Substitute.For<ILoggerFactory>();
            mockLoggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
            loggerFactory = mockLoggerFactory;
        }

        return new OpenAICompatibleTranslationProvider(
            profile,
            credentials,
            httpClientFactory,
            promptTemplates,
            promptRenderer,
            settings,
            timeProvider,
            loggerFactory);
    }

    // ---------------- 1. 请求构造 (Request construction) ----------------

    [Fact]
    public async Task Request_Url_ConstructedCorrectly()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.Expect(HttpMethod.Post, "https://api.example.com/v1/chat/completions")
            .Respond("text/event-stream", LoadFixture("openai_stream_ok.txt"));

        var profile = new TranslationProviderProfile
        {
            Id = "p1",
            DisplayName = "P1",
            Kind = TranslationProviderKind.OpenAICompatible,
            BaseUrl = "https://api.example.com/v1/",
            Model = "gpt-4o",
            OpenAIOptions = new OpenAICompatibleOptions
            {
                ChatCompletionsPath = "/chat/completions",
                Stream = true,
            },
        };

        var provider = CreateProvider(mockHttp, profile);
        var request = new ProviderTranslateRequest("Hello", "en", "zh-CN");

        await foreach (var _ in provider.TranslateStreamAsync(request, TestContext.Current.CancellationToken))
        {
        }

        mockHttp.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task Request_AuthorizationHeader_PresentWhenKeyIsProvided()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.Expect(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
            .WithHeaders("Authorization", "Bearer sk-test-1234567890")
            .Respond("text/event-stream", LoadFixture("openai_stream_ok.txt"));

        var provider = CreateProvider(mockHttp, credentials: new ProviderCredentials("sk-test-1234567890"));
        var request = new ProviderTranslateRequest("Hello", "en", "zh-CN");

        await foreach (var _ in provider.TranslateStreamAsync(request, TestContext.Current.CancellationToken))
        {
        }

        mockHttp.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task Request_AuthorizationHeader_OmittedWhenKeyIsWhitespace()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.Expect(HttpMethod.Post, "http://localhost:11434/v1/chat/completions")
            .With(req => req.Headers.Authorization == null)
            .Respond("text/event-stream", LoadFixture("openai_stream_ok.txt"));

        var profile = new TranslationProviderProfile
        {
            Id = "ollama",
            DisplayName = "Ollama",
            Kind = TranslationProviderKind.OpenAICompatible,
            BaseUrl = "http://localhost:11434/v1",
            Model = "qwen2.5",
        };

        var provider = CreateProvider(mockHttp, profile, credentials: new ProviderCredentials("   "));
        var request = new ProviderTranslateRequest("Hello", "en", "zh-CN");

        await foreach (var _ in provider.TranslateStreamAsync(request, TestContext.Current.CancellationToken))
        {
        }

        mockHttp.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task Request_ExtraHeaders_AppliedAndAuthorizationIsIgnored()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.Expect(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
            .WithHeaders("Authorization", "Bearer sk-test-1234567890")
            .WithHeaders("X-Custom-Header", "CustomValue")
            .Respond("text/event-stream", LoadFixture("openai_stream_ok.txt"));

        var profile = new TranslationProviderProfile
        {
            Id = "p1",
            DisplayName = "P1",
            Kind = TranslationProviderKind.OpenAICompatible,
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o",
            OpenAIOptions = new OpenAICompatibleOptions
            {
                ExtraHeaders = new Dictionary<string, string>
                {
                    ["authorization"] = "Bearer malicious-key",
                    ["X-Custom-Header"] = "CustomValue",
                    ["Bad Header With Spaces"] = "Val",
                },
                Stream = true,
            },
        };

        var provider = CreateProvider(mockHttp, profile);
        var request = new ProviderTranslateRequest("Hello", "en", "zh-CN");

        await foreach (var _ in provider.TranslateStreamAsync(request, TestContext.Current.CancellationToken))
        {
        }

        mockHttp.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task Request_BodyFields_RenderedCorrectly()
    {
        string? capturedBody = null;

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.Expect(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
            .With(req =>
            {
                capturedBody = req.Content?.ReadAsStringAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
                return true;
            })
            .Respond("text/event-stream", LoadFixture("openai_stream_ok.txt"));

        var profile = new TranslationProviderProfile
        {
            Id = "p1",
            DisplayName = "P1",
            Kind = TranslationProviderKind.OpenAICompatible,
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o",
            Temperature = null,
            MaxOutputTokens = 512,
            OpenAIOptions = new OpenAICompatibleOptions
            {
                Stream = true,
                IncludeStreamUsage = true,
                MaxTokensField = MaxTokensField.MaxCompletionTokens,
            },
        };

        var promptRenderer = Substitute.For<IPromptRenderer>();
        promptRenderer.Render(Arg.Any<PromptTemplate>(), Arg.Any<ProviderTranslateRequest>())
            .Returns(new RenderedPrompt("  ", "User prompt content"));

        var provider = CreateProvider(mockHttp, profile, promptRenderer: promptRenderer);
        var request = new ProviderTranslateRequest("User prompt content", "en", "zh-CN");

        await foreach (var _ in provider.TranslateStreamAsync(request, TestContext.Current.CancellationToken))
        {
        }

        capturedBody.ShouldNotBeNull();
        using var doc = JsonDocument.Parse(capturedBody);
        var root = doc.RootElement;

        // Rendered.System is whitespace, so system message is omitted
        root.GetProperty("messages").GetArrayLength().ShouldBe(1);
        root.GetProperty("messages")[0].GetProperty("role").GetString().ShouldBe("user");
        root.GetProperty("messages")[0].GetProperty("content").GetString().ShouldBe("User prompt content");

        // Temperature is null, should be omitted
        root.TryGetProperty("temperature", out _).ShouldBeFalse();

        // MaxOutputTokens with MaxCompletionTokens
        root.GetProperty("max_completion_tokens").GetInt32().ShouldBe(512);
        root.TryGetProperty("max_tokens", out _).ShouldBeFalse();

        // Stream and stream_options
        root.GetProperty("stream").GetBoolean().ShouldBeTrue();
        root.GetProperty("stream_options").GetProperty("include_usage").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Request_BodyFields_NonStreamingOmitsStreamOptions()
    {
        string? capturedBody = null;

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.Expect(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
            .With(req =>
            {
                capturedBody = req.Content?.ReadAsStringAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/json", "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}");

        var profile = new TranslationProviderProfile
        {
            Id = "p1",
            DisplayName = "P1",
            Kind = TranslationProviderKind.OpenAICompatible,
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o",
            MaxOutputTokens = null,
            OpenAIOptions = new OpenAICompatibleOptions
            {
                Stream = false,
                IncludeStreamUsage = true,
            },
        };

        var provider = CreateProvider(mockHttp, profile);
        var request = new ProviderTranslateRequest("Hi", "en", "zh-CN");

        await foreach (var _ in provider.TranslateStreamAsync(request, TestContext.Current.CancellationToken))
        {
        }

        capturedBody.ShouldNotBeNull();
        using var doc = JsonDocument.Parse(capturedBody);
        var root = doc.RootElement;

        root.TryGetProperty("stream_options", out _).ShouldBeFalse();
        root.TryGetProperty("max_tokens", out _).ShouldBeFalse();
        root.TryGetProperty("max_completion_tokens", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Request_ContentIsByteArrayContentWithApplicationJson()
    {
        HttpContent? capturedContent = null;

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.Expect(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
            .With(req =>
            {
                capturedContent = req.Content;
                return true;
            })
            .Respond("text/event-stream", LoadFixture("openai_stream_ok.txt"));

        var provider = CreateProvider(mockHttp);
        var request = new ProviderTranslateRequest("Test", "en", "zh-CN");

        await foreach (var _ in provider.TranslateStreamAsync(request, TestContext.Current.CancellationToken))
        {
        }

        capturedContent.ShouldNotBeNull();
        capturedContent.ShouldBeAssignableTo<ByteArrayContent>();
        capturedContent.Headers.ContentType?.MediaType.ShouldBe("application/json");
    }

    [Fact]
    public async Task Request_UsesProfilePromptTemplateIdToQueryTemplateProvider()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.openai.com/v1/chat/completions")
            .Respond("text/event-stream", LoadFixture("openai_stream_ok.txt"));

        var promptTemplates = Substitute.For<IPromptTemplateProvider>();
        promptTemplates.Get("custom-template-id").Returns(BuiltInPrompts.Default);

        var profile = new TranslationProviderProfile
        {
            Id = "p1",
            DisplayName = "P1",
            Kind = TranslationProviderKind.OpenAICompatible,
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o",
            PromptTemplateId = "custom-template-id",
        };

        var provider = CreateProvider(mockHttp, profile, promptTemplates: promptTemplates);
        var request = new ProviderTranslateRequest("Test", "en", "zh-CN");

        await foreach (var _ in provider.TranslateStreamAsync(request, TestContext.Current.CancellationToken))
        {
        }

        promptTemplates.Received(1).Get("custom-template-id");
    }

    // ---------------- 2. 流式 (Streaming) ----------------

    [Fact]
    public async Task Stream_OpenAiStreamOk_ProducesExpectedThreeChunks()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.openai.com/v1/chat/completions")
            .Respond("text/event-stream", LoadFixture("openai_stream_ok.txt"));

        var provider = CreateProvider(mockHttp);
        var request = new ProviderTranslateRequest("Hello, world.", "en", "zh-CN");

        var chunks = new List<TranslationChunk>();
        await foreach (var chunk in provider.TranslateStreamAsync(request, TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        chunks.Count.ShouldBe(3);
        chunks[0].Delta.ShouldBe("你好");
        chunks[0].FinishReason.ShouldBeNull();
        chunks[1].Delta.ShouldBe("，世界");
        chunks[1].FinishReason.ShouldBeNull();
        chunks[2].Delta.ShouldBe(string.Empty);
        chunks[2].FinishReason.ShouldBe(TranslationFinishReason.Completed);
        chunks[2].Usage.ShouldBeNull();
    }

    [Fact]
    public async Task Stream_OpenAiStreamUsage_CapturesUsageInFinalChunk()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.openai.com/v1/chat/completions")
            .Respond("text/event-stream", LoadFixture("openai_stream_usage.txt"));

        var provider = CreateProvider(mockHttp);
        var request = new ProviderTranslateRequest("Hello", "en", "zh-CN");

        var chunks = new List<TranslationChunk>();
        await foreach (var chunk in provider.TranslateStreamAsync(request, TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        var chunksWithFinishReason = chunks.Where(c => c.FinishReason.HasValue).ToList();
        chunksWithFinishReason.Count.ShouldBe(1);

        var finalChunk = chunksWithFinishReason[0];
        finalChunk.FinishReason.ShouldBe(TranslationFinishReason.Completed);
        finalChunk.Usage.ShouldNotBeNull();
        finalChunk.Usage.InputTokens.ShouldBe(10);
        finalChunk.Usage.OutputTokens.ShouldBe(5);
    }

    [Fact]
    public async Task Stream_OpenAiStreamThink_StripsOrPreservesThinkTagsPerOption()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.openai.com/v1/chat/completions")
            .Respond("text/event-stream", LoadFixture("openai_stream_think.txt"));

        // StripThinkTags = true
        var providerStripped = CreateProvider(mockHttp);
        var sbStripped = new StringBuilder();
        await foreach (var chunk in providerStripped.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
        {
            sbStripped.Append(chunk.Delta);
        }
        sbStripped.ToString().ShouldBe("你好世界");

        // StripThinkTags = false
        var profilePreserve = new TranslationProviderProfile
        {
            Id = "p2",
            DisplayName = "P2",
            Kind = TranslationProviderKind.OpenAICompatible,
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o",
            OpenAIOptions = new OpenAICompatibleOptions
            {
                Stream = true,
                StripThinkTags = false,
            },
        };
        var providerPreserved = CreateProvider(mockHttp, profilePreserve);
        var sbPreserved = new StringBuilder();
        await foreach (var chunk in providerPreserved.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
        {
            sbPreserved.Append(chunk.Delta);
        }
        sbPreserved.ToString().ShouldContain("<think>");
    }

    [Fact]
    public async Task Stream_OpenAiStreamReasoning_IgnoresReasoningContent()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.openai.com/v1/chat/completions")
            .Respond("text/event-stream", LoadFixture("openai_stream_reasoning.txt"));

        var provider = CreateProvider(mockHttp);
        var sb = new StringBuilder();
        await foreach (var chunk in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
        {
            sb.Append(chunk.Delta);
        }

        var text = sb.ToString();
        text.ShouldNotContain("让我想想");
        text.ShouldNotContain("翻译为世界你好");
        text.ShouldBe("世界，你好！");
    }

    [Fact]
    public async Task Stream_OpenAiStreamLength_SetsFinishReasonToTruncatedWithoutException()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.openai.com/v1/chat/completions")
            .Respond("text/event-stream", LoadFixture("openai_stream_length.txt"));

        var provider = CreateProvider(mockHttp);
        var chunks = new List<TranslationChunk>();

        await foreach (var chunk in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        chunks.Last().FinishReason.ShouldBe(TranslationFinishReason.Truncated);
    }

    [Theory]
    [InlineData("content_filter")]
    [InlineData("sensitive")]
    public async Task Stream_FinishReasonContentFilterOrSensitive_ThrowsContentRefused(string finishReason)
    {
        var fixture = $"data: {{\"choices\":[{{\"delta\":{{\"content\":\"part\"}},\"finish_reason\":null}}]}}\n\n" +
                      $"data: {{\"choices\":[{{\"delta\":{{}},\"finish_reason\":\"{finishReason}\"}}]}}\n\n" +
                      "data: [DONE]\n\n";

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.openai.com/v1/chat/completions")
            .Respond("text/event-stream", fixture);

        var provider = CreateProvider(mockHttp);

        var ex = await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
            }
        });

        ex.Code.ShouldBe(ErrorCode.ContentRefused);
        ex.ProviderId.ShouldBe("test-openai");
    }

    [Fact]
    public async Task Stream_OpenAiStreamError_YieldsFirstDeltaThenThrowsIrisException()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.openai.com/v1/chat/completions")
            .Respond("text/event-stream", LoadFixture("openai_stream_error.txt"));

        var provider = CreateProvider(mockHttp);
        var received = new List<string>();

        var ex = await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var chunk in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
                if (!string.IsNullOrEmpty(chunk.Delta))
                {
                    received.Add(chunk.Delta);
                }
            }
        });

        received.ShouldContain("你好");
        ex.Detail.ShouldNotBeNull();
        ex.Detail.ShouldContain("Rate limit reached");
    }

    [Fact]
    public async Task Stream_EventTypeError_ThrowsIrisException()
    {
        var fixture = "event: error\ndata: {\"message\":\"Custom stream error\"}\n\n";

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.openai.com/v1/chat/completions")
            .Respond("text/event-stream", fixture);

        var provider = CreateProvider(mockHttp);

        var ex = await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
            }
        });

        ex.ShouldNotBeNull();
        ex.Detail.ShouldNotBeNull();
        ex.Detail.ShouldContain("Custom stream error");
    }

    [Fact]
    public async Task Stream_EndsWithoutDoneOrFinishReason_ThrowsInvalidResponse()
    {
        var fixture = "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"},\"finish_reason\":null}]}\n\n";

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.openai.com/v1/chat/completions")
            .Respond("text/event-stream", fixture);

        var provider = CreateProvider(mockHttp);

        var ex = await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
            }
        });

        ex.Code.ShouldBe(ErrorCode.InvalidResponse);
        ex.Detail.ShouldNotBeNull();
        ex.Detail.ShouldContain("意外结束");
    }

    [Fact]
    public async Task Stream_NoEventsAndContentTypeHtml_ThrowsInvalidResponseWithCheckBaseUrl()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.openai.com/v1/chat/completions")
            .Respond("text/html", "<html><body>Gateway 200</body></html>");

        var provider = CreateProvider(mockHttp);

        var ex = await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
            }
        });

        ex.Code.ShouldBe(ErrorCode.InvalidResponse);
        ex.Detail.ShouldNotBeNull();
        ex.Detail.ShouldContain("服务未返回事件流（Content-Type: text/html），请检查 BaseUrl");
    }

    [Fact]
    public async Task Stream_PartialUsage_PromptTokensOnly_ConstructsTokenUsage()
    {
        var fixture = "data: {\"choices\":[{\"delta\":{\"content\":\"test\"},\"finish_reason\":null}]}\n\n" +
                      "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":15}}\n\n" +
                      "data: [DONE]\n\n";

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.openai.com/v1/chat/completions")
            .Respond("text/event-stream", fixture);

        var provider = CreateProvider(mockHttp);
        var chunks = new List<TranslationChunk>();

        await foreach (var chunk in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        chunks.Count.ShouldBe(2);
        chunks[1].Usage.ShouldNotBeNull();
        chunks[1].Usage!.InputTokens.ShouldBe(15);
        chunks[1].Usage!.OutputTokens.ShouldBeNull();
    }

    [Fact]
    public async Task NonStreaming_PartialUsage_CompletionTokensOnly_ConstructsTokenUsage()
    {
        var jsonResponse = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"译文\"},\"finish_reason\":\"stop\"}],\"usage\":{\"completion_tokens\":8}}";

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.openai.com/v1/chat/completions")
            .Respond("application/json", jsonResponse);

        var provider = CreateProvider(mockHttp);
        var chunks = new List<TranslationChunk>();

        await foreach (var chunk in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        chunks.Count.ShouldBe(1);
        chunks[0].Usage.ShouldNotBeNull();
        chunks[0].Usage!.InputTokens.ShouldBeNull();
        chunks[0].Usage!.OutputTokens.ShouldBe(8);
    }

    [Fact]
    public async Task Stream_EndsWithFinishReasonWithoutDone_CompletesNormally()
    {
        var fixture = "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"},\"finish_reason\":\"stop\"}]}\n\n";

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.openai.com/v1/chat/completions")
            .Respond("text/event-stream", fixture);

        var provider = CreateProvider(mockHttp);
        var chunks = new List<TranslationChunk>();

        await foreach (var chunk in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        chunks.Count.ShouldBeGreaterThan(0);
        chunks.Last().FinishReason.ShouldBe(TranslationFinishReason.Completed);
    }

    [Fact]
    public async Task Stream_DataIsNotJson_ThrowsInvalidResponseWithSafeJsonDetail()
    {
        var fixture = "data: hello\n\n";

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.openai.com/v1/chat/completions")
            .Respond("text/event-stream", fixture);

        var provider = CreateProvider(mockHttp);

        var ex = await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
            }
        });

        ex.Code.ShouldBe(ErrorCode.InvalidResponse);
        ex.InnerException.ShouldBeNull();
        ex.Detail.ShouldStartWith("JSON 解析失败（Json");
    }

    [Theory]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    public async Task TranslateStreamAsync_WhenServerReturns3xxRedirect_ThrowsInvalidResponseAndDoesNotFollowRedirect(HttpStatusCode redirectStatus)
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.Expect(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
            .Respond(_ =>
            {
                var response = new HttpResponseMessage(redirectStatus);
                response.Headers.Location = new Uri("https://attacker.example/evil-target");
                response.Content = new StringContent("Redirected to evil target", Encoding.UTF8, "text/plain");
                return response;
            });

        mockHttp.When("https://attacker.example/*")
            .Throw(new InvalidOperationException("Redirect target should not have been requested!"));

        var provider = CreateProvider(mockHttp);

        var ex = await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(
                new ProviderTranslateRequest("hello", "en", "zh-CN"),
                TestContext.Current.CancellationToken))
            {
            }
        });

        ex.Code.ShouldBe(ErrorCode.InvalidResponse);
        ex.HttpStatus.ShouldBe((int)redirectStatus);
        ex.Retryable.ShouldBeFalse();
        ex.InnerException.ShouldBeNull();
        mockHttp.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task Stream_ContentTypeApplicationJson_HandledAsNonStreamingAndOutputsOneChunk()
    {
        var jsonResponse = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"直接返回的JSON译文\"},\"finish_reason\":\"stop\"}]}";

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.openai.com/v1/chat/completions")
            .Respond("application/json", jsonResponse);

        var provider = CreateProvider(mockHttp);
        var chunks = new List<TranslationChunk>();

        await foreach (var chunk in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        chunks.Count.ShouldBe(1);
        chunks[0].Delta.ShouldBe("直接返回的JSON译文");
        chunks[0].FinishReason.ShouldBe(TranslationFinishReason.Completed);
    }

    [Fact]
    public async Task Stream_ContentTypeEventStreamWithCharsetUtf8_ParsedAsSse()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.openai.com/v1/chat/completions")
            .Respond(req =>
            {
                var res = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(LoadFixture("openai_stream_ok.txt"), Encoding.UTF8, "text/event-stream"),
                };
                res.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/event-stream; charset=utf-8");
                return res;
            });

        var provider = CreateProvider(mockHttp);
        var chunks = new List<TranslationChunk>();

        await foreach (var chunk in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        chunks.Count.ShouldBe(3);
    }

    // ---------------- 3. 非流式 (Non-streaming) ----------------

    [Fact]
    public async Task NonStreaming_Normal_YieldsExactlyOneChunkWithFullTranslationAndUsage()
    {
        var json = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"<think>思考中</think>\\n\\n完整译文\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":6,\"total_tokens\":18}}";

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.openai.com/v1/chat/completions")
            .Respond("application/json", json);

        var profile = new TranslationProviderProfile
        {
            Id = "p1",
            DisplayName = "P1",
            Kind = TranslationProviderKind.OpenAICompatible,
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o",
            OpenAIOptions = new OpenAICompatibleOptions { Stream = false, StripThinkTags = true },
        };

        var provider = CreateProvider(mockHttp, profile);
        var chunks = new List<TranslationChunk>();

        await foreach (var chunk in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        chunks.Count.ShouldBe(1);
        chunks[0].Delta.ShouldBe("完整译文");
        chunks[0].FinishReason.ShouldBe(TranslationFinishReason.Completed);
        chunks[0].Usage.ShouldNotBeNull();
        chunks[0].Usage!.InputTokens.ShouldBe(12);
        chunks[0].Usage!.OutputTokens.ShouldBe(6);
    }

    [Fact]
    public async Task NonStreaming_Http200WithErrorObject_ThrowsIrisException()
    {
        var json = "{\"error\":{\"message\":\"invalid model name\"}}";

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.openai.com/v1/chat/completions")
            .Respond("application/json", json);

        var profile = new TranslationProviderProfile
        {
            Id = "p1",
            DisplayName = "P1",
            Kind = TranslationProviderKind.OpenAICompatible,
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o",
            OpenAIOptions = new OpenAICompatibleOptions { Stream = false },
        };

        var provider = CreateProvider(mockHttp, profile);

        var ex = await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
            }
        });

        ex.Detail.ShouldNotBeNull();
        ex.Detail.ShouldContain("invalid model name");
    }

    [Fact]
    public async Task NonStreaming_EmptyChoicesOrNullContent_ThrowsInvalidResponse()
    {
        var jsonEmptyChoices = "{\"choices\":[]}";
        var mockHttp1 = new MockHttpMessageHandler();
        mockHttp1.When("https://api.openai.com/v1/chat/completions").Respond("application/json", jsonEmptyChoices);
        var profile = new TranslationProviderProfile
        {
            Id = "p1",
            DisplayName = "P1",
            Kind = TranslationProviderKind.OpenAICompatible,
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o",
            OpenAIOptions = new OpenAICompatibleOptions { Stream = false },
        };

        var p1 = CreateProvider(mockHttp1, profile);
        var ex1 = await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var _ in p1.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken)) { }
        });
        ex1.Code.ShouldBe(ErrorCode.InvalidResponse);

        var jsonNullContent = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":null}}]}";
        var mockHttp2 = new MockHttpMessageHandler();
        mockHttp2.When("https://api.openai.com/v1/chat/completions").Respond("application/json", jsonNullContent);
        var p2 = CreateProvider(mockHttp2, profile);
        var ex2 = await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var _ in p2.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken)) { }
        });
        ex2.Code.ShouldBe(ErrorCode.InvalidResponse);
    }

    // ---------------- 4. HTTP 错误 (HTTP Errors) ----------------

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ErrorCode.AuthenticationFailed)]
    [InlineData(HttpStatusCode.NotFound, ErrorCode.EndpointNotFound)]
    [InlineData(HttpStatusCode.InternalServerError, ErrorCode.ProviderUnavailable)]
    public async Task HttpErrors_MappedCorrectly(HttpStatusCode status, ErrorCode expectedCode)
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.openai.com/v1/chat/completions")
            .Respond(status, "application/json", "{\"error\":{\"message\":\"failed\"}}");

        var provider = CreateProvider(mockHttp);

        var ex = await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
            }
        });

        ex.Code.ShouldBe(expectedCode);
        ex.HttpStatus.ShouldBe((int)status);
        ex.ProviderId.ShouldBe("test-openai");
    }

    [Fact]
    public async Task HttpError_429WithInsufficientQuota_MapsToQuotaExceeded()
    {
        var json = "{\"error\":{\"message\":\"You exceeded your current quota.\",\"code\":\"insufficient_quota\"}}";
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.openai.com/v1/chat/completions")
            .Respond((HttpStatusCode)429, "application/json", json);

        var provider = CreateProvider(mockHttp);

        var ex = await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
            }
        });

        ex.Code.ShouldBe(ErrorCode.QuotaExceeded);
        ex.Retryable.ShouldBeFalse();
    }

    [Fact]
    public async Task HttpError_401EchoesApiKey_DetailRedactsKey()
    {
        const string myApiKey = "sk-test-1234567890";
        var json = $"{{\"error\":{{\"message\":\"Incorrect API key provided: {myApiKey}\"}}}}";

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.openai.com/v1/chat/completions")
            .Respond(HttpStatusCode.Unauthorized, "application/json", json);

        var provider = CreateProvider(mockHttp, credentials: new ProviderCredentials(myApiKey));

        var ex = await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
            }
        });

        ex.Detail.ShouldNotBeNull();
        ex.Detail.ShouldNotContain(myApiKey);
        ex.Detail.ShouldContain("***");
    }

    private sealed class DelayingHandler : HttpMessageHandler
    {
        private readonly TimeSpan _delay;
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public DelayingHandler(TimeSpan delay)
        {
            _delay = delay;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            await Task.Delay(_delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    // ---------------- 5. 超时与取消 (Timeout and cancellation) ----------------

    [Fact]
    public async Task Timeout_HeadersDelayedIndefinitely_ThrowsTimeoutIrisException()
    {
        var timeProvider = new FakeTimeProvider();
        var handler = new DelayingHandler(Timeout.InfiniteTimeSpan);
        var provider = CreateProvider(handler, timeProvider: timeProvider, timeoutSeconds: 5);

        var translateTask = Task.Run(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
            }
        }, TestContext.Current.CancellationToken);

        await handler.Entered;
        timeProvider.Advance(TimeSpan.FromSeconds(6));

        var ex = await Should.ThrowAsync<IrisException>(async () => await translateTask);
        ex.Code.ShouldBe(ErrorCode.Timeout);
        ex.Retryable.ShouldBeTrue();
    }

    // ---------------- AC-4: 无进展超时两个用例 ----------------

    [Fact]
    public async Task ProgressTimeout_EventsArrivePeriodicallyUnderTimeout_CompletesSuccessfully()
    {
        var stream = new ControllableStream();
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.openai.com/v1/chat/completions")
            .Respond(req =>
            {
                var res = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(stream),
                };
                res.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
                return res;
            });

        var timeProvider = new FakeTimeProvider();
        var provider = CreateProvider(mockHttp, timeProvider: timeProvider, timeoutSeconds: 10);

        var translateTask = Task.Run(async () =>
        {
            var chunks = new List<TranslationChunk>();
            await foreach (var chunk in provider.TranslateStreamAsync(new ProviderTranslateRequest("test", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
                chunks.Add(chunk);
            }
            return chunks;
        }, TestContext.Current.CancellationToken);

        await stream.WaitForReadAsync();
        for (var i = 0; i < 5; i++)
        {
            stream.Push($"data: {{\"choices\":[{{\"delta\":{{\"content\":\"chunk{i}\"}},\"finish_reason\":null}}]}}\n\n");
            await stream.WaitForReadAsync();
            timeProvider.Advance(TimeSpan.FromSeconds(6));
        }

        stream.Push("data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n");
        await stream.WaitForReadAsync();
        stream.Push("data: [DONE]\n\n");
        stream.Complete();

        var results = await translateTask;
        results.Count.ShouldBe(6);
        for (var i = 0; i < 5; i++)
        {
            results[i].Delta.ShouldBe($"chunk{i}");
            results[i].FinishReason.ShouldBeNull();
        }
        results[5].Delta.ShouldBeEmpty();
        results[5].FinishReason.ShouldBe(TranslationFinishReason.Completed);
    }

    [Fact]
    public async Task ProgressTimeout_StreamIdleExceedsTimeout_ThrowsTimeoutIrisException()
    {
        var stream = new ControllableStream();
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.openai.com/v1/chat/completions")
            .Respond(req =>
            {
                var res = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(stream),
                };
                res.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
                return res;
            });

        var timeProvider = new FakeTimeProvider();
        var provider = CreateProvider(mockHttp, timeProvider: timeProvider, timeoutSeconds: 10);

        var translateTask = Task.Run(async () =>
        {
            var chunks = new List<TranslationChunk>();
            await foreach (var chunk in provider.TranslateStreamAsync(new ProviderTranslateRequest("test", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
                chunks.Add(chunk);
            }
            return chunks;
        }, TestContext.Current.CancellationToken);

        await stream.WaitForReadAsync();
        stream.Push("data: {\"choices\":[{\"delta\":{\"content\":\"first\"},\"finish_reason\":null}]}\n\n");
        await stream.WaitForReadAsync();

        timeProvider.Advance(TimeSpan.FromSeconds(11));

        var ex = await Should.ThrowAsync<IrisException>(async () => await translateTask);
        ex.Code.ShouldBe(ErrorCode.Timeout);
        ex.Retryable.ShouldBeTrue();
    }

    // ------------------------------------------------------------

    [Fact]
    public async Task Cancellation_CallerCancelsBeforeSendOrDuringHeaderOrStream_ThrowsOperationCanceledException()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.openai.com/v1/chat/completions")
            .Respond("text/event-stream", LoadFixture("openai_stream_ok.txt"));

        var provider = CreateProvider(mockHttp);

        // Before sending
        using var ctsBefore = new CancellationTokenSource();
        ctsBefore.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), ctsBefore.Token))
            {
            }
        });

        // Waiting for response headers
        var delayingHandler = new DelayingHandler(Timeout.InfiniteTimeSpan);
        var providerHeaders = CreateProvider(delayingHandler);
        using var ctsHeaders = new CancellationTokenSource();
        var headersTask = Task.Run(async () =>
        {
            await foreach (var _ in providerHeaders.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), ctsHeaders.Token))
            {
            }
        }, TestContext.Current.CancellationToken);

        await delayingHandler.Entered;
        ctsHeaders.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await headersTask);

        // During stream reading
        var stream = new ControllableStream();
        var mockHttp2 = new MockHttpMessageHandler();
        mockHttp2.When("https://api.openai.com/v1/chat/completions")
            .Respond(req =>
            {
                var res = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(stream),
                };
                res.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
                return res;
            });

        var provider2 = CreateProvider(mockHttp2);
        using var ctsDuring = new CancellationTokenSource();

        var readTask = Task.Run(async () =>
        {
            await foreach (var _ in provider2.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), ctsDuring.Token))
            {
            }
        }, TestContext.Current.CancellationToken);

        await stream.WaitForReadAsync();
        stream.Push("data: {\"choices\":[{\"delta\":{\"content\":\"a\"},\"finish_reason\":null}]}\n\n");
        await stream.WaitForReadAsync();
        ctsDuring.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () => await readTask);
    }

    [Fact]
    public async Task Cancellation_CallerCancellationAndTimeoutSimultaneous_ThrowsOperationCanceledException()
    {
        var timeProvider = new FakeTimeProvider();
        var handler = new DelayingHandler(Timeout.InfiniteTimeSpan);
        var provider = CreateProvider(handler, timeProvider: timeProvider, timeoutSeconds: 5);
        using var callerCts = new CancellationTokenSource();

        var task = Task.Run(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), callerCts.Token))
            {
            }
        }, TestContext.Current.CancellationToken);

        await handler.Entered;
        callerCts.Cancel();
        timeProvider.Advance(TimeSpan.FromSeconds(6));

        await Should.ThrowAsync<OperationCanceledException>(async () => await task);
    }

    [Fact]
    public async Task Exceptions_NetworkExceptions_MappedToNetworkUnavailable()
    {
        var mockHttp1 = new MockHttpMessageHandler();
        mockHttp1.When("https://api.openai.com/v1/chat/completions")
            .Throw(new HttpRequestException("Connection refused"));

        var p1 = CreateProvider(mockHttp1);
        var ex1 = await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var _ in p1.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken)) { }
        });
        ex1.Code.ShouldBe(ErrorCode.NetworkUnavailable);

        var failingStream = new FailingStream(new IOException("Stream reset"));
        var mockHttp2 = new MockHttpMessageHandler();
        mockHttp2.When("https://api.openai.com/v1/chat/completions")
            .Respond(req =>
            {
                var res = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(failingStream),
                };
                res.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
                return res;
            });

        var p2 = CreateProvider(mockHttp2);
        var ex2 = await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var _ in p2.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken)) { }
        });
        ex2.Code.ShouldBe(ErrorCode.NetworkUnavailable);
    }

    [Fact]
    public async Task Exceptions_UnmappedException_RethrownVerbatim()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.openai.com/v1/chat/completions")
            .Throw(new InvalidOperationException("Programming error in mock"));

        var provider = CreateProvider(mockHttp);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
            }
        });
    }

    // ---------------- 6. 资源 (Resource management) ----------------

    [Fact]
    public async Task Resource_ConsumerBreaksAfterFirstDelta_DisposesResponse()
    {
        var trackingContent = new TrackingHttpContent(LoadFixture("openai_stream_ok.txt"));
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.openai.com/v1/chat/completions")
            .Respond(req => new HttpResponseMessage(HttpStatusCode.OK) { Content = trackingContent });

        var provider = CreateProvider(mockHttp);

        await foreach (var chunk in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
        {
            if (!string.IsNullOrEmpty(chunk.Delta))
            {
                break;
            }
        }

        trackingContent.IsDisposed.ShouldBeTrue();
    }

    // ---------------- 7. 配置判定 (Configuration determination) ----------------

    [Theory]
    [InlineData("https://api.openai.com/v1", "gpt-4o", "sk-12345678", true)]
    [InlineData("https://api.openai.com/v1", "gpt-4o", "", false)]
    [InlineData("https://api.openai.com/v1", "gpt-4o", null, false)]
    [InlineData("http://localhost:11434/v1", "qwen", "", true)]
    [InlineData("http://192.168.1.10:11434/v1", "qwen", null, true)]
    [InlineData("ftp://example.com/v1", "gpt-4o", "sk-12345678", false)]
    [InlineData("", "gpt-4o", "sk-12345678", false)]
    [InlineData("https://api.openai.com/v1", "", "sk-12345678", false)]
    [InlineData("https://api.openai.com/v1", "   ", "sk-12345678", false)]
    public void IsConfigured_DeterminedCorrectly(string baseUrl, string model, string? apiKey, bool expected)
    {
        var mockHttp = new MockHttpMessageHandler();
        var profile = new TranslationProviderProfile
        {
            Id = "test",
            DisplayName = "Test",
            Kind = TranslationProviderKind.OpenAICompatible,
            BaseUrl = baseUrl,
            Model = model,
        };

        var provider = CreateProvider(mockHttp, profile, new ProviderCredentials(apiKey));
        provider.Descriptor.IsConfigured.ShouldBe(expected);
    }

    [Fact]
    public async Task Unconfigured_TranslateStreamAsync_ThrowsNotConfiguredWithoutHttpRequest()
    {
        var mockHttp = new MockHttpMessageHandler();
        var profile = new TranslationProviderProfile
        {
            Id = "unconfigured",
            DisplayName = "Unconfigured",
            Kind = TranslationProviderKind.OpenAICompatible,
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o",
        };

        var provider = CreateProvider(mockHttp, profile, new ProviderCredentials(null));

        var ex = await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
            }
        });

        ex.Code.ShouldBe(ErrorCode.NotConfigured);
        mockHttp.GetMatchCount(mockHttp.When("*")).ShouldBe(0);
    }

    [Fact]
    public void Descriptor_FieldsSetCorrectly()
    {
        var mockHttp = new MockHttpMessageHandler();
        var profile = new TranslationProviderProfile
        {
            Id = "my-profile",
            DisplayName = "My Provider Display",
            Kind = TranslationProviderKind.OpenAICompatible,
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o-mini",
        };

        var provider = CreateProvider(mockHttp, profile);

        provider.Descriptor.Id.ShouldBe("my-profile");
        provider.Descriptor.DisplayName.ShouldBe("My Provider Display");
        provider.Descriptor.KindName.ShouldBe("OpenAICompatible");
        provider.Descriptor.Model.ShouldBe("gpt-4o-mini");
        provider.Descriptor.IsConfigured.ShouldBeTrue();
    }

    // ---------------- 8. TestAsync ----------------

    [Fact]
    public async Task TestAsync_Success_ReturnsSampleAndLatency()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.Expect(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
            .With(req =>
            {
                var body = req.Content?.ReadAsStringAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
                return body != null && body.Contains("Hello, world.");
            })
            .Respond("text/event-stream", LoadFixture("openai_stream_ok.txt"));

        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings
        {
            Translation = new TranslationSettings
            {
                PrimaryTargetLanguage = "zh-CN",
            },
        });

        var provider = CreateProvider(mockHttp, settings: settings);
        var result = await provider.TestAsync(TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.Sample.ShouldBe("你好，世界");
        result.Error.ShouldBeNull();
        result.Latency.ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public async Task TestAsync_Http401_ReturnsSuccessFalseWithAuthenticationFailedError()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.openai.com/v1/chat/completions")
            .Respond(HttpStatusCode.Unauthorized, "application/json", "{\"error\":{\"message\":\"Invalid API Key\"}}");

        var provider = CreateProvider(mockHttp);
        var result = await provider.TestAsync(TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Sample.ShouldBeNull();
        result.Error.ShouldNotBeNull();
        result.Error.Code.ShouldBe(ErrorCode.AuthenticationFailed);
    }

    [Fact]
    public async Task TestAsync_CallerCancellation_ThrowsOperationCanceledException()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.openai.com/v1/chat/completions")
            .Respond("text/event-stream", LoadFixture("openai_stream_ok.txt"));

        var provider = CreateProvider(mockHttp);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await provider.TestAsync(cts.Token);
        });
    }

    [Fact]
    public async Task TestAsync_UnexpectedException_ReturnsSuccessFalseWithErrorCodeUnknownAndTypeOnlyDetail()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.openai.com/v1/chat/completions")
            .Throw(new InvalidOperationException("Sensitive internal crash message"));

        var provider = CreateProvider(mockHttp);
        var result = await provider.TestAsync(TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.Code.ShouldBe(ErrorCode.Unknown);
        result.Error.Detail.ShouldBe("InvalidOperationException");
        result.Error.Detail.ShouldNotBeNull();
        result.Error.Detail.ShouldNotContain("Sensitive internal crash message");
    }

    // ---------------- 9. ListModelsAsync ----------------

    [Fact]
    public async Task ListModelsAsync_ParsesDeduplicatesAndSortsModelsCaseInsensitively()
    {
        var json = "{\"data\":[{\"id\":\"b\"},{\"id\":\"A\"},{\"id\":\"b\"},{\"id\":\" \"}]}";

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.Expect(HttpMethod.Get, "https://api.openai.com/v1/models")
            .WithHeaders("Authorization", "Bearer sk-test-1234567890")
            .Respond("application/json", json);

        var provider = CreateProvider(mockHttp);
        var models = await provider.ListModelsAsync(TestContext.Current.CancellationToken);

        models.Count.ShouldBe(2);
        models[0].ShouldBe("A");
        models[1].ShouldBe("b");
    }

    [Fact]
    public async Task ListModelsAsync_DataNullOr401_ThrowsExpectedIrisException()
    {
        var mockHttpNull = new MockHttpMessageHandler();
        mockHttpNull.When("https://api.openai.com/v1/models")
            .Respond("application/json", "{\"data\":null}");

        var providerNull = CreateProvider(mockHttpNull);
        var exNull = await Should.ThrowAsync<IrisException>(async () =>
        {
            await providerNull.ListModelsAsync(TestContext.Current.CancellationToken);
        });
        exNull.Code.ShouldBe(ErrorCode.InvalidResponse);

        var mockHttp401 = new MockHttpMessageHandler();
        mockHttp401.When("https://api.openai.com/v1/models")
            .Respond(HttpStatusCode.Unauthorized, "application/json", "{\"error\":{\"message\":\"Unauthorized\"}}");

        var provider401 = CreateProvider(mockHttp401);
        var ex401 = await Should.ThrowAsync<IrisException>(async () =>
        {
            await provider401.ListModelsAsync(TestContext.Current.CancellationToken);
        });
        ex401.Code.ShouldBe(ErrorCode.AuthenticationFailed);
    }

    [Fact]
    public async Task ListModelsAsync_EmptyModel_ReturnsModels_WhileDescriptorNotConfiguredAndTestFailsWithoutRequest()
    {
        var profile = new TranslationProviderProfile
        {
            Id = "test-openai-empty-model",
            DisplayName = "Test OpenAI Empty Model",
            Kind = TranslationProviderKind.OpenAICompatible,
            BaseUrl = "https://api.openai.com/v1",
            Model = "", // Empty model
            OpenAIOptions = new OpenAICompatibleOptions
            {
                Stream = true,
                StripThinkTags = true,
            },
        };

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.Expect(HttpMethod.Get, "https://api.openai.com/v1/models")
            .WithHeaders("Authorization", "Bearer sk-test-1234567890")
            .Respond("application/json", "{\"data\":[{\"id\":\"gpt-4o\"}]}");

        var provider = CreateProvider(mockHttp, profile: profile);

        // 1. Descriptor is not configured because Model is empty
        provider.Descriptor.IsConfigured.ShouldBeFalse();

        // 2. ListModels succeeds and sends GET /models
        var models = await provider.ListModelsAsync(TestContext.Current.CancellationToken);
        models.ShouldBe(["gpt-4o"]);
        mockHttp.VerifyNoOutstandingExpectation();

        // 3. TestAsync fails without making any HTTP request (mockHttp has no matches)
        var testResult = await provider.TestAsync(TestContext.Current.CancellationToken);
        testResult.Success.ShouldBeFalse();
        testResult.Error.ShouldNotBeNull();
        testResult.Error.Code.ShouldBe(ErrorCode.NotConfigured);

        // 4. TranslateStreamAsync also throws NotConfigured
        var req = new ProviderTranslateRequest("hello", "en", "zh-Hans");
        var ex = await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(req, TestContext.Current.CancellationToken)) { }
        });
        ex.Code.ShouldBe(ErrorCode.NotConfigured);
    }

    // ---------------- 10. 隐私 (Privacy) ----------------

    [Fact]
    public async Task Privacy_SensitiveInformation_IsNotLogged()
    {
        const string apiKey = "sk-secret-token-12345678";
        const string rawInput = "Sensitive secret translation source";
        const string headerSecret = "SecretExtraHeaderValue";

        var fakeCollector = new FakeLogCollector();
        var loggerFactory = new LoggerFactory();
        loggerFactory.AddProvider(new FakeLoggerProvider(fakeCollector));

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.Expect(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
            .Respond("text/event-stream", LoadFixture("openai_stream_ok.txt"));

        mockHttp.Expect(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
            .Respond(HttpStatusCode.Unauthorized, "application/json", $"{{\"error\":{{\"message\":\"Key failed: {apiKey}\"}}}}");

        var profile = new TranslationProviderProfile
        {
            Id = "p-privacy",
            DisplayName = "Privacy Provider",
            Kind = TranslationProviderKind.OpenAICompatible,
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o",
            OpenAIOptions = new OpenAICompatibleOptions
            {
                Stream = true,
                ExtraHeaders = new Dictionary<string, string>
                {
                    ["X-Secret"] = headerSecret,
                },
            },
        };

        var provider = CreateProvider(
            mockHttp,
            profile: profile,
            credentials: new ProviderCredentials(apiKey),
            loggerFactory: loggerFactory);

        // Run success
        await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest(rawInput, "en", "zh-CN"), TestContext.Current.CancellationToken))
        {
        }

        // Run 401
        await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest(rawInput, "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
            }
        });

        var logs = fakeCollector.GetSnapshot();
        logs.ShouldNotBeEmpty();

        foreach (var record in logs)
        {
            record.Message.ShouldNotContain(apiKey);
            record.Message.ShouldNotContain(rawInput);
            record.Message.ShouldNotContain("，世界");
            record.Message.ShouldNotContain(headerSecret);

            if (record.StructuredState != null)
            {
                foreach (var kvp in record.StructuredState)
                {
                    var val = kvp.Value?.ToString();
                    val?.ShouldNotContain(apiKey);
                    val?.ShouldNotContain(rawInput);
                    val?.ShouldNotContain("，世界");
                    val?.ShouldNotContain(headerSecret);
                }
            }
        }
    }

    // ---------------- Helper classes ----------------

    private sealed class ControllableStream : Stream
    {
        private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>();
        private byte[]? _currentChunk;
        private int _currentOffset;
        private TaskCompletionSource _waitingToRead = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WaitForReadAsync()
        {
            TaskCompletionSource tcs;
            lock (_channel)
            {
                tcs = _waitingToRead;
            }
            await tcs.Task.ConfigureAwait(false);
        }

        public void Push(string sseText)
        {
            lock (_channel)
            {
                if (_waitingToRead.Task.IsCompleted)
                {
                    _waitingToRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
            _channel.Writer.TryWrite(Encoding.UTF8.GetBytes(sseText));
        }

        public void Complete()
        {
            _channel.Writer.Complete();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_currentChunk == null || _currentOffset >= _currentChunk.Length)
            {
                lock (_channel)
                {
                    _waitingToRead.TrySetResult();
                }

                if (!await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return 0;
                }
                if (_channel.Reader.TryRead(out var chunk))
                {
                    _currentChunk = chunk;
                    _currentOffset = 0;
                }
            }

            var available = _currentChunk.Length - _currentOffset;
            var toCopy = Math.Min(available, buffer.Length);
            _currentChunk.AsSpan(_currentOffset, toCopy).CopyTo(buffer.Span);
            _currentOffset += toCopy;
            return toCopy;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FailingStream : Stream
    {
        private readonly Exception _exception;

        public FailingStream(Exception exception)
        {
            _exception = exception;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw _exception;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(_exception);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class TrackingHttpContent : StringContent
    {
        public bool IsDisposed { get; private set; }

        public TrackingHttpContent(string content) : base(content, Encoding.UTF8, "text/event-stream")
        {
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            IsDisposed = true;
        }
    }

    [Fact]
    public async Task Regression_R1_2_OpenAICompatible_DeferredExecution_NotConfiguredThrownAtFirstMoveNextAsync()
    {
        var mockHttp = new MockHttpMessageHandler();
        var profile = new TranslationProviderProfile
        {
            Id = "p-unconf",
            DisplayName = "Unconfigured",
            Kind = TranslationProviderKind.OpenAICompatible,
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o",
        };

        var provider = CreateProvider(mockHttp, profile, new ProviderCredentials(null));

        // Phase 0: Synchronous parameter validation
        Should.Throw<ArgumentNullException>(() => provider.TranslateStreamAsync(null!));

        // Phase 1: Calling method must NOT throw and must NOT send HTTP
        var enumerable = provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken);
        enumerable.ShouldNotBeNull();
        mockHttp.GetMatchCount(mockHttp.When("*")).ShouldBe(0);

        // Phase 2: Getting async enumerator must NOT throw and must NOT send HTTP
        var enumerator = enumerable.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        enumerator.ShouldNotBeNull();
        mockHttp.GetMatchCount(mockHttp.When("*")).ShouldBe(0);

        // Phase 3: First MoveNextAsync throws NotConfigured with correct ProviderId
        var ex = await Should.ThrowAsync<IrisException>(async () => await enumerator.MoveNextAsync());
        ex.Code.ShouldBe(ErrorCode.NotConfigured);
        ex.ProviderId.ShouldBe(provider.Descriptor.Id);
        mockHttp.GetMatchCount(mockHttp.When("*")).ShouldBe(0);
    }

    [Fact]
    public async Task TranslateStreamAsync_WithPreparedPrompt_Streaming_BypassesTemplateAndRenderer_AndSendsExactMessages()
    {
        var mockHttp = new MockHttpMessageHandler();
        string? capturedBody = null;

        mockHttp.Expect(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
            .With(req =>
            {
                capturedBody = req.Content?.ReadAsStringAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
                return true;
            })
            .Respond("text/event-stream", LoadFixture("openai_stream_ok.txt"));

        var promptTemplates = Substitute.For<IPromptTemplateProvider>();
        promptTemplates.Get(Arg.Any<string?>()).Returns(_ => throw new InvalidOperationException("PromptTemplateProvider should not be called when PreparedPrompt is present."));

        var promptRenderer = Substitute.For<IPromptRenderer>();
        promptRenderer.Render(Arg.Any<PromptTemplate>(), Arg.Any<ProviderTranslateRequest>())
            .Returns(_ => throw new InvalidOperationException("PromptRenderer should not be called when PreparedPrompt is present."));

        var provider = CreateProvider(mockHttp, promptTemplates: promptTemplates, promptRenderer: promptRenderer);
        var preparedPrompt = new RenderedPrompt("Custom prepared system instruction", "Custom prepared user content");
        var request = new ProviderTranslateRequest("Original text", "en", "zh-CN")
        {
            PreparedPrompt = preparedPrompt,
        };

        var chunks = new List<TranslationChunk>();
        await foreach (var chunk in provider.TranslateStreamAsync(request, TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        chunks.Count.ShouldBeGreaterThan(0);
        promptTemplates.DidNotReceiveWithAnyArgs().Get(default);
        promptRenderer.DidNotReceiveWithAnyArgs().Render(default!, default!);

        capturedBody.ShouldNotBeNull();
        using var doc = JsonDocument.Parse(capturedBody);
        var messages = doc.RootElement.GetProperty("messages");
        messages.GetArrayLength().ShouldBe(2);

        messages[0].GetProperty("role").GetString().ShouldBe("system");
        messages[0].GetProperty("content").GetString().ShouldBe("Custom prepared system instruction");

        messages[1].GetProperty("role").GetString().ShouldBe("user");
        messages[1].GetProperty("content").GetString().ShouldBe("Custom prepared user content");

        mockHttp.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task TranslateStreamAsync_WithPreparedPrompt_NonStreaming_BypassesTemplateAndRenderer_AndSendsExactMessages()
    {
        var mockHttp = new MockHttpMessageHandler();
        string? capturedBody = null;

        mockHttp.Expect(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
            .With(req =>
            {
                capturedBody = req.Content?.ReadAsStringAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/json", "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"translated ok\"},\"finish_reason\":\"stop\"}]}");

        var profile = new TranslationProviderProfile
        {
            Id = "p-nonstream",
            DisplayName = "NonStream OpenAI",
            Kind = TranslationProviderKind.OpenAICompatible,
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o",
            OpenAIOptions = new OpenAICompatibleOptions
            {
                Stream = false,
            },
        };

        var promptTemplates = Substitute.For<IPromptTemplateProvider>();
        promptTemplates.Get(Arg.Any<string?>()).Returns(_ => throw new InvalidOperationException("PromptTemplateProvider should not be called when PreparedPrompt is present."));

        var promptRenderer = Substitute.For<IPromptRenderer>();
        promptRenderer.Render(Arg.Any<PromptTemplate>(), Arg.Any<ProviderTranslateRequest>())
            .Returns(_ => throw new InvalidOperationException("PromptRenderer should not be called when PreparedPrompt is present."));

        var provider = CreateProvider(mockHttp, profile: profile, promptTemplates: promptTemplates, promptRenderer: promptRenderer);
        var preparedPrompt = new RenderedPrompt("Non-stream system prompt", "Non-stream user prompt");
        var request = new ProviderTranslateRequest("Original text", "en", "zh-CN")
        {
            PreparedPrompt = preparedPrompt,
        };

        var chunks = new List<TranslationChunk>();
        await foreach (var chunk in provider.TranslateStreamAsync(request, TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        chunks.Count.ShouldBeGreaterThan(0);
        chunks.Any(c => c.Delta.Contains("translated ok")).ShouldBeTrue();
        promptTemplates.DidNotReceiveWithAnyArgs().Get(default);
        promptRenderer.DidNotReceiveWithAnyArgs().Render(default!, default!);

        capturedBody.ShouldNotBeNull();
        using var doc = JsonDocument.Parse(capturedBody);
        var messages = doc.RootElement.GetProperty("messages");
        messages.GetArrayLength().ShouldBe(2);

        messages[0].GetProperty("role").GetString().ShouldBe("system");
        messages[0].GetProperty("content").GetString().ShouldBe("Non-stream system prompt");

        messages[1].GetProperty("role").GetString().ShouldBe("user");
        messages[1].GetProperty("content").GetString().ShouldBe("Non-stream user prompt");

        mockHttp.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task TranslateStreamAsync_WithoutPreparedPrompt_CallsTemplateAndRenderer()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.Expect(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
            .Respond("text/event-stream", LoadFixture("openai_stream_ok.txt"));

        var promptTemplates = Substitute.For<IPromptTemplateProvider>();
        var template = BuiltInPrompts.Default;
        promptTemplates.Get(Arg.Any<string?>()).Returns(template);

        var promptRenderer = Substitute.For<IPromptRenderer>();
        promptRenderer.Render(Arg.Any<PromptTemplate>(), Arg.Any<ProviderTranslateRequest>())
            .Returns(new RenderedPrompt("Rendered system", "Rendered user"));

        var provider = CreateProvider(mockHttp, promptTemplates: promptTemplates, promptRenderer: promptRenderer);
        var request = new ProviderTranslateRequest("Source text", "en", "zh-CN")
        {
            PreparedPrompt = null,
        };

        await foreach (var _ in provider.TranslateStreamAsync(request, TestContext.Current.CancellationToken))
        {
        }

        promptTemplates.Received(1).Get(Arg.Any<string?>());
        promptRenderer.Received(1).Render(template, request);
        mockHttp.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task TranslateStreamAsync_StreamLaziness_DoesNotIssueHttpBeforeFirstMoveNextAsync()
    {
        var mockHttp = new MockHttpMessageHandler();
        var callCount = 0;
        mockHttp.Expect(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
            .Respond(_ =>
            {
                callCount++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(LoadFixture("openai_stream_ok.txt"), Encoding.UTF8, "text/event-stream"),
                };
            });

        var provider = CreateProvider(mockHttp);
        var request = new ProviderTranslateRequest("Text to translate", "en", "zh-CN")
        {
            PreparedPrompt = new RenderedPrompt("Sys", "User"),
        };

        // 1. Invoking TranslateStreamAsync must NOT send HTTP
        var enumerable = provider.TranslateStreamAsync(request, TestContext.Current.CancellationToken);
        enumerable.ShouldNotBeNull();
        callCount.ShouldBe(0);

        // 2. Getting async enumerator must NOT send HTTP
        await using var enumerator = enumerable.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        enumerator.ShouldNotBeNull();
        callCount.ShouldBe(0);

        // 3. First MoveNextAsync sends HTTP request
        var hasNext = await enumerator.MoveNextAsync();
        hasNext.ShouldBeTrue();
        callCount.ShouldBe(1);

        mockHttp.VerifyNoOutstandingExpectation();
    }
}


