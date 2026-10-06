using System.Globalization;
using System.Text.Json;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Ocr;
using Iris.Core.Text;

namespace Iris.Providers.Ocr.Baidu;

internal static class BaiduResponseParser
{
    private static readonly JsonDocumentOptions Options = new() { MaxDepth = 32 };
    internal static (string Value, TimeSpan Ttl) Token(byte[] bytes, int status, string id)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(bytes, Options); }
        catch (JsonException) when (status is < 200 or > 299) { throw BaiduErrors.Http(status, id, true); }
        using (document)
        {
            if (status is < 200 or > 299)
            {
                Dictionary<string, JsonElement> failed;
                try { failed = Object(document.RootElement, id, "access_token", "expires_in", "error", "error_description", "scope"); }
                catch (BaiduException) { throw BaiduErrors.Http(status, id, true); }
                if (failed.TryGetValue("error", out var oauth) && oauth.ValueKind == JsonValueKind.String && oauth.GetString()!.Length != 0)
                    throw new BaiduException(ErrorCode.AuthenticationFailed, "OAUTH", id, status);
                throw BaiduErrors.Http(status, id, true);
            }
            var root = Object(document.RootElement, id, "access_token", "expires_in", "error", "error_description", "scope");
            if (root.TryGetValue("error", out var error) && error.ValueKind != JsonValueKind.Null && String(error, id).Length != 0)
                throw new BaiduException(ErrorCode.AuthenticationFailed, "OAUTH", id, status);
            string token = String(Required(root, "access_token", id), id);
            if (string.IsNullOrWhiteSpace(token) || token.Length > 16384 || token.Any(char.IsControl)) throw Invalid(id);
            var expiry = Required(root, "expires_in", id);
            if (expiry.ValueKind != JsonValueKind.Number) throw Invalid(id);
            string raw = expiry.GetRawText();
            if (raw.Length == 0 || !raw.All(char.IsAsciiDigit) || raw.All(c => c == '0')) throw Invalid(id);
            long seconds = long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out long value) ? Math.Min(value, 2592000) : 2592000;
            OptionalString(root, "scope", id); OptionalString(root, "error_description", id);
            return (token, TimeSpan.FromSeconds(seconds));
        }
    }

    internal static (int BusinessCode, IReadOnlyList<OcrLine>? Lines) Recognition(byte[] bytes, BaiduConfiguration configuration, Action check)
    {
        string id = configuration.Descriptor.Id;
        using var document = JsonDocument.Parse(bytes, Options);
        var root = Object(document.RootElement, id, "error_code", "error_msg", "log_id", "direction", "words_result_num", "words_result", "paragraphs_result_num", "paragraphs_result");
        int business = root.TryGetValue("error_code", out var error) ? Integer(error, id) : 0;
        if (business != 0) return (business, null);
        OptionalString(root, "error_msg", id);
        if (root.TryGetValue("direction", out var direction)) Integer(direction, id);
        if (root.TryGetValue("log_id", out var log) && (log.ValueKind != JsonValueKind.Number || !log.TryGetUInt64(out _))) throw Invalid(id);
        var words = Required(root, "words_result", id);
        if (words.ValueKind != JsonValueKind.Array || words.GetArrayLength() > 20000) throw Invalid(id);
        CheckCount(root, "words_result_num", words.GetArrayLength(), id);
        var lines = new List<OcrLine>(words.GetArrayLength());
        long textLength = 0;
        foreach (var word in words.EnumerateArray())
        {
            check();
            var fields = Object(word, id, "words", "location", "probability");
            string text = String(Required(fields, "words", id), id);
            textLength += text.Length; if (textLength > 1_000_000) throw Invalid(id);
            PixelRect? bounds = null; double? confidence = null;
            if (configuration.Positions && fields.TryGetValue("location", out var location))
            {
                var box = Object(location, id, "left", "top", "width", "height");
                int x = Integer(Required(box, "left", id), id), y = Integer(Required(box, "top", id), id);
                int w = Integer(Required(box, "width", id), id), h = Integer(Required(box, "height", id), id);
                if (x < 0 || y < 0 || w <= 0 || h <= 0 || (long)x + w > int.MaxValue || (long)y + h > int.MaxValue) throw Invalid(id);
                bounds = new(x, y, w, h);
            }
            if (fields.TryGetValue("probability", out var probability))
            {
                var values = Object(probability, id, "average", "min", "variance");
                if (values.TryGetValue("average", out var average))
                {
                    double number = Number(average, id); if (number < 0 || number > 1) throw Invalid(id); confidence = number;
                }
                if (values.TryGetValue("min", out var minimum)) Number(minimum, id);
                if (values.TryGetValue("variance", out var variance)) Number(variance, id);
            }
            lines.Add(new(text, bounds, confidence));
        }
        if (!root.TryGetValue("paragraphs_result", out var paragraphs))
        {
            CheckCount(root, "paragraphs_result_num", 0, id); return (0, lines.AsReadOnly());
        }
        if (paragraphs.ValueKind != JsonValueKind.Array) throw Invalid(id);
        CheckCount(root, "paragraphs_result_num", paragraphs.GetArrayLength(), id);
        if (!configuration.Options.Paragraph || paragraphs.GetArrayLength() == 0) return (0, lines.AsReadOnly());
        var referenced = new HashSet<int>(); var groups = new Dictionary<int, OcrLine>();
        foreach (var paragraph in paragraphs.EnumerateArray())
        {
            check();
            var fields = Object(paragraph, id, "words_result_idx");
            var indices = Required(fields, "words_result_idx", id);
            if (indices.ValueKind != JsonValueKind.Array || indices.GetArrayLength() == 0 || indices.GetArrayLength() > lines.Count) throw Invalid(id);
            var members = new List<int>();
            foreach (var index in indices.EnumerateArray())
            {
                int n = Integer(index, id);
                if (n < 0 || n >= lines.Count || !referenced.Add(n)) throw Invalid(id);
                members.Add(n);
            }
            members.Sort();
            if (members.Count == 1) { groups.Add(members[0], lines[members[0]]); continue; }
            var selected = members.Select(i => lines[i]).ToArray();
            PixelRect? bounds = null;
            if (selected.All(l => l.Bounds != null))
            {
                int x = selected.Min(l => l.Bounds!.Value.X), y = selected.Min(l => l.Bounds!.Value.Y);
                long right = selected.Max(l => (long)l.Bounds!.Value.X + l.Bounds.Value.Width), bottom = selected.Max(l => (long)l.Bounds!.Value.Y + l.Bounds.Value.Height);
                bounds = new(x, y, checked((int)(right - x)), checked((int)(bottom - y)));
            }
            double? confidence = selected.All(l => l.Confidence != null) ? selected.Average(l => l.Confidence!.Value) : null;
            groups.Add(members[0], new(SoftLineBreakMerger.Merge(selected.Select(l => l.Text).ToArray()), bounds, confidence));
        }
        var result = new List<OcrLine>();
        for (int i = 0; i < lines.Count; i++)
        {
            check();
            if (groups.TryGetValue(i, out var group)) result.Add(group);
            else if (!referenced.Contains(i)) result.Add(lines[i]);
        }
        if (result.Sum(l => (long)l.Text.Length) > 1_000_000) throw Invalid(id);
        return (0, result.AsReadOnly());
    }

    private static Dictionary<string, JsonElement> Object(JsonElement element, string id, params string[] known)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid(id);
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
            if (known.Contains(property.Name, StringComparer.OrdinalIgnoreCase) && !result.TryAdd(property.Name, property.Value)) throw Invalid(id);
        return result;
    }
    private static JsonElement Required(Dictionary<string, JsonElement> fields, string name, string id) => fields.TryGetValue(name, out var value) ? value : throw Invalid(id);
    private static string String(JsonElement value, string id) => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Invalid(id);
    private static int Integer(JsonElement value, string id) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int n) ? n : throw Invalid(id);
    private static double Number(JsonElement value, string id) => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double n) && double.IsFinite(n) ? n : throw Invalid(id);
    private static void OptionalString(Dictionary<string, JsonElement> fields, string name, string id) { if (fields.TryGetValue(name, out var value) && value.ValueKind != JsonValueKind.Null) String(value, id); }
    private static void CheckCount(Dictionary<string, JsonElement> fields, string name, int expected, string id)
    { if (fields.TryGetValue(name, out var value) && Integer(value, id) != expected) throw Invalid(id); }
    private static BaiduException Invalid(string id) => new(ErrorCode.InvalidResponse, "JSON_PROTOCOL", id);
}
