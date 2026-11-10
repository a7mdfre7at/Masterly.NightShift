using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Masterly.NightShift.SourceGenerator;

/// <summary>
/// Emits a <c>Masterly.NightShift.Common.JobInvoker</c> subclass for every background job method the assembly uses, so
/// <c>Masterly.NightShift.Core</c> performs those jobs with a direct call instead of <c>MethodInfo.Invoke</c>.
/// </summary>
/// <remarks>
/// Job methods are found in two ways: the expression lambdas passed to Masterly.NightShift client APIs
/// (<c>Enqueue</c>, <c>Schedule</c>, <c>AddOrUpdate</c> and friends), and every public method of a type
/// marked with <c>[Masterly.NightShift.GenerateJobInvokers]</c>, except generic methods, overrides of
/// <c>Equals</c>, <c>GetHashCode</c> and <c>ToString</c>, and compiler-generated members such as a record's
/// <c>Equals</c> or <c>&lt;Clone&gt;$</c>. A method the generator can't call safely is skipped
/// and Masterly.NightShift uses a compiled invoker for it at runtime instead. The generator never fails the build: an
/// unexpected error becomes the NSGEN001 warning.
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class JobInvokerGenerator : IIncrementalGenerator
{
    private const string GenerateAttributeName = "Masterly.NightShift.GenerateJobInvokersAttribute";
    private const string JobInvokerBaseType = "global::Masterly.NightShift.Common.JobInvoker";
    private const string RegistryType = "global::Masterly.NightShift.Common.JobInvokerRegistry";
    private const string ProviderAttribute = "global::Masterly.NightShift.Common.JobInvokersProviderAttribute";
    private const string ModuleInitializerMetadataName = "System.Runtime.CompilerServices.ModuleInitializerAttribute";
    private const string DisableProperty = "build_property.NightShiftDisableJobInvokerGeneration";

    private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
            | SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.ExpandNullable);

    private static readonly DiagnosticDescriptor GeneratorFailed = new(
        id: "NSGEN001",
        title: "Masterly.NightShift job invokers could not be generated",
        messageFormat: "The Masterly.NightShift source generator failed, the affected jobs will use compiled invokers at runtime: {0}",
        category: "Masterly.NightShift.SourceGenerator",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValueProvider<bool> disabled = context.AnalyzerConfigOptionsProvider.Select(static (options, _) =>
            options.GlobalOptions.TryGetValue(DisableProperty, out string? value)
            && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));

        IncrementalValuesProvider<InvokerModel> fromLambdas = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is LambdaExpressionSyntax { Body: InvocationExpressionSyntax },
                static (ctx, cancellationToken) => TryCreateFromLambda(ctx, cancellationToken))
            .Where(static model => model != null)
            .Select(static (model, _) => model!);

        IncrementalValuesProvider<InvokerModel> fromAttributes = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                GenerateAttributeName,
                static (node, _) => node is TypeDeclarationSyntax,
                static (ctx, cancellationToken) => CreateFromMarkedType(ctx, cancellationToken))
            .SelectMany(static (models, _) => models);

        IncrementalValuesProvider<InvokerModel> fromHandlers = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                static (ctx, cancellationToken) => CreateFromHandler(ctx, cancellationToken))
            .SelectMany(static (models, _) => models);

        IncrementalValueProvider<ImmutableArray<InvokerModel>> declared = fromAttributes.Collect()
            .Combine(fromHandlers.Collect())
            .Select(static (pair, _) => pair.Left.AddRange(pair.Right));

        IncrementalValueProvider<EnvironmentModel> environment = context.CompilationProvider.Select(static (compilation, _) => new EnvironmentModel(
            assemblyName: compilation.AssemblyName ?? "Assembly",
            hasModuleInitializer: compilation.GetTypeByMetadataName(ModuleInitializerMetadataName) != null
                && compilation is CSharpCompilation csharp
                && csharp.LanguageVersion >= LanguageVersion.CSharp9,
            hasNightShift: compilation.GetTypeByMetadataName("Masterly.NightShift.Common.JobInvokerRegistry") != null));

        IncrementalValueProvider<(((ImmutableArray<InvokerModel> Left, ImmutableArray<InvokerModel> Right) Left, EnvironmentModel Right) Left, bool Right)> all = fromLambdas.Collect()
            .Combine(declared)
            .Combine(environment)
            .Combine(disabled);

        context.RegisterSourceOutput(all, static (output, source) =>
        {
            (((ImmutableArray<InvokerModel> lambdas, ImmutableArray<InvokerModel> marked), EnvironmentModel? env), bool isDisabled) = source;
            if (isDisabled || !env.HasNightShift) return;

            try
            {
                Emit(output, lambdas.Concat(marked), env);
            }
            catch (Exception ex)
            {
                output.ReportDiagnostic(Diagnostic.Create(GeneratorFailed, Location.None, ex.Message));
            }
        });
    }

    private static InvokerModel? TryCreateFromLambda(GeneratorSyntaxContext context, CancellationToken cancellationToken)
    {
        LambdaExpressionSyntax lambda = (LambdaExpressionSyntax)context.Node;

        if (lambda.AsyncKeyword != default || lambda.Body is not InvocationExpressionSyntax invocation)
        {
            return null;
        }

        SemanticModel semanticModel = context.SemanticModel;

        // Only lambdas converted to expression trees can be jobs
        if (semanticModel.GetTypeInfo(lambda, cancellationToken).ConvertedType is not INamedTypeSymbol { Name: "Expression" } convertedType
            || convertedType.ContainingNamespace?.ToDisplayString() != "System.Linq.Expressions")
        {
            return null;
        }

        if (!IsPassedToNightShift(lambda, semanticModel, cancellationToken))
        {
            return null;
        }

        if (semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method)
        {
            return null;
        }

        // An extension method call `x.Ext()` is a static call of the unreduced method for Masterly.NightShift
        method = method.GetConstructedReducedFrom() ?? method;

        return TryCreateModel(method, semanticModel.Compilation);
    }

    private static bool IsPassedToNightShift(LambdaExpressionSyntax lambda, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        if (lambda.Parent is not ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax call } })
        {
            return false;
        }

        SymbolInfo symbolInfo = semanticModel.GetSymbolInfo(call, cancellationToken);
        ImmutableArray<ISymbol> candidates = symbolInfo.Symbol != null ? ImmutableArray.Create(symbolInfo.Symbol) : symbolInfo.CandidateSymbols;

        foreach (ISymbol candidate in candidates)
        {
            string? ns = candidate.ContainingNamespace?.ToDisplayString();
            if (ns != null && (ns == "Masterly.NightShift" || ns.StartsWith("Masterly.NightShift.", StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private static ImmutableArray<InvokerModel> CreateFromMarkedType(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        if (context.TargetSymbol is not INamedTypeSymbol type)
        {
            return ImmutableArray<InvokerModel>.Empty;
        }

        ImmutableArray<InvokerModel>.Builder builder = ImmutableArray.CreateBuilder<InvokerModel>();

        foreach (ISymbol member in type.GetMembers())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Overrides of Equals, GetHashCode and ToString aren't jobs, even when a marked type declares them
            if (member is IMethodSymbol { MethodKind: MethodKind.Ordinary, DeclaredAccessibility: Accessibility.Public } method
                && !method.IsGenericMethod
                && !OverridesObjectMember(method))
            {
                InvokerModel? model = TryCreateModel(method, context.SemanticModel.Compilation);
                if (model != null)
                {
                    builder.Add(model);
                }
            }
        }

        return builder.ToImmutable();
    }

    // Jobs for a job class are created from its type or an arguments object, so no lambda names its method
    private static ImmutableArray<InvokerModel> CreateFromHandler(GeneratorSyntaxContext context, CancellationToken cancellationToken)
    {
        if (context.SemanticModel.GetDeclaredSymbol(context.Node, cancellationToken) is not INamedTypeSymbol type)
        {
            return ImmutableArray<InvokerModel>.Empty;
        }

        ImmutableArray<InvokerModel>.Builder builder = ImmutableArray.CreateBuilder<InvokerModel>();

        foreach (INamedTypeSymbol handlerInterface in type.AllInterfaces.Where(IsJobClassInterface))
        {
            foreach (ISymbol member in handlerInterface.GetMembers())
            {
                if (type.FindImplementationForInterfaceMember(member) is IMethodSymbol method
                    && TryCreateModel(method, context.SemanticModel.Compilation) is { } model)
                {
                    builder.Add(model);
                }
            }
        }

        return builder.ToImmutable();
    }

    private static bool IsJobClassInterface(INamedTypeSymbol candidate)
    {
        return candidate is { Name: "IJobHandler", Arity: 1 } or { Name: "IJob", Arity: 0 }
            && candidate.ContainingNamespace?.ToDisplayString() == "Masterly.NightShift";
    }

    private static InvokerModel? TryCreateModel(IMethodSymbol method, Compilation compilation)
    {
        // Compiler-generated members (a record's Equals, ToString or <Clone>$) and names C# can't write
        // aren't jobs, and a static abstract or virtual interface member can only be called through a
        // type parameter
        if (method.MethodKind != MethodKind.Ordinary
            || method.DeclaredAccessibility != Accessibility.Public
            || method.IsImplicitlyDeclared
            || !method.CanBeReferencedByName
            || (method.IsStatic && (method.IsAbstract || method.IsVirtual))
            || method.ReturnsByRef
            || method.ReturnsByRefReadonly
            || method.IsVararg
            || method.ContainingType == null)
        {
            return null;
        }

        // Masterly.NightShift rejects async void methods, and we can't call anything that still has open type parameters
        if (method.ReturnsVoid && method.IsAsync) return null;
        if (ContainsTypeParameter(method.ContainingType)) return null;
        if (method.IsGenericMethod && method.TypeArguments.Any(ContainsTypeParameter)) return null;

        IAssemblySymbol within = compilation.Assembly;

        if (!compilation.IsSymbolAccessibleWithin(method, within) || !IsTypeUsable(method.ContainingType, compilation, within))
        {
            return null;
        }

        if (!method.ReturnsVoid && !IsTypeUsable(method.ReturnType, compilation, within))
        {
            return null;
        }

        string[] parameterTypes = new string[method.Parameters.Length];
        bool[] parameterDefaults = new bool[method.Parameters.Length];

        for (int i = 0; i < method.Parameters.Length; i++)
        {
            IParameterSymbol parameter = method.Parameters[i];

            if (parameter.RefKind != RefKind.None || !IsTypeUsable(parameter.Type, compilation, within))
            {
                return null;
            }

            parameterTypes[i] = parameter.Type.ToDisplayString(TypeFormat);
            parameterDefaults[i] = parameter.Type.IsValueType && !IsNullableValueType(parameter.Type);
        }

        string[] genericArguments = method.IsGenericMethod
            ? [.. method.TypeArguments.Select(x => x.ToDisplayString(TypeFormat))]
            : [];

        return new InvokerModel(
            declaringType: method.ContainingType.ToDisplayString(TypeFormat),
            methodName: method.Name,
            parameterTypes: parameterTypes,
            parameterDefaults: parameterDefaults,
            genericArguments: genericArguments,
            isStatic: method.IsStatic,
            returnsVoid: method.ReturnsVoid);
    }

    private static bool OverridesObjectMember(IMethodSymbol method)
    {
        if (!method.IsOverride) return false;

        IMethodSymbol current = method;
        while (current.OverriddenMethod != null)
        {
            current = current.OverriddenMethod;
        }

        return current.ContainingType?.SpecialType == SpecialType.System_Object;
    }

    private static bool IsNullableValueType(ITypeSymbol type)
    {
        return type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };
    }

    private static bool ContainsTypeParameter(ITypeSymbol type)
    {
        switch (type)
        {
            case ITypeParameterSymbol:
                return true;
            case IArrayTypeSymbol array:
                return ContainsTypeParameter(array.ElementType);
            case INamedTypeSymbol named:
                if (named.ContainingType != null && ContainsTypeParameter(named.ContainingType)) return true;
                return named.TypeArguments.Any(ContainsTypeParameter);
            default:
                return false;
        }
    }

    /// <summary>
    /// Whether generated code in this assembly can name the type and cast to it: accessible, not a
    /// pointer, ref struct, dynamic or anonymous type, and the same for every type argument.
    /// </summary>
    private static bool IsTypeUsable(ITypeSymbol type, Compilation compilation, IAssemblySymbol within)
    {
        switch (type)
        {
            case IPointerTypeSymbol:
            case IFunctionPointerTypeSymbol:
            case IDynamicTypeSymbol:
            case ITypeParameterSymbol:
                return false;
            case IArrayTypeSymbol array:
                return IsTypeUsable(array.ElementType, compilation, within);
            case INamedTypeSymbol named:
                if (named.IsAnonymousType || named.IsRefLikeType || named.TypeKind == TypeKind.Error) return false;
                if (named.IsFileLocal) return false;
                if (!compilation.IsSymbolAccessibleWithin(named, within)) return false;
                if (named.ContainingType != null && !IsTypeUsable(named.ContainingType, compilation, within)) return false;
                return named.TypeArguments.All(argument => IsTypeUsable(argument, compilation, within));
            default:
                return false;
        }
    }

    private static void Emit(SourceProductionContext output, IEnumerable<InvokerModel> models, EnvironmentModel env)
    {
        List<InvokerModel> distinct = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (InvokerModel? model in models.OrderBy(static x => x.Key, StringComparer.Ordinal))
        {
            if (seen.Add(model.Key))
            {
                distinct.Add(model);
            }
        }

        if (distinct.Count == 0) return;

        string className = "NightShiftJobInvokers_" + Sanitize(env.AssemblyName);
        StringBuilder sb = new();

        // No #nullable directive: the compiler treats generated code as nullable-oblivious, so the invokers compile
        // the same whatever the consumer's nullable settings and language version.
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Reflection-free job invokers emitted by Masterly.NightShift.SourceGenerator. Do not edit.");
        sb.AppendLine("#pragma warning disable");
        sb.AppendLine();
        sb.Append("[assembly: ").Append(ProviderAttribute).Append("(typeof(global::Masterly.NightShift.Generated.").Append(className).AppendLine("))]");
        sb.AppendLine();
        sb.AppendLine("namespace Masterly.NightShift.Generated");
        sb.AppendLine("{");
        sb.AppendLine("    [global::System.CodeDom.Compiler.GeneratedCode(\"Masterly.NightShift.SourceGenerator\", \"1.0.0\")]");
        sb.AppendLine("    [global::System.Diagnostics.DebuggerNonUserCode]");
        sb.Append("    internal static class ").AppendLine(className);
        sb.AppendLine("    {");
        sb.AppendLine("        private static int _registered;");
        sb.AppendLine();

        if (env.HasModuleInitializer)
        {
            sb.AppendLine("        [global::System.Runtime.CompilerServices.ModuleInitializer]");
        }

        sb.AppendLine("        public static void Register()");
        sb.AppendLine("        {");
        sb.AppendLine("            if (global::System.Threading.Interlocked.Exchange(ref _registered, 1) != 0) return;");

        for (int i = 0; i < distinct.Count; i++)
        {
            sb.Append("            ").Append(RegistryType).Append(".Register(new Invoker").Append(i).AppendLine("());");
        }

        sb.AppendLine("        }");

        for (int i = 0; i < distinct.Count; i++)
        {
            sb.AppendLine();
            EmitInvoker(sb, distinct[i], i);
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");

        output.AddSource("NightShiftJobInvokers.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    private static void EmitInvoker(StringBuilder sb, InvokerModel model, int index)
    {
        sb.Append("        // ").Append(model.DeclaringType).Append('.').Append(model.MethodName);

        if (model.GenericArguments.Length > 0)
        {
            sb.Append('<').Append(string.Join(", ", model.GenericArguments)).Append('>');
        }

        sb.Append('(').Append(string.Join(", ", model.ParameterTypes)).AppendLine(")");

        sb.Append("        private sealed class Invoker").Append(index).Append(" : ").AppendLine(JobInvokerBaseType);
        sb.AppendLine("        {");

        sb.Append("            public Invoker").Append(index).Append("() : base(typeof(").Append(model.DeclaringType).Append("), \"")
            .Append(model.MethodName).Append("\", ");
        AppendTypeArray(sb, model.ParameterTypes);
        sb.Append(", ");
        AppendTypeArray(sb, model.GenericArguments);
        sb.AppendLine(") { }");
        sb.AppendLine();

        sb.AppendLine("            public override object Invoke(object instance, object[] arguments)");
        sb.AppendLine("            {");
        sb.Append("                ");

        if (!model.ReturnsVoid)
        {
            sb.Append("return ");
        }

        if (model.IsStatic)
        {
            sb.Append(model.DeclaringType);
        }
        else
        {
            sb.Append("((").Append(model.DeclaringType).Append(")instance)");
        }

        sb.Append('.').Append(EscapeIdentifier(model.MethodName));

        if (model.GenericArguments.Length > 0)
        {
            sb.Append('<').Append(string.Join(", ", model.GenericArguments)).Append('>');
        }

        sb.Append('(');

        for (int i = 0; i < model.ParameterTypes.Length; i++)
        {
            if (i > 0) sb.Append(", ");

            string type = model.ParameterTypes[i];

            if (model.ParameterDefaults[i])
            {
                // Like MethodInfo.Invoke, a null argument for a non-nullable value type means its default value.
                // Anything else is unboxed like the compiled invoker does: a boxed int for an enum parameter
                // works, a value of another type throws InvalidCastException instead of becoming the default.
                sb.Append("arguments[").Append(i).Append("] == null ? default(").Append(type).Append(") : (")
                    .Append(type).Append(")arguments[").Append(i).Append(']');
            }
            else
            {
                sb.Append('(').Append(type).Append(")arguments[").Append(i).Append(']');
            }
        }

        sb.AppendLine(");");

        if (model.ReturnsVoid)
        {
            sb.AppendLine("                return null;");
        }

        sb.AppendLine("            }");
        sb.AppendLine("        }");
    }

    private static string EscapeIdentifier(string name)
    {
        return SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;
    }

    private static void AppendTypeArray(StringBuilder sb, string[] types)
    {
        if (types.Length == 0)
        {
            sb.Append("global::System.Type.EmptyTypes");
            return;
        }

        sb.Append("new global::System.Type[] { ");

        for (int i = 0; i < types.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append("typeof(").Append(types[i]).Append(')');
        }

        sb.Append(" }");
    }

    private static string Sanitize(string assemblyName)
    {
        StringBuilder sb = new(assemblyName.Length);

        foreach (char ch in assemblyName)
        {
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        }

        return sb.ToString();
    }

    private sealed class EnvironmentModel(string assemblyName, bool hasModuleInitializer, bool hasNightShift) : IEquatable<EnvironmentModel>
    {
        public string AssemblyName { get; } = assemblyName;
        public bool HasModuleInitializer { get; } = hasModuleInitializer;
        public bool HasNightShift { get; } = hasNightShift;

        public bool Equals(EnvironmentModel? other)
        {
            return other != null
                && AssemblyName == other.AssemblyName
                && HasModuleInitializer == other.HasModuleInitializer
                && HasNightShift == other.HasNightShift;
        }

        public override bool Equals(object? obj) => Equals(obj as EnvironmentModel);

        public override int GetHashCode()
        {
            unchecked
            {
                return (AssemblyName.GetHashCode() * 397) ^ (HasModuleInitializer ? 1 : 0) ^ (HasNightShift ? 2 : 0);
            }
        }
    }

    /// <summary>
    /// Everything needed to emit one invoker, as plain strings so the incremental pipeline can compare
    /// models between compilations.
    /// </summary>
    private sealed class InvokerModel(
        string declaringType,
        string methodName,
        string[] parameterTypes,
        bool[] parameterDefaults,
        string[] genericArguments,
        bool isStatic,
        bool returnsVoid) : IEquatable<InvokerModel>
    {
        public string Key { get; } = declaringType + "::" + methodName
                + "<" + string.Join(",", genericArguments) + ">"
                + "(" + string.Join(",", parameterTypes) + ")"
                + (isStatic ? ":static" : string.Empty)
                + (returnsVoid ? ":void" : string.Empty);
        public string DeclaringType { get; } = declaringType;
        public string MethodName { get; } = methodName;
        public string[] ParameterTypes { get; } = parameterTypes;
        public bool[] ParameterDefaults { get; } = parameterDefaults;
        public string[] GenericArguments { get; } = genericArguments;
        public bool IsStatic { get; } = isStatic;
        public bool ReturnsVoid { get; } = returnsVoid;

        public bool Equals(InvokerModel? other) => other != null && Key == other.Key;
        public override bool Equals(object? obj) => Equals(obj as InvokerModel);
        public override int GetHashCode() => Key.GetHashCode();
    }
}
