using System;
using System.Collections.Generic;
using Iris.Core.Settings;

namespace Iris.Core.Translation;

/// <summary>快照中的注册项。InstanceId 为实例代次；不可记录整个对象或其配置/凭据内容。</summary>
public sealed record TranslationProviderRegistration(
    TranslationProviderProfile Profile,
    ITranslationProvider Provider,
    Guid InstanceId);

/// <summary>拥有 Provider 使用权的快照。消费者须在子任务全部结束后释放，不能自行释放借出的 Provider。
/// Dispose 幂等；释放后访问属性抛 ObjectDisposedException；旧快照不受注册表后续重建影响。</summary>
public interface ITranslationProviderSnapshot : IDisposable
{
    AppSettings Settings { get; }
    IReadOnlyList<TranslationProviderRegistration> Providers { get; }
}
