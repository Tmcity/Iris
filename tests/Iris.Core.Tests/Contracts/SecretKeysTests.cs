using Iris.Core.Security;
using Shouldly;
using Xunit;

namespace Iris.Core.Tests.Contracts;

public class SecretKeysTests
{
    [Fact]
    public void ApiKey_And_SecretKey_FormatProperly()
    {
        SecretKeys.ApiKey("tp-1").ShouldBe("provider/tp-1/api-key");
        SecretKeys.SecretKey("tp-1").ShouldBe("provider/tp-1/secret-key");
    }
}
