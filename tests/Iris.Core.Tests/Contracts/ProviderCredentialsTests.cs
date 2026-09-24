using Iris.Core.Common;
using Shouldly;
using Xunit;

namespace Iris.Core.Tests.Contracts;

public class ProviderCredentialsTests
{
    [Fact]
    public void ToString_DoesNotLeakSecrets()
    {
        var creds = new ProviderCredentials("sk-abc", "sec-xyz");
        var str = creds.ToString();

        str.ShouldNotContain("sk-abc");
        str.ShouldNotContain("sec-xyz");
    }
}
