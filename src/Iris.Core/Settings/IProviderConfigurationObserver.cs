using System.Threading;
using System.Threading.Tasks;

namespace Iris.Core.Settings;

/// <summary>配置提交后的发布屏障；刷新当前设置与凭据，成功返回后旧引用的解析工作已经结束。</summary>
public interface IProviderConfigurationObserver
{
    Task RefreshAsync(CancellationToken cancellationToken = default);
}
