using Injectio.Attributes;
using Injectio.Generators.Extensions;

#pragma warning disable CS8604

namespace Injectio.Generators;

public static class ServiceRegistrationWriter
{
#if DEBUG
    private static int _counter = 0;
#endif

    public static string GenerateExtensionClass(
        IReadOnlyList<ModuleRegistration> moduleRegistrations,
        IReadOnlyList<ServiceRegistration> serviceRegistrations,
        string assemblyName,
        string methodName,
        bool skipVersion = false)
    {
        var codeBuilder = new IndentedStringBuilder();
        codeBuilder
            .AppendLine("// <auto-generated />")
            .AppendLine("#nullable enable")
            .AppendLine();

#if DEBUG
        // used to track re-writes for performance
        codeBuilder.AppendLine($"// Counter: {Interlocked.Increment(ref _counter)}");
#endif

        codeBuilder
            .AppendLine("namespace Microsoft.Extensions.DependencyInjection")
            .AppendLine("{")
            .IncrementIndent()
            .AppendLine("/// <summary>")
            .AppendLine("/// Extension methods for discovered service registrations")
            .AppendLine("/// </summary>");

        if (!skipVersion)
        {
            codeBuilder
                .Append("[global::System.CodeDom.Compiler.GeneratedCode(\"")
                .Append("Injectio.Generators")
                .Append("\", \"")
                .Append("1.0.0.0")
                .AppendLine("\")]");
        }

        codeBuilder
            .AppendLine("[global::System.Diagnostics.DebuggerNonUserCodeAttribute]")
            .AppendLine("[global::System.Diagnostics.DebuggerStepThroughAttribute]")
            .AppendLine("public static class DiscoveredServicesExtensions")
            .AppendLine("{")
            .IncrementIndent()
            .AppendLine("/// <summary>")
            .AppendLine($"/// Adds discovered services from {assemblyName} to the specified service collection")
            .AppendLine("/// </summary>")
            .AppendLine("/// <param name=\"serviceCollection\">The service collection.</param>")
            .AppendLine("/// <param name=\"tags\">The service registration tags to include.</param>")
            .AppendLine("/// <returns>The service collection</returns>")
            .Append("public static global::Microsoft.Extensions.DependencyInjection.IServiceCollection")
            .Append(" Add")
            .Append(methodName)
            .AppendLine("(this global::Microsoft.Extensions.DependencyInjection.IServiceCollection serviceCollection, params string[]? tags)")
            .AppendLine("{")
            .IncrementIndent();


        codeBuilder
            .AppendLine("var tagSet = new global::System.Collections.Generic.HashSet<string>(tags ?? global::System.Linq.Enumerable.Empty<string>());")
            .AppendLine();

        var moduleCount = 1;

        foreach (var moduleRegistration in moduleRegistrations)
        {
            moduleCount = WriteModule(codeBuilder, moduleRegistration, moduleCount);
        }

        foreach (var serviceRegistration in serviceRegistrations)
        {
            WriteRegistration(codeBuilder, serviceRegistration);
        }

        codeBuilder
            .AppendLine("return serviceCollection;")
            .DecrementIndent()
            .AppendLine("}") // method
            .DecrementIndent()
            .AppendLine("}") // class
            .DecrementIndent()
            .AppendLine("}"); // namespace

        return codeBuilder.ToString();
    }

    private static int WriteModule(
        IndentedStringBuilder codeBuilder,
        ModuleRegistration moduleRegistration,
        int moduleCount)
    {
        if (moduleRegistration.IsStatic)
        {
            codeBuilder
                .AppendIf("global::", !moduleRegistration.ClassName.StartsWith("global::"))
                .Append(moduleRegistration.ClassName)
                .Append('.')
                .Append(moduleRegistration.MethodName)
                .Append("(")
                .Append("serviceCollection")
                .AppendIf(", tagSet", moduleRegistration.HasTagCollection)
                .Append(");")
                .AppendLine()
                .AppendLine();
        }
        else
        {
            codeBuilder
                .Append("var module")
                .Append($"{moduleCount:0000}")
                .Append(" = new ")
                .AppendIf("global::", !moduleRegistration.ClassName.StartsWith("global::"))
                .Append(moduleRegistration.ClassName)
                .AppendLine("();");

            codeBuilder
                .Append("module")
                .Append($"{moduleCount:0000}")
                .Append('.')
                .Append(moduleRegistration.MethodName)
                .Append("(")
                .Append("serviceCollection")
                .AppendIf(", tagSet", moduleRegistration.HasTagCollection)
                .Append(");")
                .AppendLine()
                .AppendLine();

            moduleCount++;
        }

        return moduleCount;
    }

    private static void WriteRegistration(
        IndentedStringBuilder codeBuilder,
        ServiceRegistration serviceRegistration)
    {
        if (serviceRegistration.Tags.Count > 0)
        {
            codeBuilder
                .Append("if (tagSet.Count == 0 || tagSet.Intersect(new[] { ");

            bool wroteTag = false;
            foreach (var tag in serviceRegistration.Tags)
            {
                if (wroteTag)
                    codeBuilder.Append(", ");

                codeBuilder
                    .Append("\"")
                    .Append(tag)
                    .Append("\"");

                wroteTag = true;
            }

            codeBuilder
                .AppendLine(" }).Any())")
                .AppendLine("{")
                .IncrementIndent();
        }

        var serviceMethod = GetServiceCollectionMethod(serviceRegistration.Duplicate);
        var describeMethod = GetDescribeMethod(serviceRegistration.ServiceKey);

        foreach (var serviceType in serviceRegistration.ServiceTypes)
        {
            if (serviceType.IsNullOrWhiteSpace())
                continue;

            WriteServiceType(codeBuilder, serviceRegistration, serviceMethod, describeMethod, serviceType);
        }

        if (serviceRegistration.Tags.Count > 0)
        {
            codeBuilder
                .DecrementIndent()
                .AppendLine("}")
                .AppendLine();
        }
    }

    private static void WriteServiceType(
        IndentedStringBuilder codeBuilder,
        ServiceRegistration serviceRegistration,
        string serviceMethod,
        string describeMethod,
        string serviceType)
    {
        codeBuilder
            .Append("global::Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.")
            .Append(serviceMethod)
            .AppendLine("(")
            .IncrementIndent()
            .AppendLine("serviceCollection,")
            .Append("global::Microsoft.Extensions.DependencyInjection.ServiceDescriptor.")
            .Append(describeMethod)
            .AppendLine("(")
            .IncrementIndent()
            .Append("typeof(")
            .AppendIf("global::", !serviceType.StartsWith("global::"))
            .Append(serviceType)
            .AppendLine("),");

        if (serviceRegistration.ServiceKey.HasValue())
        {
            var anyKey = serviceRegistration.ServiceKey == "*";
            if (anyKey)
            {
                codeBuilder.AppendLine("global::Microsoft.Extensions.DependencyInjection.KeyedService.AnyKey,");
            }
            else
            {
                codeBuilder
                    .Append("\"")
                    .Append(serviceRegistration.ServiceKey)
                    .AppendLine("\",");
            }
        }

        if (serviceRegistration.Factory.HasValue())
        {
            bool hasNamespace = serviceRegistration.Factory?.Contains(".") == true;

            codeBuilder
                .AppendIf(serviceRegistration.ImplementationType, !hasNamespace)
                .AppendIf(".", !hasNamespace)
                .Append(serviceRegistration.Factory);
        }
        else if (serviceRegistration.ImplementationType.HasValue())
        {
            codeBuilder
                .Append("typeof(")
                .AppendIf("global::", !serviceRegistration.ImplementationType.StartsWith("global::"))
                .Append(serviceRegistration.ImplementationType)
                .Append(')');
        }
        else
        {
            codeBuilder
                .Append("typeof(")
                .AppendIf("global::", !serviceType.StartsWith("global::"))
                .Append(serviceType)
                .Append(')');
        }

        codeBuilder
            .AppendLine(", ")
            .Append("global::")
            .Append(serviceRegistration.Lifetime)
            .AppendLine()
            .DecrementIndent()
            .AppendLine(")")
            .DecrementIndent()
            .AppendLine(");")
            .AppendLine();
    }

    public static string GetServiceCollectionMethod(DuplicateStrategy duplicateStrategy)
    {
        return duplicateStrategy switch
        {
            DuplicateStrategy.Skip => "TryAdd",
            DuplicateStrategy.Replace => "Replace",
            DuplicateStrategy.Append => "Add",
            _ => "TryAdd"
        };
    }

    public static string GetDescribeMethod(string serviceKey)
    {
        return serviceKey.HasValue() ? "DescribeKeyed" : "Describe";
    }
}
