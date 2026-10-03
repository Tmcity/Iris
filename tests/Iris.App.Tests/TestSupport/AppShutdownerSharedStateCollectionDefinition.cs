using Xunit;

namespace Iris.App.Tests.TestSupport;

/// <summary>
/// 显式测试集合：隔离对全局静态 AppBootstrapper.AppShutdowner 的写入测试类（AppBootstrapperTests 与 TranslateWindowIntegrationTests），
/// 确保其测试方法彼此串行执行，避免跨测试的退出委托保存/恢复发生竞态，同时不影响其它 WPF 测试类的正常并行。
/// </summary>
[CollectionDefinition("AppShutdownerSharedState")]
public class AppShutdownerSharedStateCollectionDefinition
{
}
