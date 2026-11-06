using System.Text.Json;
using System.Globalization;
using Windows.Media.Ocr;

if (Environment.UserName != "WDAGUtilityAccount")
    throw new InvalidOperationException("Owned T-040 acceptance Sandbox only.");
var output = @"C:\IrisT040Output";
if (!Directory.Exists(output)) throw new InvalidOperationException("Missing owned output mapping.");
var rows = new List<object>();
string? errorType = null;
string? errorHResult = null;
try
{
    foreach (var language in OcrEngine.AvailableRecognizerLanguages)
        rows.Add(new { language.LanguageTag, EngineAvailable = OcrEngine.TryCreateFromLanguage(language) != null });
}
catch (Exception error)
{
    errorType = error.GetType().FullName;
    errorHResult = error.HResult.ToString("X8", CultureInfo.InvariantCulture);
}
var record = new { Utc = DateTimeOffset.UtcNow, ProductOperationPerformed = false,
    Scope = "Read-only Windows OCR API capability using the .NET Windows SDK projection; no image processed.",
    Languages = rows, LanguageCount = rows.Count, OcrEngine.MaxImageDimension, ErrorType = errorType, ErrorHResult = errorHResult };
var path = Path.Combine(output, "ocr-dotnet-probe-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ", CultureInfo.InvariantCulture) + ".json");
await File.WriteAllTextAsync(path, JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(path);
