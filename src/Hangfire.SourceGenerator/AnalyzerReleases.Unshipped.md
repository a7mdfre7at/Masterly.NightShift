; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
HFGEN001 | Hangfire.SourceGenerator | Warning | JobInvokerGenerator, the generator failed and the affected jobs fall back to compiled invokers
