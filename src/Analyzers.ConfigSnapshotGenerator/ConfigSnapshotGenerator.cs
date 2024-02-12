using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Text;

namespace PodNet.Analyzers;

[Generator(LanguageNames.CSharp)]
public sealed class ConfigSnapshotGenerator : IIncrementalGenerator
{
    public static string EnableAnalyzerConfigSnapshotProperty { get; } = "PodNetEnableAnalyzerConfigSnapshot";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var enabled = context.AnalyzerConfigOptionsProvider
            .Select((o, _) => o.GlobalOptions.TryGetValue($"build_property.{EnableAnalyzerConfigSnapshotProperty}", out var value) && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));

        GenerateSource(context, enabled, context.AnalyzerConfigOptionsProvider, GetGlobalOptionsContents, "_AnalyzerConfigOptions.GlobalOptions");
        GenerateSource(context, enabled, context.AdditionalTextsProvider.Combine(context.AnalyzerConfigOptionsProvider).Collect(), GetAdditionalTextsOptionsContents, "_AdditionalTexts");
        GenerateSource(context, enabled, context.ParseOptionsProvider, GetParseOptionsContents, "_ParseOptions");
        GenerateSource(context, enabled, context.CompilationProvider.SelectMany((c, _) => c.SyntaxTrees).Collect().Combine(context.AnalyzerConfigOptionsProvider), GetSyntaxTreesOptionsContents, "_SyntaxTrees");
        GenerateSource(context, enabled, context.CompilationProvider, GetCompilationOptions, "_Compilation");
    }

    private static string GetCompilationOptions(Compilation compilation, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"// [AssemblyName]:            {compilation.AssemblyName}");
        sb.AppendLine($"// [IsCaseSensitive]:         {compilation.IsCaseSensitive}");
        sb.AppendLine($"// [Language]:                {compilation.Language}");
        sb.AppendLine($"// <ReferencedAssemblyNames>:");
        foreach (var reference in compilation.ReferencedAssemblyNames)
            sb.AppendLine($"//   {reference}");

        sb.AppendLine();

        sb.AppendLine($"// <References>:");
        foreach (var (reference, type) in compilation.DirectiveReferences.Select(r => (r, "#r")).Concat(compilation.ExternalReferences.Select(r => (r, "ex"))))
        {
            sb.AppendLine($"//   | '{reference.Display}' {type} {reference.Properties.Kind} {(reference.Properties.EmbedInteropTypes ? "(EmbedInteropTypes)" : "")}");
            if (reference.Properties.Aliases.Length > 0)
            {
                foreach (var alias in reference.Properties.Aliases)
                    sb.AppendLine($"//     > [Alias]: {alias}");
            }
        }
        return sb.ToString();
    }

    private static void GenerateSource<T>(
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<bool> enabled,
        IncrementalValueProvider<T> source,
        Func<T, CancellationToken, string> stringProvider,
        string hintName)
    {
        var stringOrNull = enabled.Combine(source)
            .Select((pair, cancellation) => pair.Left ? stringProvider(pair.Right, cancellation) : null);

        context.RegisterSourceOutput(stringOrNull, (context, value) =>
        {
            if (value is not null)
                context.AddSource(hintName, value);
        });
    }

    private static string GetParseOptionsContents(ParseOptions options, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"// [DocumentationMode]:       {options.DocumentationMode}");
        sb.AppendLine($"// [Kind]:                    {options.Kind}");
        sb.AppendLine($"// [Language]:                {options.Language}");
        sb.AppendLine($"// [PreprocessorSymbolNames]: {string.Join(", ", options.PreprocessorSymbolNames)}");
        sb.AppendLine($"// [SpecifiedKind]:           {string.Join(", ", options.SpecifiedKind)}");
        sb.AppendLine($"// <Errors>:");
        foreach (var error in options.Errors)
            sb.AppendLine($"//   | {error}");

        sb.AppendLine($"// <Features>:");
        foreach (var feature in options.Features)
            sb.AppendLine($"//   | [{feature.Key}] = {feature.Value}");

        return sb.ToString();
    }

    private static string GetSyntaxTreesOptionsContents((ImmutableArray<SyntaxTree> SyntaxTrees, AnalyzerConfigOptionsProvider OptionsProvider) pair, CancellationToken cancellationToken)
        => GetTextItemsMetadata(pair.SyntaxTrees.Select(i => (i.FilePath, i.GetText(), (bool?)i.HasCompilationUnitRoot, pair.OptionsProvider.GetOptions(i)))!);

    private static string GetTextItemsMetadata(IEnumerable<(string path, SourceText? sourceText, bool? hasCompilationUnitRoot, AnalyzerConfigOptions options)> items)
    {
        var sb = new StringBuilder();

        foreach (var (path, sourceText, hasCompilationUnitRoot, options) in items)
        {
            sb.AppendLine($"// [{path}]:");
            if (hasCompilationUnitRoot != null)
                sb.AppendLine($"//   | [HasCompilationUnitRoot]: {hasCompilationUnitRoot}");
            sb.AppendLine($"//   | GetText():");
            if (sourceText is not null)
            {
                sb.AppendLine($"//   | [CanBeEmbedded]:     {sourceText.CanBeEmbedded}");
                sb.AppendLine($"//   | [ChecksumAlgorithm]: {sourceText.ChecksumAlgorithm}");
                sb.AppendLine($"//   | [Encoding]:          {sourceText.Encoding}");
                sb.AppendLine($"//   | [Length]:            {sourceText.Length}");
                sb.AppendLine($"//   | [Lines.Count]:       {sourceText.Lines.Count}");
            }
            else
                sb.AppendLine("$//   | <- null ->");
            sb.AppendLine(GetAllValuesText(options, "//   |> "));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string GetAdditionalTextsOptionsContents(ImmutableArray<(AdditionalText Text, AnalyzerConfigOptionsProvider OptionsProvider)> items, CancellationToken cancellationToken)
        => GetTextItemsMetadata(items.Select(i => (i.Text.Path, i.Text.GetText(), (bool?)null, i.OptionsProvider.GetOptions(i.Text))));

    private static string GetGlobalOptionsContents(AnalyzerConfigOptionsProvider options, CancellationToken cancellationToken)
        => GetAllValuesText(options.GlobalOptions);

    private static string GetAllValuesText(AnalyzerConfigOptions options, string itemPrefix = "// ", string separator = "\r\n")
    {
        var maxLength = options.Keys.Max(k => k.Length);
        return string.Join(separator, options.Keys.Select(k => GetKeyAndValueText(options, k, maxLength + 2, itemPrefix)));
    }

    private static string GetKeyAndValueText(AnalyzerConfigOptions options, string key, int padItems = 0, string prefix = "// ")
        => $"{prefix}{$"[{key}]".PadRight(padItems)} = {GetValueText(options, key)}";

    private static string GetValueText(AnalyzerConfigOptions options, string key)
        => options.TryGetValue(key, out var value) ? (value, string.IsNullOrWhiteSpace(value)) switch
        {
            (null, _) => "<- null ->",
            ({ Length: 0 }, _) => "<- empty ->",
            ({ Length: > 0 }, true) => "<- whitespace ->",
            ({ Length: > 0 }, false) => $"\"{value}\"",
        } : "<- key not found ->";
}
