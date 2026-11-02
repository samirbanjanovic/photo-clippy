using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using PhotoClippy.Contracts;

namespace PhotoClippy.Core.Plugins;

public enum PluginSource
{
    BuiltIn,
    Assembly,
    External,
}

public sealed record PluginDescriptor(IPluginFactory Factory, PluginSource Source, string? Location);

/// <summary>
/// Available plugin factories: built-ins supplied by the host plus public parameterless <see cref="IPluginFactory"/>
/// types found in &lt;plugins-dir&gt;/&lt;name&gt;/*.dll (each folder gets its own AssemblyLoadContext).
/// </summary>
public sealed class PluginCatalog
{
    private readonly Dictionary<string, PluginDescriptor> _factories = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _loadErrors = [];

    public IReadOnlyCollection<PluginDescriptor> Factories => _factories.Values;
    public IReadOnlyList<string> LoadErrors => _loadErrors;

    public PluginCatalog Add(IPluginFactory factory, PluginSource source = PluginSource.BuiltIn, string? location = null)
    {
        _factories[factory.Id] = new PluginDescriptor(factory, source, location);
        return this;
    }

    public PluginCatalog AddRange(IEnumerable<IPluginFactory> factories)
    {
        foreach (var factory in factories)
        {
            Add(factory);
        }

        return this;
    }

    public bool TryGet(string id, out PluginDescriptor descriptor) => _factories.TryGetValue(id, out descriptor!);

    /// <summary>Finds public parameterless IPluginFactory implementations in an assembly (used for built-ins too).</summary>
    public static IEnumerable<IPluginFactory> DiscoverFactories(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = [.. ex.Types.Where(t => t is not null).Cast<Type>()];
        }

        foreach (var type in types)
        {
            if (type is { IsAbstract: false, IsInterface: false } && typeof(IPluginFactory).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) is not null)
            {
                yield return (IPluginFactory)Activator.CreateInstance(type)!;
            }
        }
    }

    public PluginCatalog LoadDirectory(string directory, ILogger? logger = null)
    {
        if (!Directory.Exists(directory))
        {
            return this;
        }

        foreach (var pluginDir in Directory.EnumerateDirectories(directory))
        {
            // Main assemblies are those with a .deps.json (dotnet publish output); otherwise try every assembly.
            var mains = Directory.EnumerateFiles(pluginDir, "*.deps.json")
                .Select(d => Path.Combine(pluginDir, Path.GetFileName(d)[..^".deps.json".Length] + ".dll"))
                .Where(File.Exists)
                .ToList();
            if (mains.Count == 0)
            {
                mains = [.. Directory.EnumerateFiles(pluginDir, "*.dll").Where(d => !PluginLoadContext.IsShared(Path.GetFileNameWithoutExtension(d)))];
            }

            if (mains.Count == 0)
            {
                continue;
            }

            var context = new PluginLoadContext(mains[0]);
            foreach (var dll in mains)
            {
                try
                {
                    var assembly = context.LoadFromAssemblyPath(dll);
                    foreach (var factory in DiscoverFactories(assembly))
                    {
                        Add(factory, PluginSource.Assembly, dll);
                        logger?.LogInformation("Loaded plugin {Id} from {Path}", factory.Id, dll);
                    }
                }
                catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException or TypeLoadException or ReflectionTypeLoadException or InvalidCastException)
                {
                    _loadErrors.Add($"{dll}: {ex.Message}");
                    logger?.LogWarning("Could not load plugin assembly {Path}: {Message}", dll, ex.Message);
                }
            }
        }

        return this;
    }
}

/// <summary>Isolates a plugin's dependencies while sharing the contract assemblies with the host.</summary>
internal sealed class PluginLoadContext(string pluginPath) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(pluginPath), isCollectible: false)
{
    private static readonly HashSet<string> Shared = new(StringComparer.OrdinalIgnoreCase)
    {
        "PhotoClippy.Contracts",
        "Microsoft.Extensions.Logging.Abstractions",
        "Microsoft.Extensions.DependencyInjection.Abstractions",
    };

    private readonly AssemblyDependencyResolver _resolver = new(pluginPath);

    public static bool IsShared(string assemblyName) => Shared.Contains(assemblyName);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is { } name && Shared.Contains(name))
        {
            return null;
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
