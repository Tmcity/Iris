using System.IO;
using Iris.Core.Storage;

namespace Iris.Infrastructure.Tests.TestSupport;

public sealed class FakeAppPaths(string dataDirectory) : IAppPaths
{
    public bool IsPortable => false;
    public string DataDirectory { get; } = dataDirectory;
    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public string SecretsFile => Path.Combine(DataDirectory, "secrets.dat");
    public string DatabaseFile => Path.Combine(DataDirectory, "history.db");
    public string LogDirectory => Path.Combine(DataDirectory, "logs");
}
