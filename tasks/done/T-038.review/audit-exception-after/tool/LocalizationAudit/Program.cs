using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

string root = Path.GetFullPath(args[0]);
string output = Path.GetFullPath(args[1]);
if (args.Contains("--migrate")) { Migration.Run(root); return; }
var hits = new List<object>();
var uiTrees = Directory.EnumerateFiles(Path.Combine(root, "src", "Iris.App"), "UiText*.cs")
    .Select(file => CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file)).ToArray();
var compilation = CSharpCompilation.Create("UiTextValues", uiTrees,
    [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)], new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
var named = new SortedDictionary<string, string>();
foreach (var tree in uiTrees)
{
    var model = compilation.GetSemanticModel(tree);
    foreach (var field in tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>())
    {
        if (field.Initializer == null) continue;
        var value = model.GetConstantValue(field.Initializer.Value);
        if (value.HasValue && value.Value is string text) named[field.Identifier.ValueText] = text;
    }
}
bool Han(string text) => Regex.IsMatch(text, @"\p{IsCJKUnifiedIdeographs}");
string Fingerprint(string path, string kind, string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path + "\0" + kind + "\0" + (kind == "comment" ? text.ReplaceLineEndings("\n") : text))));
string Site(string fingerprint, string context, string owner) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint + "\0" + context.ReplaceLineEndings("\n") + "\0" + owner)));
string Owner(SyntaxNode node)
{
    var member = node.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault();
    string name = member switch
    {
        MethodDeclarationSyntax method => method.Identifier.ValueText + method.ParameterList.NormalizeWhitespace().ToFullString(),
        ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText + constructor.ParameterList.NormalizeWhitespace().ToFullString(),
        PropertyDeclarationSyntax property => property.Identifier.ValueText,
        FieldDeclarationSyntax field => string.Join(",", field.Declaration.Variables.Select(v => v.Identifier.ValueText)),
        _ => member?.Kind().ToString() ?? "root"
    };
    return string.Join(".", node.Ancestors().OfType<TypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.ValueText)) + "." + name;
}
foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*", SearchOption.AllDirectories)
    .Where(p => (p.EndsWith(".cs", StringComparison.Ordinal) || p.EndsWith(".xaml", StringComparison.Ordinal)) && !p.Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj")).Order())
{
    string path = Path.GetRelativePath(root, file).Replace('\\', '/');
    string text = File.ReadAllText(file);
    if (file.EndsWith(".cs", StringComparison.Ordinal))
    {
        var tree = CSharpSyntaxTree.ParseText(text); var syntax = tree.GetRoot();
        foreach (var field in syntax.DescendantNodes().OfType<FieldDeclarationSyntax>().Where(f => f.Modifiers.Any(SyntaxKind.StaticKeyword) && f.Modifiers.Any(SyntaxKind.ReadOnlyKeyword)))
        {
            string value = field.ToString();
            if (value.Contains("UiText.", StringComparison.Ordinal) || value.Contains("AppResources.Get(", StringComparison.Ordinal) || value.Contains("CoreText.Get(", StringComparison.Ordinal))
                hits.Add(new { Path = path, Kind = "resource-cache", Text = value, Fingerprint = Fingerprint(path, "resource-cache", value) });
        }
        foreach (var node in syntax.DescendantNodes().Where(n => n is LiteralExpressionSyntax or InterpolatedStringExpressionSyntax))
        {
            string? value = node is LiteralExpressionSyntax literal && literal.Token.Value is string s ? s : null;
            string[] arguments = [];
            if (node is InterpolatedStringExpressionSyntax interpolated)
            {
                var format = new StringBuilder(); var expressions = new List<string>();
                foreach (var part in interpolated.Contents)
                {
                    if (part is InterpolatedStringTextSyntax segment) format.Append(segment.TextToken.ValueText);
                    else if (part is InterpolationSyntax hole)
                    {
                        format.Append('{').Append(expressions.Count);
                        if (hole.AlignmentClause != null) format.Append(',').Append(hole.AlignmentClause.Value.ToString());
                        if (hole.FormatClause != null) format.Append(':').Append(hole.FormatClause.FormatStringToken.ValueText);
                        format.Append('}'); expressions.Add(hole.Expression.ToString());
                    }
                }
                value = format.ToString(); arguments = expressions.ToArray();
            }
            if (value == null || !Han(value)) continue;
            // Strings inside an interpolation expression are separate literals and need their own audit.
            string kind = node is InterpolatedStringExpressionSyntax ? "interpolated" : "literal";
            var contextNode = node.Ancestors().FirstOrDefault(n => n is StatementSyntax or AttributeSyntax or VariableDeclaratorSyntax or ArrowExpressionClauseSyntax) ?? node;
            string context = contextNode.ToString();
            string canonical = contextNode.NormalizeWhitespace().ToFullString();
            string owner = Owner(node), fingerprint = Fingerprint(path, kind, value);
            hits.Add(new { Path = path, Kind = kind, Text = value, Raw = node.ToString(), Start = node.SpanStart, Length = node.Span.Length,
                Line = tree.GetLineSpan(node.Span).StartLinePosition.Line + 1, Context = context, CanonicalContext = canonical, Owner = owner,
                Arguments = arguments, Fingerprint = fingerprint, SiteFingerprint = Site(fingerprint, canonical, owner) });
        }
        foreach (var trivia in syntax.DescendantTrivia().Where(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia) || t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)))
        {
            string value = trivia.ToString(); if (!Han(value)) continue;
            hits.Add(new { Path = path, Kind = "comment", Text = value, Start = trivia.SpanStart, Length = trivia.Span.Length,
                Line = tree.GetLineSpan(trivia.Span).StartLinePosition.Line + 1, Fingerprint = Fingerprint(path, "comment", value),
                SiteFingerprint = Site(Fingerprint(path, "comment", value), "", "") });
        }
    }
    else
    {
        var doc = XDocument.Parse(text, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
        foreach (var element in doc.Descendants())
        {
            foreach (var attribute in element.Attributes().Where(a => Han(a.Value)))
                hits.Add(new { Path = path, Kind = "xaml-attribute", Text = attribute.Value, Name = attribute.Name.ToString(), Element = element.Name.LocalName,
                    Line = ((System.Xml.IXmlLineInfo)attribute).LineNumber, Fingerprint = Fingerprint(path, "xaml-attribute", attribute.Name + "=" + attribute.Value),
                    SiteFingerprint = Site(Fingerprint(path, "xaml-attribute", attribute.Name + "=" + attribute.Value), element.Name + "/" + attribute.Name, element.Attribute("{http://schemas.microsoft.com/winfx/2006/xaml}Name")?.Value ?? "") });
            foreach (var node in element.Nodes().OfType<XText>().Where(t => Han(t.Value)))
                hits.Add(new { Path = path, Kind = "xaml-text", Text = node.Value, Element = element.Name.LocalName,
                    Line = ((System.Xml.IXmlLineInfo)node).LineNumber, Fingerprint = Fingerprint(path, "xaml-text", node.Value),
                    SiteFingerprint = Site(Fingerprint(path, "xaml-text", node.Value), element.Name.ToString(), element.Attribute("{http://schemas.microsoft.com/winfx/2006/xaml}Name")?.Value ?? "") });
        }
        foreach (var comment in doc.DescendantNodes().OfType<XComment>().Where(c => Han(c.Value)))
            hits.Add(new { Path = path, Kind = "comment", Text = comment.Value, Fingerprint = Fingerprint(path, "comment", comment.Value),
                SiteFingerprint = Site(Fingerprint(path, "comment", comment.Value), "", "") });
    }
}
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(hits, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
Console.WriteLine($"Scanned C# and XAML; Chinese entries including comments: {hits.Count}");
File.WriteAllText(output + ".ui.json", JsonSerializer.Serialize(named, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
if (args.Contains("--compiled"))
{
    var assemblies = new[] { ("Iris.App", "Resources"), ("Iris.Core", "Localization") };
    int values = 0;
    foreach (var (project, resourceFolder) in assemblies)
    {
        string bin = Directory.EnumerateFiles(Path.Combine(root, "src", project, "bin", "Release"), project + ".dll", SearchOption.AllDirectories)
            .First(p => !p.Contains(Path.DirectorySeparatorChar + "uia" + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        foreach (string language in new[] { "zh-CN", "en-US" })
        {
            string assemblyPath = language == "en-US" ? Path.Combine(Path.GetDirectoryName(bin)!, "en-US", project + ".resources.dll") : bin;
            var assembly = System.Reflection.Assembly.LoadFile(assemblyPath);
            string name = project + "." + resourceFolder + ".Strings" + (language == "en-US" ? ".en-US" : "") + ".resources";
            using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("Compiled resource missing: " + name);
            using var reader = new System.Resources.ResourceReader(stream);
            var actual = reader.Cast<System.Collections.DictionaryEntry>().ToDictionary(e => (string)e.Key, e => ((string)e.Value!).ReplaceLineEndings("\n"));
            var source = XDocument.Load(Path.Combine(root, "src", project, resourceFolder, "Strings" + (language == "en-US" ? ".en-US" : "") + ".resx"))
                .Descendants("data").ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")!.Value);
            if (actual.Count != source.Count || source.Any(pair => !actual.TryGetValue(pair.Key, out var value) || value != pair.Value))
            {
                Console.WriteLine($"{project} {language}: compiled={actual.Count} source={source.Count} assembly={assemblyPath}");
                foreach (var pair in source.Where(pair => !actual.TryGetValue(pair.Key, out var value) || value != pair.Value).Take(10))
                    Console.WriteLine($"Mismatch {pair.Key}: compiled={JsonSerializer.Serialize(actual.GetValueOrDefault(pair.Key))} source={JsonSerializer.Serialize(pair.Value)}");
                throw new InvalidOperationException("Compiled/source resources differ: " + project + " " + language);
            }
            foreach (var value in actual.Values) _ = System.Text.CompositeFormat.Parse(value);
            values += actual.Count;
        }
    }
    Console.WriteLine($"Compiled resource values matched after XML newline normalization; composite formats parsed: {values}");
}
