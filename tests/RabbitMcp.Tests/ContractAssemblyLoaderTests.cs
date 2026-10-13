// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using RabbitMcp.Contracts.Orders;
using RabbitMcp.Server.Contracts;

namespace RabbitMcp.Tests;

public class ContractAssemblyLoaderTests
{
    /// <summary>tests/RabbitMcp.ExternalContracts/bin/{Configuration}/{tfm}/RabbitMcp.ExternalContracts.dll</summary>
    internal static string ExternalContractsDll()
    {
        // AppContext.BaseDirectory = tests/RabbitMcp.Tests/bin/{Configuration}/{tfm}/
        var tfmDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var configDir = tfmDir.Parent!;
        var testsDir = configDir.Parent!.Parent!.Parent!;
        var path = Path.Combine(testsDir.FullName, "RabbitMcp.ExternalContracts", "bin", configDir.Name, tfmDir.Name,
            "RabbitMcp.ExternalContracts.dll");
        Assert.True(File.Exists(path), $"External contracts were not built: {path}");
        return path;
    }

    [Fact]
    public void Nothing_configured_uses_the_built_in_samples()
    {
        var assemblies = ContractAssemblyLoader.Load(null, AppContext.BaseDirectory, typeof(OrderSubmitted).Assembly);
        Assert.Same(typeof(OrderSubmitted).Assembly, Assert.Single(assemblies));
    }

    [Fact]
    public void Paths_are_split_resolved_against_the_base_directory_and_deduplicated()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "rabbit-mcp-base");
        var paths = ContractAssemblyLoader.ParsePaths(" a.dll ; sub/b.dll; a.dll ;; ", baseDir);

        Assert.Equal(new[] { Path.Combine(baseDir, "a.dll"), Path.Combine(baseDir, "sub", "b.dll") }, paths);
        Assert.Empty(ContractAssemblyLoader.ParsePaths("  ", baseDir));
    }

    [Fact]
    public void Comma_is_part_of_a_path_not_a_separator()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "rabbit-mcp-base");
        var path = Assert.Single(ContractAssemblyLoader.ParsePaths("Doe, John/contracts.dll", baseDir));
        Assert.Equal(Path.Combine(baseDir, "Doe, John", "contracts.dll"), path);
    }

    private static string CopyToTemp(string sourcePath)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rabbit-mcp-copy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, Path.GetFileName(sourcePath));
        File.Copy(sourcePath, target);
        return target;
    }

    [Fact]
    public void A_different_file_named_like_a_server_assembly_is_rejected()
    {
        // e.g. a contracts project accidentally called "RabbitMcp.Server": the default context would
        // silently return the server's copy and no contracts would be found.
        var copy = CopyToTemp(typeof(ContractAssemblyLoader).Assembly.Location);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ContractAssemblyLoader.Load(copy, AppContext.BaseDirectory, typeof(OrderSubmitted).Assembly));

        Assert.Contains("already uses", ex.Message);
    }

    [Fact]
    public void A_copy_of_the_shared_contracts_assembly_resolves_to_the_bundled_one()
    {
        var copy = CopyToTemp(typeof(OrderSubmitted).Assembly.Location);

        var assemblies = ContractAssemblyLoader.Load(copy, AppContext.BaseDirectory, typeof(OrderSubmitted).Assembly);

        Assert.Same(typeof(OrderSubmitted).Assembly, Assert.Single(assemblies));
    }

    [Fact]
    public void Type_load_failures_name_the_missing_dependency()
    {
        var missing = new FileNotFoundException("Could not load file or assembly 'Acme.Money, Version=2.0.0.0'.");
        var rtle = new System.Reflection.ReflectionTypeLoadException([null], [missing]);

        var message = ContractAssemblyLoader.Describe(rtle);

        Assert.Contains("Acme.Money", message);
        Assert.Contains("next to it", message);
        Assert.Equal("plain", ContractAssemblyLoader.Describe(new InvalidOperationException("plain")));
    }

    [Fact]
    public void External_assembly_contracts_become_tools_with_their_own_namespace()
    {
        var dll = ExternalContractsDll();

        var assemblies = ContractAssemblyLoader.Load(dll, AppContext.BaseDirectory, typeof(OrderSubmitted).Assembly);
        var registry = ContractRegistry.FromAssemblies(assemblies.ToArray());

        Assert.Equal(new[] { "GetInvoice", "InvoiceIssued" }, registry.Contracts.Select(c => c.Name));
        var invoice = registry.FindByName("publish_invoice_issued")!;
        Assert.Equal("urn:message:Billing.Contracts:InvoiceIssued", invoice.MessageTypeUrn);
        Assert.Equal(2, invoice.Version);
        Assert.Equal(2, registry.FindByName("request_get_invoice")!.Responses.Count);
        Assert.Null(registry.FindByName("OrderSubmitted")); // samples are not mixed in
    }

    [Fact]
    public void Loading_the_same_assembly_twice_returns_the_same_instance()
    {
        var dll = ExternalContractsDll();
        var first = ContractAssemblyLoader.Load(dll, AppContext.BaseDirectory, typeof(OrderSubmitted).Assembly);
        var second = ContractAssemblyLoader.Load(dll, AppContext.BaseDirectory, typeof(OrderSubmitted).Assembly);
        Assert.Same(first[0], second[0]);
    }

    [Fact]
    public void Missing_file_is_a_readable_configuration_error()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ContractAssemblyLoader.Load("does-not-exist.dll", AppContext.BaseDirectory, typeof(OrderSubmitted).Assembly));
        Assert.Contains("does-not-exist.dll", ex.Message);
        Assert.Contains("does not exist", ex.Message);
    }

    [Fact]
    public void Non_assembly_file_is_a_readable_configuration_error()
    {
        var path = Path.Combine(Path.GetTempPath(), $"not-an-assembly-{Guid.NewGuid():N}.dll");
        File.WriteAllText(path, "this is not a PE file");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                ContractAssemblyLoader.Load(path, AppContext.BaseDirectory, typeof(OrderSubmitted).Assembly));
            Assert.Contains("not a loadable .NET assembly", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
