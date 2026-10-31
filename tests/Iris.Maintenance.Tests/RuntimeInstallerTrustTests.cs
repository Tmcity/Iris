using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Shouldly;

namespace Iris.Maintenance.Tests;

public sealed class RuntimeInstallerTrustTests
{
    [Theory]
    [InlineData("CN=.NET, O=Microsoft Corporation", true)]
    [InlineData("CN=Microsoft Corporation, O=Other organization", false)]
    [InlineData("CN=Microsoft Corporation", false)]
    [InlineData("CN=Component, O=Microsoft Corporation impersonation", false)]
    public void PublisherUsesExactOrganizationRatherThanDisplayName(string subject, bool expected)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        RuntimeInstallerTrust.IsMicrosoftPublisher(certificate).ShouldBe(expected);
    }
}
