using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static class Migration
{
    private sealed record Entry(string Path, string Kind, string Text, string Project, string Key);
    internal static void Run(string root)
    {
        var plan = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(Path.Combine(root, "build/localization/migration-plan.json")))!;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj")))
        {
            string path = Path.GetRelativePath(root, file).Replace('\\', '/');
            var entries = plan.Where(p => p.Path == path).ToArray();
            bool ui = Path.GetFileName(file).StartsWith("UiText", StringComparison.Ordinal);
            string original = File.ReadAllText(file);
            if (!ui && entries.Length == 0 && !original.Contains("UiText.", StringComparison.Ordinal)) continue;
            var syntax = CSharpSyntaxTree.ParseText(original).GetRoot();
            var result = new Rewriter(entries, ui).Visit(syntax)!.ToFullString();
            if (result != original) File.WriteAllText(file, result, new UTF8Encoding(false));
        }
    }
    private sealed class Rewriter(Entry[] entries, bool ui) : CSharpSyntaxRewriter
    {
        private static string Access(Entry entry) => entry.Project == "App" ? "global::Iris.App.Resources.AppResources" : "global::Iris.Core.Localization.CoreText";
        private Entry? Find(string kind, string text) => entries.FirstOrDefault(e => e.Kind == kind && e.Text == text);
        private static bool Localized(string text) => text.Contains("AppResources.Get(", StringComparison.Ordinal) || text.Contains("CoreText.Get(", StringComparison.Ordinal) || text.Contains(".Format(\"Text_", StringComparison.Ordinal);
        public override SyntaxNode? VisitLiteralExpression(LiteralExpressionSyntax node)
        {
            var entry = node.Token.Value is string value ? Find("literal", value) : null;
            return entry == null ? base.VisitLiteralExpression(node) : SyntaxFactory.ParseExpression($"{Access(entry)}.Get(\"{entry.Key}\")").WithTriviaFrom(node);
        }
        public override SyntaxNode? VisitInterpolatedStringExpression(InterpolatedStringExpressionSyntax node)
        {
            var format = new StringBuilder(); var expressions = new List<string>();
            foreach (var part in node.Contents)
            {
                if (part is InterpolatedStringTextSyntax segment) format.Append(segment.TextToken.ValueText);
                else if (part is InterpolationSyntax hole)
                {
                    format.Append('{').Append(expressions.Count);
                    if (hole.AlignmentClause != null) format.Append(',').Append(hole.AlignmentClause.Value.ToString());
                    if (hole.FormatClause != null) format.Append(':').Append(hole.FormatClause.FormatStringToken.ValueText);
                    format.Append('}'); expressions.Add(Visit(hole.Expression)!.ToString());
                }
            }
            var entry = Find("interpolated", format.ToString());
            return entry == null ? base.VisitInterpolatedStringExpression(node) : SyntaxFactory.ParseExpression($"{Access(entry)}.Format(\"{entry.Key}\", {string.Join(", ", expressions)})").WithTriviaFrom(node);
        }
        public override SyntaxNode? VisitFieldDeclaration(FieldDeclarationSyntax node)
        {
            var visited = (FieldDeclarationSyntax)base.VisitFieldDeclaration(node)!;
            if (node.Declaration.Variables.Count != 1) return visited;
            var variable = visited.Declaration.Variables[0];
            if (ui || node.Modifiers.Any(SyntaxKind.ConstKeyword) && Localized(visited.ToString()))
            {
                string visibility = node.Modifiers.Any(SyntaxKind.PublicKeyword) ? "public" : node.Modifiers.Any(SyntaxKind.InternalKeyword) ? "internal" : "private";
                string expression = ui ? $"global::Iris.App.Resources.AppResources.Get(\"{variable.Identifier.ValueText}\")" : variable.Initializer!.Value.ToString();
                return SyntaxFactory.ParseMemberDeclaration($"{visibility} static string {variable.Identifier.ValueText} => {expression};")!.WithTriviaFrom(node);
            }
            return visited;
        }
        public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
        {
            var visited = (LocalDeclarationStatementSyntax)base.VisitLocalDeclarationStatement(node)!;
            return node.Modifiers.Any(SyntaxKind.ConstKeyword) && Localized(visited.ToString())
                ? visited.WithModifiers(SyntaxFactory.TokenList(visited.Modifiers.Where(t => !t.IsKind(SyntaxKind.ConstKeyword)))) : visited;
        }
        public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
        {
            var visited = (PropertyDeclarationSyntax)base.VisitPropertyDeclaration(node)!;
            if (visited.Modifiers.Any(SyntaxKind.StaticKeyword) && visited.Initializer != null && (visited.Initializer.ToString().Contains("UiText.", StringComparison.Ordinal) || Localized(visited.Initializer.ToString())))
                return visited.WithAccessorList(null).WithExpressionBody(SyntaxFactory.ArrowExpressionClause(visited.Initializer.Value)).WithInitializer(null)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
            return visited;
        }
        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
            if (node.Expression.ToString() == "FormattableString.Invariant" && visited.ArgumentList.Arguments.Count == 1 && Localized(visited.ArgumentList.Arguments[0].ToString()))
                return visited.ArgumentList.Arguments[0].Expression.WithTriviaFrom(node);
            return visited;
        }
    }
}
