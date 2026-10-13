// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

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
    /// Splits <paramref name="configured"/> (";"-separated DLL paths — not ",", which is legal in paths), resolves relative
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
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => Path.GetFullPath(Environment.ExpandEnvironmentVariables(p), baseDirectory))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

    /// <summary>
    /// Loads <paramref name="path"/>, or returns the assembly of the same name that is already loaded when it is
    /// that very file or the shared RabbitMcp.Contracts. A *different* assembly with the name of one the server
    /// already uses (e.g. a contracts project called "RabbitMcp.Server") cannot be loaded next to it — the
    /// default context would silently hand back the server's copy and no contracts would be found.
    /// </summary>
    private static Assembly LoadOnce(string path)
    {
        var name = AssemblyName.GetAssemblyName(path);
        var loaded = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a =>
            string.Equals(a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase));
        if (loaded is null)
            return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);

        // Location is "" for assemblies bundled in a single-file app: those never count as "the same file".
#pragma warning disable IL3000
        var samePath = !string.IsNullOrEmpty(loaded.Location) &&
            string.Equals(Path.GetFullPath(loaded.Location), path,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
#pragma warning restore IL3000
        if (samePath || loaded == SharedContractsAssembly)
            return loaded;

        throw new InvalidOperationException(
            $"RabbitMcp:ContractAssemblies: '{path}' is named '{name.Name}', which the server already uses for its own " +
            "code. Give your contracts assembly a different name.");
    }

    private static readonly Assembly SharedContractsAssembly = typeof(RabbitMcp.Contracts.MessageContractAttribute).Assembly;

    /// <summary>
    /// A readable one-line reason for a failed contract load. ReflectionTypeLoadException's own message
    /// ("Unable to load one or more of the requested types") hides the missing dependency.
    /// </summary>
    public static string Describe(Exception ex) => ex switch
    {
        ReflectionTypeLoadException rtle when rtle.LoaderExceptions.FirstOrDefault(e => e is not null) is { } first =>
            $"Contract types could not be loaded: {first.Message} " +
            "Copy the contracts assembly's dependencies next to it (e.g. with dotnet publish).",
        FileNotFoundException fnf =>
            $"A dependency of the contract assemblies is missing: {fnf.Message} Copy it next to the contracts DLL.",
        _ => ex.Message,
    };

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
