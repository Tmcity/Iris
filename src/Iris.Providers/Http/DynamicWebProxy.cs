using Iris.Core.Network;

namespace Iris.Providers.Http;

// Existing Provider local-endpoint guards share the same pure classification as network capture.
internal static class DynamicWebProxy
{
    internal static bool IsLocalAddress(Uri uri) => NetworkAddressClassifier.IsLocalAddress(uri);
}
