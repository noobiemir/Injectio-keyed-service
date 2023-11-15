using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;

using Injectio.Attributes;
using Injectio.Generators.Extensions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Injectio.Generators;

[Generator]
public class ServiceRegistrationGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var pipeline = context.SyntaxProvider.CreateSyntaxProvider(
                predicate: SyntacticPredicate,
                transform: SemanticTransform
            )
            .Where(static context => context is not null);

        // Emit the diagnostics, if needed
        var diagnostics = pipeline
            .Select(static (item, _) => item.Diagnostics)
            .Where(static item => item.Count > 0);

        context.RegisterSourceOutput(diagnostics, ReportDiagnostic);

        // select contexts with registrations
        var registrations = pipeline
            .Where(static context => context.ServiceRegistrations.Count > 0 || context.ModuleRegistrations.Count > 0)
            .Collect();

        // include config options
        var assemblyName = context.CompilationProvider
            .Select(static (c, _) => c.AssemblyName);

        var methodName = context.AnalyzerConfigOptionsProvider
            .Select(static (c, _) =>
            {
                c.GlobalOptions.TryGetValue("build_property.injectioname", out var methodName);
                return methodName;
            });

        var options = assemblyName.Combine(methodName);
        var generation = registrations.Combine(options);

        context.RegisterSourceOutput(generation, ExecuteGeneration);
    }

    private void ExecuteGeneration(
        SourceProductionContext sourceContext,
        (ImmutableArray<ServiceRegistrationContext> Registrations, (string AssemblyName, string MethodName) Options) source)
    {
        var serviceRegistrations = source.Registrations
            .SelectMany(m => m.ServiceRegistrations)
            .Where(m => m is not null)
            .ToArray();

        var moduleRegistrations = source.Registrations
            .SelectMany(m => m.ModuleRegistrations)
            .Where(m => m is not null)
            .ToArray();

        // compute extension method name
        var methodName = source.Options.MethodName;
        if (methodName.IsNullOrWhiteSpace())
            methodName = Regex.Replace(source.Options.AssemblyName, "\\W", "");

        // generate registration method
        var result = ServiceRegistrationWriter.GenerateExtensionClass(
            moduleRegistrations,
            serviceRegistrations,
            source.Options.AssemblyName,
            methodName);

        // add source file
        sourceContext.AddSource("Injectio.g.cs", SourceText.From(result, Encoding.UTF8));
    }

    private static void ReportDiagnostic(SourceProductionContext context, EquatableArray<Diagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
            context.ReportDiagnostic(diagnostic);
    }

    private static bool SyntacticPredicate(SyntaxNode syntaxNode, CancellationToken cancellationToken)
    {
        return syntaxNode is ClassDeclarationSyntax { AttributeLists.Count: > 0 } classDeclaration
                   && !classDeclaration.Modifiers.Any(SyntaxKind.AbstractKeyword)
                   && !classDeclaration.Modifiers.Any(SyntaxKind.StaticKeyword)
               || syntaxNode is MemberDeclarationSyntax { AttributeLists.Count: > 0 } memberDeclaration
                   && !memberDeclaration.Modifiers.Any(SyntaxKind.AbstractKeyword);
    }

    private static ServiceRegistrationContext SemanticTransform(GeneratorSyntaxContext context, CancellationToken cancellationToken)
    {
        return context.Node switch
        {
            ClassDeclarationSyntax => SemanticTransformClass(context),
            MethodDeclarationSyntax => SemanticTransformMethod(context),
            _ => null
        };
    }

    private static ServiceRegistrationContext SemanticTransformMethod(GeneratorSyntaxContext context)
    {
        if (context.Node is not MethodDeclarationSyntax methodDeclaration)
            return null;

        var methodSymbol = context.SemanticModel.GetDeclaredSymbol(methodDeclaration);
        if (methodSymbol is null)
            return null;

        // make sure attribute is for registration
        var attributes = methodSymbol.GetAttributes();
        var isKnown = attributes.Any(IsMethodAttribute);
        if (!isKnown)
            return null;

        var (diagnostics, hasServiceCollection, hasTagCollection) = ValidateMethod(methodDeclaration, methodSymbol);
        if (diagnostics.Any())
            return new ServiceRegistrationContext(diagnostics);

        var registration = new ModuleRegistration
        (
            className: methodSymbol.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            methodName: methodSymbol.Name,
            isStatic: methodSymbol.IsStatic,
            hasTagCollection: hasTagCollection
        );

        return new ServiceRegistrationContext(moduleRegistrations: new[] { registration });
    }

    private static ServiceRegistrationContext SemanticTransformClass(GeneratorSyntaxContext context)
    {
        if (context.Node is not ClassDeclarationSyntax classSyntax)
            return null;

        var classSymbol = context.SemanticModel.GetDeclaredSymbol(classSyntax);
        if (classSymbol is null)
            return null;

        var attributes = classSymbol.GetAttributes();

        // support multiple register attributes on a class
        var registrations = attributes
            .Select(attribute => CreateServiceRegistration(classSymbol, attribute))
            .Where(registration => registration != null)
            .ToArray();

        if (registrations.Length == 0)
            return null;

        return new ServiceRegistrationContext(serviceRegistrations: registrations);
    }

    private static (IReadOnlyCollection<Diagnostic> diagnostics, bool hasServiceCollection, bool hasTagCollection) ValidateMethod(MethodDeclarationSyntax methodDeclaration, IMethodSymbol methodSymbol)
    {
        var diagnostics = new List<Diagnostic>();
        var hasServiceCollection = false;
        var hasTagCollection = false;

        var methodName = methodSymbol.Name;

        // validate first parameter should be service collection
        if (methodSymbol.Parameters.Length is 1 or 2)
        {
            var parameterSymbol = methodSymbol.Parameters[0];
            hasServiceCollection = IsServiceCollection(parameterSymbol);
            if (!hasServiceCollection)
            {
                var diagnostic = Diagnostic.Create(
                    DiagnosticDescriptors.InvalidModuleParameter,
                    methodDeclaration.GetLocation(),
                    parameterSymbol.Name,
                    methodName
                );
                diagnostics.Add(diagnostic);
            }
        }

        // validate second parameter should be string collection
        if (methodSymbol.Parameters.Length is 2)
        {
            var parameterSymbol = methodSymbol.Parameters[1];
            hasTagCollection = IsStringCollection(parameterSymbol);
            if (!hasTagCollection)
            {
                var diagnostic = Diagnostic.Create(
                    DiagnosticDescriptors.InvalidModuleParameter,
                    methodDeclaration.GetLocation(),
                    parameterSymbol.Name,
                    methodName
                );
                diagnostics.Add(diagnostic);
            }
        }

        if (methodSymbol.Parameters.Length is 1 or 2)
            return (diagnostics, hasServiceCollection, hasTagCollection);

        // invalid parameter count
        var parameterDiagnostic = Diagnostic.Create(
            DiagnosticDescriptors.InvalidServiceCollectionParameter,
            methodDeclaration.GetLocation(),
            methodName
        );
        diagnostics.Add(parameterDiagnostic);

        return (diagnostics, hasServiceCollection, hasTagCollection);
    }

    private static ServiceRegistration CreateServiceRegistration(INamedTypeSymbol classSymbol, AttributeData attribute)
    {
        // check for known attribute
        if (!IsKnownAttribute(attribute, out var serviceLifetime))
            return null;

        // defaults
        var serviceTypes = new HashSet<string>();
        string implementationType = null;
        string implementationFactory = null;
        DuplicateStrategy? duplicateStrategy = null;
        RegistrationStrategy? registrationStrategy = null;
        var tags = new HashSet<string>();
        string serviceKey = null;

        var attributeClass = attribute.AttributeClass;
        if (attributeClass is { IsGenericType: true } && attributeClass.TypeArguments.Length == attributeClass.TypeParameters.Length)
        {
            // if generic attribute, get service and implementation from generic type parameters
            for (var index = 0; index < attributeClass.TypeParameters.Length; index++)
            {
                var typeParameter = attributeClass.TypeParameters[index];
                var typeArgument = attributeClass.TypeArguments[index];

                if (typeParameter.Name == "TService")
                {
                    var service = typeArgument.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    serviceTypes.Add(service);
                }
                else if (typeParameter.Name == "TImplementation")
                {
                    implementationType = typeArgument.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                }
            }
        }

        foreach (var parameter in attribute.NamedArguments)
        {
            // match name with service registration configuration
            var name = parameter.Key;
            var value = parameter.Value.Value;

            if (string.IsNullOrEmpty(name) || value == null)
                continue;

            switch (name)
            {
                case "ServiceType":
                    serviceTypes.Add(value.ToString());
                    break;
                case "ImplementationType":
                    implementationType = value.ToString();
                    break;
                case "Factory":
                    implementationFactory = value.ToString();
                    break;
                case "Key":
                    serviceKey = value.ToString();
                    break;
                case "Duplicate":
                    duplicateStrategy = ParseEnum<DuplicateStrategy>(value);
                    break;
                case "Registration":
                    registrationStrategy = ParseEnum<RegistrationStrategy>(value);
                    break;
                case "Tags":
                    var tagsItems = value
                        .ToString()
                        .Split(',', ';')
                        .Where(v => v.HasValue());

                    foreach (var tagItem in tagsItems)
                        tags.Add(tagItem);

                    break;
            }
        }

        // default to ignore duplicate service registrations
        duplicateStrategy ??= DuplicateStrategy.Skip;

        // if implementation and service types not set, default to self with interfaces
        if (registrationStrategy == null
            && implementationType == null
            && serviceTypes.Count == 0)
        {
            registrationStrategy = RegistrationStrategy.SelfWithInterfaces;
        }

        // no implementation type set, use class attribute is on
        if (implementationType.IsNullOrWhiteSpace())
        {
            implementationType = classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }

        // add implemented interfaces
        bool includeInterfaces = registrationStrategy is RegistrationStrategy.ImplementedInterfaces or RegistrationStrategy.SelfWithInterfaces;
        if (includeInterfaces)
        {
            foreach (var implementedInterface in classSymbol.AllInterfaces)
            {
                var interfaceName = implementedInterface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                serviceTypes.Add(interfaceName);
            }
        }

        // add class attribute is on; default service type if not set
        bool includeSelf = registrationStrategy is RegistrationStrategy.Self or RegistrationStrategy.SelfWithInterfaces;
        if (includeSelf || serviceTypes.Count == 0)
            serviceTypes.Add(implementationType);

        return new ServiceRegistration(
            serviceLifetime,
            implementationType,
            serviceTypes,
            implementationFactory,
            duplicateStrategy ?? DuplicateStrategy.Skip,
            registrationStrategy ?? RegistrationStrategy.SelfWithInterfaces,
            tags,
            serviceKey);
    }

    private static TEnum? ParseEnum<TEnum>(object value) where TEnum : struct
    {
        return value switch
        {
            int numberValue => Enum.IsDefined(typeof(TEnum), numberValue) ? (TEnum)Enum.ToObject(typeof(TEnum), numberValue) : null,
            string stringValue => Enum.TryParse<TEnum>(stringValue, out var strategy) ? strategy : null,
            _ => null
        };
    }

    private static bool IsKnownAttribute(AttributeData attribute, out string serviceLifetime)
    {
        if (IsSingletonAttribute(attribute))
        {
            serviceLifetime = "Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton";
            return true;
        }

        if (IsScopedAttribute(attribute))
        {
            serviceLifetime = "Microsoft.Extensions.DependencyInjection.ServiceLifetime.Scoped";
            return true;
        }

        if (IsTransientAttribute(attribute))
        {
            serviceLifetime = "Microsoft.Extensions.DependencyInjection.ServiceLifetime.Transient";
            return true;
        }

        serviceLifetime = "Microsoft.Extensions.DependencyInjection.ServiceLifetime.Transient";
        return false;
    }

    private static bool IsTransientAttribute(AttributeData attribute)
    {
        return attribute?.AttributeClass is
        {
            Name: KnownTypes.TransientAttributeShortName or KnownTypes.TransientAttributeTypeName,
            ContainingNamespace:
            {
                Name: "Attributes",
                ContainingNamespace:
                {
                    Name: "Injectio"
                }
            }
        };
    }

    private static bool IsSingletonAttribute(AttributeData attribute)
    {
        return attribute?.AttributeClass is
        {
            Name: KnownTypes.SingletonAttributeShortName or KnownTypes.SingletonAttributeTypeName,
            ContainingNamespace:
            {
                Name: "Attributes",
                ContainingNamespace:
                {
                    Name: "Injectio"
                }
            }
        };
    }

    private static bool IsScopedAttribute(AttributeData attribute)
    {
        return attribute?.AttributeClass is
        {
            Name: KnownTypes.ScopedAttributeShortName or KnownTypes.ScopedAttributeTypeName,
            ContainingNamespace:
            {
                Name: "Attributes",
                ContainingNamespace:
                {
                    Name: "Injectio"
                }
            }
        };
    }

    private static bool IsMethodAttribute(AttributeData attribute)
    {
        return attribute?.AttributeClass is
        {
            Name: KnownTypes.ModuleAttributeShortName or KnownTypes.ModuleAttributeTypeName,
            ContainingNamespace:
            {
                Name: "Attributes",
                ContainingNamespace:
                {
                    Name: "Injectio"
                }
            }
        };
    }

    private static bool IsServiceCollection(IParameterSymbol parameterSymbol)
    {
        return parameterSymbol?.Type is
        {
            Name: "IServiceCollection" or "ServiceCollection",
            ContainingNamespace:
            {
                Name: "DependencyInjection",
                ContainingNamespace:
                {
                    Name: "Extensions",
                    ContainingNamespace:
                    {
                        Name: "Microsoft"
                    }
                }
            }
        };
    }

    private static bool IsStringCollection(IParameterSymbol parameterSymbol)
    {
        var type = parameterSymbol?.Type as INamedTypeSymbol;

        return type is
        {
            Name: "IEnumerable" or "IReadOnlySet" or "IReadOnlyCollection" or "ICollection" or "ISet" or "HashSet",
            IsGenericType: true,
            TypeArguments.Length: 1,
            TypeParameters.Length: 1,
            ContainingNamespace:
            {
                Name: "Generic",
                ContainingNamespace:
                {
                    Name: "Collections",
                    ContainingNamespace:
                    {
                        Name: "System"
                    }
                }
            }
        };
    }
}
