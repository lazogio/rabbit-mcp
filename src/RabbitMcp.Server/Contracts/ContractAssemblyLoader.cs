using System.Reflection;
using System.Runtime.Loader;

namespace RabbitMcp.Server.Contracts;

/// <summary>
/// Loads the assemblies that hold [MessageContract] types, as configured in
/// <c>RabbitMcp:ContractAssemblies</c>. Without configuration the built-in sample
/// contracts (RabbitMcp.Contracts) are used, so the server works out of the box.
/// </summary>
public static class ContractAssemblyLoader
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> ProbeDirectories = new(StringComparer.Ordinal);
    private static bool _resolverInstalled;

    /// <summary>
    /// Splits <paramref name="configured"/> (";" or "," separated DLL paths), resolves relative
    /// paths against <paramref name="baseDirectory"/> and loads each assembly into the default
    /// context. Dependencies are probed in each assembly's own folder. Throws a readable
    /// <see cref="InvalidOperationException"/> for missing files or unloadable assemblies.
    /// </summary>
    public static IReadOnlyList<Assembly> Load(string? configured, string baseDirectory, Assembly fallback)
    {
        var paths = ParsePaths(configured, baseDirectory);
        if (paths.Count == 0)
            return new[] { fallback };

        var assemblies = new List<Assembly>(paths.Count);
        foreach (var path in paths)
        {
            if (!File.Exists(path))
                throw new InvalidOperationException(
                    $"RabbitMcp:ContractAssemblies: '{path}' does not exist. Paths are relative to the server's folder ({baseDirectory}).");

            InstallResolver(Path.GetDirectoryName(path)!);
            try
            {
                assemblies.Add(LoadOnce(path));
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException)
            {
                throw new InvalidOperationException(
                    $"RabbitMcp:ContractAssemblies: '{path}' is not a loadable .NET assembly ({ex.Message}).", ex);
            }
        }
        return assemblies;
    }

    public static IReadOnlyList<string> ParsePaths(string? configured, string baseDirectory) =>
        string.IsNullOrWhiteSpace(configured)
            ? Array.Empty<string>()
            : configured
                .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => Path.GetFullPath(Environment.ExpandEnvironmentVariables(p), baseDirectory))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

    /// <summary>Returns the already-loaded assembly with the same identity instead of loading it twice.</summary>
    private static Assembly LoadOnce(string path)
    {
        var name = AssemblyName.GetAssemblyName(path);
        var loaded = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a =>
            AssemblyName.ReferenceMatchesDefinition(name, a.GetName()) &&
            string.Equals(a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase));
        return loaded ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }

    /// <summary>Lets a contracts DLL bring its own dependencies, located next to it.</summary>
    private static void InstallResolver(string directory)
    {
        lock (Gate)
        {
            ProbeDirectories.Add(directory);
            if (_resolverInstalled)
                return;
            _resolverInstalled = true;
        }

        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string[] directories;
            lock (Gate)
                directories = [.. ProbeDirectories];

            foreach (var dir in directories)
            {
                var candidate = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(candidate))
                    return context.LoadFromAssemblyPath(candidate);
            }
            return null;
        };
    }
}
