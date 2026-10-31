// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using RabbitMcp.Server.Contracts;

namespace RabbitMcp.Server.Cli;

/// <summary>
/// <c>rabbit-mcp contracts diff OLD.dll NEW.dll [--json]</c>: wire-compatibility check between two builds of a
/// contracts assembly, for CI. Exit code 0 = compatible, 1 = breaking changes, 2 = usage or load error.
/// </summary>
public static class ContractsCommand
{
    public const string Usage =
        "usage: rabbit-mcp contracts diff <old> <new> [--json]\n" +
        "         Compares two versions of the contracts (a [MessageContract] DLL or an AsyncAPI .yaml/.json file).\n" +
        "         Exit code: 0 compatible, 1 breaking changes, 2 error.\n" +
        "       rabbit-mcp contracts asyncapi <contracts> [--title NAME]\n" +
        "         Writes the contracts as an AsyncAPI 3.0 document (JSON) to stdout.";

    /// <summary>rabbit-mcp --help: without it the bare binary only starts the server, which waits silently for a client.</summary>
    public static string ServerUsage { get; } =
        "rabbit-mcp: MCP server for RabbitMQ, built on message contracts.\n\n" +
        "usage: rabbit-mcp\n" +
        "         Runs the MCP server, normally started by an MCP client (stdio; RabbitMcp__Transport=http serves HTTP).\n" +
        "         Options are RabbitMcp__* environment variables: https://github.com/lazogio/rabbit-mcp#configuration\n" +
        "       rabbit-mcp --version\n" +
        "       " + Usage["usage: ".Length..];

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args is ["asyncapi", .. var rest])
            return Export(rest, output, error);

        var json = args.Contains("--json");
        var positional = args.Where(a => a != "--json").ToArray();
        if (positional is not ["diff", var oldPath, var newPath])
        {
            error.WriteLine(Usage);
            return 2;
        }

        IReadOnlyList<ContractChange> changes;
        try
        {
            changes = ContractDiff.Compare(Load(oldPath), Load(newPath));
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or FileLoadException
                                       or BadImageFormatException or ReflectionTypeLoadException)
        {
            error.WriteLine($"rabbit-mcp: {ContractAssemblyLoader.Describe(ex)}");
            return 2;
        }

        var breaking = changes.Count(c => c.Severity == ContractDiff.Breaking);
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(new { compatible = breaking == 0, changes },
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        }
        else
        {
            foreach (var c in changes.OrderBy(c => Rank(c.Severity)).ThenBy(c => c.Contract).ThenBy(c => c.Path))
                output.WriteLine($"{c.Severity.ToUpperInvariant(),-8} {c.Contract}{(c.Path.Length > 0 ? " " + c.Path : "")}: {c.Message}");
            output.WriteLine(changes.Count == 0
                ? "No contract changes."
                : $"{breaking} breaking, {changes.Count(c => c.Severity == ContractDiff.Warning)} warnings, " +
                  $"{changes.Count(c => c.Severity == ContractDiff.Info)} compatible changes.");
        }
        return breaking == 0 ? 0 : 1;
    }

    private static int Rank(string severity) => severity switch
    {
        ContractDiff.Breaking => 0,
        ContractDiff.Warning => 1,
        _ => 2,
    };

    private static int Export(string[] args, TextWriter output, TextWriter error)
    {
        var title = "Contracts";
        var paths = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--title" && i + 1 < args.Length)
                title = args[++i];
            else
                paths.Add(args[i]);
        }
        if (paths.Count != 1)
        {
            error.WriteLine(Usage);
            return 2;
        }
        try
        {
            var document = AsyncApiExporter.Export(Load(paths[0]), title);
            output.WriteLine(document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or FileLoadException
                                       or BadImageFormatException or ReflectionTypeLoadException)
        {
            error.WriteLine($"rabbit-mcp: {ContractAssemblyLoader.Describe(ex)}");
            return 2;
        }
    }

    /// <summary>
    /// An AsyncAPI file (.yaml/.yml/.json), or a contracts DLL — each build in its own load context, so two versions of
    /// the same assembly can be compared.
    /// </summary>
    public static ContractRegistry Load(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new InvalidOperationException($"'{full}' does not exist.");
        if (Path.GetExtension(full).ToLowerInvariant() is ".yaml" or ".yml" or ".json")
            return new ContractRegistry([], AsyncApiLoader.Load(full));
        var assembly = new IsolatedContractContext(full).LoadFromAssemblyPath(full);
        return ContractRegistry.FromAssemblies(assembly);
    }

    private sealed class IsolatedContractContext(string path) : AssemblyLoadContext($"contracts:{path}", isCollectible: true)
    {
        private readonly string _directory = Path.GetDirectoryName(path)!;
        private static readonly string SharedContracts = typeof(RabbitMcp.Contracts.MessageContractAttribute).Assembly.GetName().Name!;

        protected override Assembly? Load(AssemblyName name)
        {
            // [MessageContract] must be the server's own type; framework assemblies come from the default context.
            if (name.Name == SharedContracts)
                return null;
            var candidate = Path.Combine(_directory, name.Name + ".dll");
            return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
        }
    }
}
