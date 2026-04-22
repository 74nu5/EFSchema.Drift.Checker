using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using EfSchemaDiff.Core.Interfaces;
using EfSchemaDiff.Core.Models;

namespace EfSchemaDiff.Infrastructure.Loading;

/// <summary>
/// Loads a user-provided <see cref="DbContext"/> from an isolated
/// <see cref="EfSchemaDiffLoadContext"/>, extracts the EF Core schema, and returns the
/// neutral <see cref="DatabaseSchema"/> DTO.  The DbContext and the ALC are disposed
/// before returning, so no EF Core objects cross the isolation boundary.
/// </summary>
/// <remarks>
/// EF Core assemblies (<c>Microsoft.EntityFrameworkCore.*</c>) are intentionally shared with
/// the tool's default context so that <see cref="IModel"/>, <see cref="IEntityType"/>, etc.
/// are the same .NET types on both sides, enabling direct use of
/// <see cref="IEfModelExtractor"/> without reflection bridging.
/// </remarks>
public sealed class DbContextLoader : IDbContextLoader
{
    private readonly IEfModelExtractor _extractor;

    public DbContextLoader(IEfModelExtractor extractor)
        => _extractor = extractor;

    public Task<DatabaseSchema> LoadAndExtractAsync(
        string assemblyPath,
        string? startupAssemblyPath,
        string dbContextTypeName,
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        // Run in a thread-pool thread so the caller's synchronisation context is not blocked.
        return Task.Run(() => LoadAndExtract(
            assemblyPath, startupAssemblyPath, dbContextTypeName, connectionString), cancellationToken);
    }

    // -----------------------------------------------------------------------
    // Core loading logic (synchronous, runs on thread-pool)
    // -----------------------------------------------------------------------

    private DatabaseSchema LoadAndExtract(
        string assemblyPath,
        string? startupAssemblyPath,
        string dbContextTypeName,
        string connectionString)
    {
        // Fail fast with a clear message if the target project uses a different EF Core major version.
        CheckEfCoreVersionCompatibility(assemblyPath);

        var alc = new EfSchemaDiffLoadContext(assemblyPath);

        try
        {
            var assembly = alc.LoadFromAssemblyPath(assemblyPath);
            var dbContextType = FindDbContextType(assembly, dbContextTypeName);

            DbContext? dbContext =
                    TryLoadViaDesignTimeFactory(assembly, dbContextType, connectionString)
                 ?? TryLoadViaOptionsConstructor(dbContextType, connectionString)
                 ?? TryLoadViaHostBuilder(alc, startupAssemblyPath, dbContextType, connectionString)
                 ?? TryLoadViaNoArgConstructor(dbContextType);

            if (dbContext is null)
                throw new InvalidOperationException(
                    $"Could not instantiate DbContext '{dbContextType.FullName ?? dbContextType.Name}'. " +
                    "Tried: IDesignTimeDbContextFactory, DbContextOptions constructor, IHostBuilder, " +
                    "and no-arg constructor. Verify the assembly path and DbContext type name.");

            using (dbContext)
                return _extractor.Extract(dbContext);
        }
        catch (InvalidOperationException)
        {
            throw; // version mismatch, "no DbContext found", etc. — already descriptive
        }
        catch (ReflectionTypeLoadException ex)
        {
            var missing = ex.LoaderExceptions
                .OfType<Exception>()
                .Select(ExtractAssemblySimpleName)
                .Distinct()
                .Take(5)
                .ToList();
            var detail = missing.Count > 0
                ? $" Missing: {string.Join(", ", missing)}"
                : string.Empty;
            throw new InvalidOperationException(
                $"Could not load all types from '{Path.GetFileName(assemblyPath)}'.{detail}", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Error loading '{Path.GetFileName(assemblyPath)}': {ex.GetType().Name} — {ex.Message}", ex);
        }
        finally
        {
            alc.Unload();
        }
    }

    private static string ExtractAssemblySimpleName(Exception? ex)
    {
        if (ex?.Message is null) return "?";
        // FileNotFoundException: "Could not load file or assembly 'Name, Version=...'…"
        var start = ex.Message.IndexOf('\'');
        if (start < 0) return ex.Message.Length > 80 ? ex.Message[..80] : ex.Message;
        start++;
        var end = ex.Message.IndexOfAny([',', '\''], start);
        return end > start ? ex.Message[start..end] : ex.Message[start..];
    }

    // -----------------------------------------------------------------------
    // Version compatibility check
    // -----------------------------------------------------------------------

    private static void CheckEfCoreVersionCompatibility(string assemblyPath)
    {
        var resolver = new System.Runtime.Loader.AssemblyDependencyResolver(assemblyPath);
        var resolvedPath = resolver.ResolveAssemblyToPath(new AssemblyName("Microsoft.EntityFrameworkCore"));
        if (resolvedPath is null) return; // EF Core not a direct dep — let it proceed

        var userVersion = AssemblyName.GetAssemblyName(resolvedPath).Version;
        var toolVersion = typeof(DbContext).Assembly.GetName().Version;

        if (userVersion is null || toolVersion is null) return;
        if (userVersion.Major == toolVersion.Major) return;

        throw new InvalidOperationException(
            $"EF Core major version mismatch.\n" +
            $"  Target assembly requires: EF Core {userVersion.Major}.{userVersion.Minor}.{userVersion.Build}\n" +
            $"  ef-schema-diff is built with: EF Core {toolVersion.Major}.{toolVersion.Minor}.{toolVersion.Build}\n\n" +
            $"Ensure the target project uses EF Core {toolVersion.Major}.x.");
    }

    // -----------------------------------------------------------------------
    // Strategy helpers
    // -----------------------------------------------------------------------

    private static Type FindDbContextType(Assembly assembly, string typeName)
    {
        // GetTypes() can throw ReflectionTypeLoadException if some types fail to load
        // due to optional/unresolved dependencies. We collect the types that did load.
        Type[] allTypes;
        try
        {
            allTypes = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            allTypes = ex.Types.OfType<Type>().ToArray();
        }

        var candidates = allTypes
            .Where(t => !t.IsAbstract && typeof(DbContext).IsAssignableFrom(t))
            .ToList();

        if (candidates.Count == 0)
            throw new InvalidOperationException(
                $"No concrete DbContext found in '{assembly.Location}'.");

        if (string.IsNullOrWhiteSpace(typeName))
        {
            if (candidates.Count == 1) return candidates[0];
            throw new InvalidOperationException(
                $"Multiple DbContext types found in '{assembly.Location}'. " +
                $"Specify one via --context: {string.Join(", ", candidates.Select(t => t.Name))}");
        }

        var match = candidates.FirstOrDefault(t =>
            t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase) ||
            t.FullName?.Equals(typeName, StringComparison.OrdinalIgnoreCase) == true);

        return match ?? throw new InvalidOperationException(
            $"DbContext '{typeName}' not found in '{assembly.Location}'. " +
            $"Available: {string.Join(", ", candidates.Select(t => t.Name))}");
    }

    /// <summary>Strategy 1 — <c>IDesignTimeDbContextFactory&lt;T&gt;</c>.</summary>
    private static DbContext? TryLoadViaDesignTimeFactory(
        Assembly assembly, Type dbContextType, string connectionString)
    {
        try
        {
            var factoryInterface = typeof(IDesignTimeDbContextFactory<>).MakeGenericType(dbContextType);

            var factoryType = assembly
                             .GetTypes()
                             .FirstOrDefault(t => !t.IsAbstract && factoryInterface.IsAssignableFrom(t));

            if (factoryType is null) return null;

            var factory = Activator.CreateInstance(factoryType);
            if (factory is null) return null;

            var createMethod = factoryInterface.GetMethod("CreateDbContext");
            if (createMethod is null) return null;

            // Pass the connection string as a command-line arg; some factories consume it.
            var args = new[] { "--connection", connectionString };
            return createMethod.Invoke(factory, new object[] { args }) as DbContext;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Strategy 2 — constructor accepting <c>DbContextOptions</c>.</summary>
    private static DbContext? TryLoadViaOptionsConstructor(Type dbContextType, string connectionString)
    {
        try
        {
            var optionsBuilderType = typeof(DbContextOptionsBuilder<>).MakeGenericType(dbContextType);
            var optionsBuilder = (DbContextOptionsBuilder)Activator.CreateInstance(optionsBuilderType)!;
            optionsBuilder.UseSqlServer(connectionString);

            // Try the specific constructor first, then the base-typed one.
            var ctor =
                dbContextType.GetConstructor([typeof(DbContextOptions<>).MakeGenericType(dbContextType)])
                ?? dbContextType.GetConstructor([typeof(DbContextOptions)]);

            if (ctor is null) return null;

            return (DbContext?)Activator.CreateInstance(dbContextType, optionsBuilder.Options);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Strategy 3 — classic <c>CreateHostBuilder(string[])</c> entry point.
    /// Resolves the DbContext from the built service container without starting the host.
    /// </summary>
    private static DbContext? TryLoadViaHostBuilder(
        EfSchemaDiffLoadContext alc,
        string? startupAssemblyPath,
        Type dbContextType,
        string connectionString)
    {
        if (startupAssemblyPath is null) return null;

        try
        {
            var startupAssembly = alc.LoadFromAssemblyPath(startupAssemblyPath);

            var programType = startupAssembly.GetTypes()
                .FirstOrDefault(t => t.Name == "Program");

            if (programType is null) return null;

            var createHostBuilder = programType.GetMethod(
                "CreateHostBuilder",
                BindingFlags.Public | BindingFlags.Static,
                [typeof(string[])]);

            if (createHostBuilder is null) return null;

            if (createHostBuilder.Invoke(null, new object[] { Array.Empty<string>() })
                is not IHostBuilder hostBuilder)
                return null;

            // Inject the connection string via in-memory configuration so the startup
            // code can bind it regardless of the key it uses.
            hostBuilder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = connectionString,
                    [$"ConnectionStrings:{dbContextType.Name.Replace("Context", "")}"] = connectionString,
                    ["ConnectionStrings:DefaultConnection"] = connectionString,
                }));

            // Build the host but do NOT start it; we only need the service container.
            var host = hostBuilder.Build();
            using var scope = host.Services.CreateScope();
            return scope.ServiceProvider.GetService(dbContextType) as DbContext;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Strategy 4 — no-arg constructor fallback.
    /// Works for contexts that fully configure themselves in <c>OnConfiguring</c>.
    /// </summary>
    private static DbContext? TryLoadViaNoArgConstructor(Type dbContextType)
    {
        var ctor = dbContextType.GetConstructor(Type.EmptyTypes);
        if (ctor is null) return null;

        try
        {
            return (DbContext?)Activator.CreateInstance(dbContextType);
        }
        catch
        {
            return null;
        }
    }
}
