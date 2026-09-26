using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Iris.Core.Settings;
using Iris.Core.Translation;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.Core.Tests.Translation;

public sealed class PromptTemplateProviderTests
{
    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Logs { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Logs.Add((logLevel, formatter(state, exception)));
        }
    }

    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly TestLogger<PromptTemplateProvider> _logger = new();

    private PromptTemplateProvider CreateProvider(
        string defaultTemplateId = BuiltInPrompts.DefaultTemplateId,
        IReadOnlyList<PromptTemplate>? customPrompts = null)
    {
        _settings.Current.Returns(new AppSettings
        {
            Translation = new TranslationSettings
            {
                DefaultPromptTemplateId = defaultTemplateId,
                CustomPrompts = customPrompts ?? [],
            },
        });

        return new PromptTemplateProvider(_settings, _logger);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Get_NullOrWhitespaceId_UsesDefaultPromptTemplateIdFromSettings(string? inputId)
    {
        var custom = new PromptTemplate
        {
            Id = "custom.default",
            Name = "Custom Default",
            System = "Translate {{text}}",
            User = "User text",
        };

        var provider = CreateProvider(defaultTemplateId: "custom.default", customPrompts: [custom]);
        var result = provider.Get(inputId);

        result.Id.ShouldBe("custom.default");
        result.Name.ShouldBe("Custom Default");
    }

    [Theory]
    [InlineData("custom.id")]
    [InlineData("CUSTOM.ID")]
    [InlineData("Custom.Id")]
    [InlineData("  custom.id  ")]
    public void Get_CustomTemplateById_CaseInsensitive_ReturnsTemplate(string inputId)
    {
        var custom = new PromptTemplate
        {
            Id = "custom.id",
            Name = "My Prompt",
            System = "Translate {{source_language}} to {{target_language}}",
            User = "{{text}}",
        };

        var provider = CreateProvider(customPrompts: [custom]);
        var result = provider.Get(inputId);

        result.Id.ShouldBe("custom.id");
        result.Name.ShouldBe("My Prompt");
    }

    [Theory]
    [InlineData("builtin.translate.v1")]
    [InlineData("BUILTIN.TRANSLATE.V1")]
    [InlineData("BuiltIn.Translate.V1")]
    public void Get_BuiltInTemplate_CaseInsensitive_ReturnsBuiltInTemplate(string templateId)
    {
        var provider = CreateProvider();
        var result = provider.Get(templateId);

        result.ShouldBe(BuiltInPrompts.Default);
        result.Id.ShouldBe(BuiltInPrompts.DefaultTemplateId);
    }

    [Fact]
    public void Get_NotFound_FallsBackToBuiltInDefault()
    {
        var provider = CreateProvider();
        var result = provider.Get("non.existent.id");

        result.ShouldBe(BuiltInPrompts.Default);
    }

    [Fact]
    public void Get_InvalidTemplateWithoutTextPlaceholder_FallsBackToDefault_AndLogsWarningOnceWithoutTemplateBody()
    {
        const string secretBody = "SuperSecretTemplatePromptContentThatMustNeverBeLogged";
        var invalid = new PromptTemplate
        {
            Id = "invalid.no.text",
            Name = "Invalid",
            System = $"Translate without placeholder: {secretBody}",
            User = "Just instructions",
        };

        var provider = CreateProvider(customPrompts: [invalid]);

        // 第一次调用：回退默认模板，记录 1 条 Warning
        var firstResult = provider.Get("invalid.no.text");
        firstResult.ShouldBe(BuiltInPrompts.Default);

        _logger.Logs.Count.ShouldBe(1);
        _logger.Logs[0].Level.ShouldBe(LogLevel.Warning);
        _logger.Logs[0].Message.ShouldContain("invalid.no.text");
        _logger.Logs[0].Message.ShouldNotContain(secretBody);

        // 第二次调用：仍回退默认模板，但不再重复记录 Warning
        var secondResult = provider.Get("invalid.no.text");
        secondResult.ShouldBe(BuiltInPrompts.Default);

        _logger.Logs.Count.ShouldBe(1);
    }

    [Fact]
    public void Get_TemplateWithTextInSystemOnly_ConsideredValid()
    {
        var systemOnly = new PromptTemplate
        {
            Id = "system.only",
            Name = "System Only",
            System = "Translate {{text}} into Chinese",
            User = "Go!",
        };

        var provider = CreateProvider(customPrompts: [systemOnly]);
        var result = provider.Get("system.only");

        result.ShouldBe(systemOnly);
        _logger.Logs.ShouldBeEmpty();
    }

    [Fact]
    public void Get_SettingsModifiedBetweenCalls_UsesNewSettings()
    {
        var templateA = new PromptTemplate
        {
            Id = "template.a",
            Name = "A",
            System = "A System {{text}}",
            User = "A User",
        };
        var templateB = new PromptTemplate
        {
            Id = "template.b",
            Name = "B",
            System = "B System {{text}}",
            User = "B User",
        };

        _settings.Current.Returns(new AppSettings
        {
            Translation = new TranslationSettings
            {
                CustomPrompts = [templateA],
            },
        });

        var provider = new PromptTemplateProvider(_settings, _logger);
        provider.Get("template.a").Name.ShouldBe("A");
        provider.Get("template.b").ShouldBe(BuiltInPrompts.Default); // 尚不存在

        // 修改设置
        _settings.Current.Returns(new AppSettings
        {
            Translation = new TranslationSettings
            {
                CustomPrompts = [templateA, templateB],
            },
        });

        provider.Get("template.b").Name.ShouldBe("B");
    }
}
