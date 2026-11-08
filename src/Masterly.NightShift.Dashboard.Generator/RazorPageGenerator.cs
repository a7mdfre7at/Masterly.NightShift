using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.Language.Extensions;
using Microsoft.AspNetCore.Razor.Language.Intermediate;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Masterly.NightShift.Dashboard.Generator
{
    /// <summary>
    /// Compiles the dashboard's <c>*.cshtml</c> pages (passed as <c>AdditionalFiles</c>) into classes
    /// derived from <c>Masterly.NightShift.Dashboard.RazorPage</c>, as RazorGenerator did before.
    /// </summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class RazorPageGenerator : IIncrementalGenerator
    {
        private const string PagesNamespace = "Masterly.NightShift.Dashboard.Pages";
        private const string BaseType = "Masterly.NightShift.Dashboard.RazorPage";

        // The usings RazorGenerator added to every page
        private const string DefaultImports =
            "@using System\r\n@using System.Collections.Generic\r\n@using System.Linq\r\n@using System.Text\r\n";

        // Each page starts with RazorGenerator's options, e.g.
        // @* Generator: Template TypeVisibility: Internal GeneratePrettyNames: True TrimLeadingUnderscores : true *@
        private static readonly Regex CommentOnlyLine = new Regex(@"^([ \t]*)@\*.*?\*@[ \t]*(?=\r$)", RegexOptions.Multiline);
        private static readonly Regex VisibilityOption = new Regex(@"TypeVisibility\s*:\s*(\w+)", RegexOptions.IgnoreCase);
        private static readonly Regex TrimUnderscoresOption = new Regex(@"TrimLeadingUnderscores\s*:\s*(\w+)", RegexOptions.IgnoreCase);

        private static readonly DiagnosticDescriptor RazorError = new DiagnosticDescriptor(
            id: "NSDASH001",
            title: "Dashboard page can't be compiled",
            messageFormat: "{0}",
            category: "Masterly.NightShift.Dashboard",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var pages = context.AdditionalTextsProvider
                .Where(static file => file.Path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
                .Select(static (file, cancellationToken) => (file.Path, Text: file.GetText(cancellationToken)?.ToString() ?? String.Empty));

            context.RegisterSourceOutput(pages, static (output, page) => Generate(output, page.Path, page.Text));
        }

        private static void Generate(SourceProductionContext output, string path, string text)
        {
            // Pages have always been compiled with Windows line endings: keep the HTML identical on every OS
            text = NormalizeLineEndings(text.TrimStart('﻿'));

            var options = text.Split('\n')[0];

            // RazorGenerator kept the indentation and line break of a line holding only a Razor comment,
            // while Razor removes the whole line. Drop just the comments to keep the HTML identical.
            text = CommentOnlyLine.Replace(text, "$1");
            var className = GetClassName(path, options);
            var visibility = String.Equals(VisibilityOption.Match(options).Groups[1].Value, "Public", StringComparison.OrdinalIgnoreCase)
                ? "public"
                : "internal";

            var engine = RazorProjectEngine.Create(RazorConfiguration.Default, RazorProjectFileSystem.Create("/"), builder =>
            {
                builder.SetNamespace(PagesNamespace);
                builder.SetBaseType(BaseType);
                InheritsDirective.Register(builder);

                builder.ConfigureClass((_, @class) =>
                {
                    @class.ClassName = className;
                    @class.Modifiers.Clear();
                    @class.Modifiers.Add(visibility);
                    @class.Modifiers.Add("partial");
                });

                builder.Features.Add(new ExecuteMethodPass());
            });

            var document = engine.Process(
                RazorSourceDocument.Create(text, path),
                FileKinds.Legacy,
                new[] { RazorSourceDocument.Create(DefaultImports, "_DefaultImports.cshtml") },
                Array.Empty<TagHelperDescriptor>());

            var csharp = document.GetCSharpDocument();

            foreach (var diagnostic in csharp.Diagnostics.Where(static x => x.Severity == RazorDiagnosticSeverity.Error))
            {
                output.ReportDiagnostic(Diagnostic.Create(RazorError, Location.None, $"{path}: {diagnostic.GetMessage(CultureInfo.InvariantCulture)}"));
            }

            output.AddSource($"{className}.cshtml.g.cs", SourceText.From(csharp.GeneratedCode, Encoding.UTF8));
        }

        private static string GetClassName(string path, string options)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var trimUnderscores = String.Equals(TrimUnderscoresOption.Match(options).Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);

            return trimUnderscores ? name.TrimStart('_') : name;
        }

        private static string NormalizeLineEndings(string text)
        {
            return text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        }

        /// <summary>
        /// Turns Razor's default <c>async Task ExecuteAsync()</c> into the
        /// <c>public override void Execute()</c> that <c>RazorPage</c> expects.
        /// </summary>
        private sealed class ExecuteMethodPass : IntermediateNodePassBase, IRazorOptimizationPass
        {
            protected override void ExecuteCore(RazorCodeDocument codeDocument, DocumentIntermediateNode documentNode)
            {
                var method = documentNode.FindPrimaryMethod();

                method.MethodName = "Execute";
                method.ReturnType = "void";
                method.Modifiers.Clear();
                method.Modifiers.Add("public");
                method.Modifiers.Add("override");
            }
        }
    }
}
