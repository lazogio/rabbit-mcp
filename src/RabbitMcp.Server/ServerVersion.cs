// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Reflection;

namespace RabbitMcp.Server;

/// <summary>Version reported to MCP clients: the assembly's informational version without build metadata.</summary>
public static class ServerVersion
{
    public static string Current { get; } = FromInformational(
        typeof(ServerVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>"1.2.3-rc.1+abc123" -> "1.2.3-rc.1"; null/empty -> "0.0.0".</summary>
    public static string FromInformational(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational))
            return "0.0.0";
        var plus = informational.IndexOf('+');
        return plus >= 0 ? informational[..plus] : informational;
    }
}
