using System.Reflection;
using System.Runtime.Loader;

namespace EfSchemaDiff.Infrastructure.Loading;

/// <summary>
/// An isolated <see cref="AssemblyLoadContext"/> used to load a user's assembly without
/// conflicting with the tool's own assemblies. BCL, EF Core, Microsoft.Extensions.*,
/// and EfSchemaDiff.* are shared from the default context so that EF Core types
/// (IModel, IEntityType, …) remain compatible across the boundary.
/// </summary>
internal sealed class EfSchemaDiffLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    // Prefixes that should always resolve from the tool's default (shared) context.
    private static readonly string[] SharedPrefixes =
    [
        "EfSchemaDiff.",
        "Microsoft.EntityFrameworkCore",
        "Microsoft.Extensions.",
        "Microsoft.Bcl.",
        "Microsoft.CSharp",
        "Microsoft.Win32.",
        "System.",
        "netstandard",
        "mscorlib",
        "WindowsBase",
    ];

    public EfSchemaDiffLoadContext(string assemblyPath) : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(assemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (name is null) return null;

        // Defer shared assemblies to the default (tool) context.
        foreach (var prefix in SharedPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return null;
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
