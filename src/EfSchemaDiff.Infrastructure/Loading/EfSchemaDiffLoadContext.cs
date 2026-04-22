using System.Reflection;
using System.Runtime.Loader;

namespace EfSchemaDiff.Infrastructure.Loading;

/// <summary>
/// An isolated <see cref="AssemblyLoadContext"/> used to load a user's assembly without
/// conflicting with the tool's own assemblies. BCL, EF Core, and EfSchemaDiff.* are
/// always shared from the default context so that EF Core types (IModel, IEntityType, …)
/// remain compatible across the boundary.
/// </summary>
/// <remarks>
/// <c>Microsoft.Extensions.*</c> assemblies are shared <em>only if the tool bundles them</em>.
/// User-specific Extension dependencies (e.g. HealthChecks.Abstractions) that the tool does
/// not ship are loaded from the user's own dependency tree instead, avoiding FileNotFoundExceptions.
/// </remarks>
internal sealed class EfSchemaDiffLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _toolDirectory;

    // Prefixes that always resolve from the tool's default (shared) context.
    private static readonly string[] AlwaysSharedPrefixes =
    [
        "EfSchemaDiff.",
        "Microsoft.EntityFrameworkCore",
        "Microsoft.CSharp",
        "System.",
        "netstandard",
        "mscorlib",
        "WindowsBase",
    ];

    // Prefixes that resolve from the tool's default context ONLY when the tool actually
    // bundles the assembly.  Unknown assemblies under these prefixes (e.g. third-party
    // Microsoft.Extensions.* packages the tool never references) fall through to the
    // user's resolver instead of causing a FileNotFoundException.
    private static readonly string[] ConditionallySharedPrefixes =
    [
        "Microsoft.Extensions.",
        "Microsoft.Bcl.",
        "Microsoft.Win32.",
    ];

    public EfSchemaDiffLoadContext(string assemblyPath) : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(assemblyPath);
        _toolDirectory = Path.GetDirectoryName(typeof(EfSchemaDiffLoadContext).Assembly.Location)!;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (name is null) return null;

        // BCL, EF Core, and tool assemblies: always defer to the tool's default context.
        foreach (var prefix in AlwaysSharedPrefixes)
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

        // Microsoft.Extensions.* and similar: defer to the tool's default context only when
        // the tool actually ships that assembly.  Otherwise fall through to the user's resolver
        // so user-specific dependencies (e.g. HealthChecks.Abstractions) load correctly.
        foreach (var prefix in ConditionallySharedPrefixes)
        {
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (File.Exists(Path.Combine(_toolDirectory, $"{name}.dll"))) return null;
            break; // not bundled by tool — resolve from user's dependency tree below
        }

        // Resolve user-app-specific assemblies from the user's dependency tree.
        var resolvedPath = _resolver.ResolveAssemblyToPath(assemblyName);
        return resolvedPath is not null ? LoadFromAssemblyPath(resolvedPath) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is not null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
