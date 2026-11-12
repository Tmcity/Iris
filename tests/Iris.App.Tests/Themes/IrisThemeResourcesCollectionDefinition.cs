using Xunit;

namespace Iris.App.Tests.Themes;

// 这些测试读 Application.Current.Resources 里的 Iris 键、并离屏渲染窗口；
// 其他夹具会临时替换全局资源字典，所以本 collection 不与其他 collection 并行。
[CollectionDefinition("IrisThemeResources", DisableParallelization = true)]
public sealed class IrisThemeResourcesCollectionDefinition;
