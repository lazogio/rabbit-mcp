// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Security.Claims;
using System.Text.RegularExpressions;
using RabbitMcp.Server.Contracts;
using RabbitMcp.Server.Hosting;

namespace RabbitMcp.Server.Security;

/// <summary>What a caller may do. Each tool needs exactly one of these (see <see cref="ToolAccess"/>).</summary>
public enum Permission
{
    /// <summary>list_contracts, validate_message, queue_stats, peek_messages, list_queues, contract_consumers.</summary>
    Read,
    /// <summary>publish_* (events); may be limited to contracts.</summary>
    Publish,
    /// <summary>request_* (request/response); may be limited to contracts.</summary>
    Request,
    /// <summary>republish_from_error and dead_letter_from_error; may be limited to error queues.</summary>
    Republish,
    /// <summary>pending_requests, reply_*, reject_request; may be limited to request contracts.</summary>
    Respond,
}

/// <summary>One permission of a role, optionally limited to subjects matching <c>*</c> patterns (empty: everything).</summary>
public sealed record Grant(Permission Permission, IReadOnlyList<string> Patterns)
{
    private Regex[] Regexes { get; } = [.. Patterns.Select(Glob.ToRegex)];

    public bool Covers(params string[] subjects) =>
        Regexes.Length == 0 || subjects.Any(s => Regexes.Any(r => r.IsMatch(s)));
}

/// <summary>A configured role: every permission ("*"), or a list of grants.</summary>
public sealed record RoleDefinition(bool All, IReadOnlyList<Grant> Grants);

/// <summary>The union of a caller's roles. <see cref="Everything"/> when access control is off or in stdio mode.</summary>
public sealed class UserPermissions
{
    private readonly IReadOnlyList<Grant>? _grants; // null: unrestricted

    private UserPermissions(IReadOnlyList<Grant>? grants, IReadOnlyList<string> roles)
    {
        _grants = grants;
        Roles = roles;
    }

    public static UserPermissions Everything { get; } = new(null, ["*"]);

    public static UserPermissions From(IEnumerable<Grant> grants, IReadOnlyList<string> roles) => new([.. grants], roles);

    public IReadOnlyList<string> Roles { get; }

    public bool IsUnrestricted => _grants is null;

    /// <summary>Has the permission for at least one subject.</summary>
    public bool Has(Permission permission) => _grants is null || _grants.Any(g => g.Permission == permission);

    /// <summary>May use <paramref name="permission"/> on this contract (patterns match name, tool name, type or URN).</summary>
    public bool Allows(Permission permission, ContractDescriptor contract) =>
        _grants is null || _grants.Any(g => g.Permission == permission &&
            g.Covers(contract.Name, contract.ToolName, contract.Type.FullName ?? contract.Name, contract.MessageTypeUrn));

    /// <summary>May use <paramref name="permission"/> on this queue (republish patterns match the error queue name).</summary>
    public bool AllowsQueue(Permission permission, string queue) =>
        _grants is null || _grants.Any(g => g.Permission == permission && g.Covers(queue));
}

/// <summary>
/// Role-based access for HTTP callers (RabbitMcp:Roles, UserRoles, DefaultRoles, JwtRolesClaim). Off while no role is
/// configured: then every authenticated caller may use every enabled tool, as before. Stdio has a single local user,
/// who always has every enabled tool — the Enable* switches are the limit there.
/// </summary>
public sealed class AccessControl
{
    public const string AllPermissions = "*";

    private static readonly Regex RoleName = new("^[A-Za-z0-9_.-]{1,64}$", RegexOptions.CultureInvariant);

    private readonly Dictionary<string, RoleDefinition> _roles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string[]> _userRoles = new(StringComparer.OrdinalIgnoreCase);
    private readonly string[] _defaultRoles;
    private readonly string _rolesClaim;

    /// <exception cref="StartupException">Unknown permission, undefined role, or a pattern where none is allowed.</exception>
    public AccessControl(RabbitMcpOptions options)
    {
        foreach (var (name, definition) in options.Roles)
        {
            if (!RoleName.IsMatch(name))
                throw new StartupException($"RabbitMcp:Roles: '{name}' is not a valid role name (letters, digits, '_', '.', '-').");
            _roles[name] = ParseRole(name, definition);
        }

        foreach (var (user, roles) in ParseUserRoles(options.UserRoles))
            _userRoles[user] = CheckDefined(roles, $"RabbitMcp:UserRoles ('{user}')");

        _defaultRoles = CheckDefined(SplitList(options.DefaultRoles, ','), "RabbitMcp:DefaultRoles");
        _rolesClaim = string.IsNullOrWhiteSpace(options.JwtRolesClaim) ? "roles" : options.JwtRolesClaim.Trim();

        if (!Enabled && (_userRoles.Count > 0 || _defaultRoles.Length > 0))
            throw new StartupException("RabbitMcp:UserRoles/DefaultRoles need at least one role in RabbitMcp:Roles.");
    }

    /// <summary>True once at least one role is configured.</summary>
    public bool Enabled => _roles.Count > 0;

    public IReadOnlyCollection<string> RoleNames => _roles.Keys;

    /// <summary>
    /// The caller's permissions: roles assigned to any of its names (API key name, preferred_username, email, sub)
    /// plus roles named in its token's roles claim; DefaultRoles when it has none. No user (stdio): everything.
    /// </summary>
    public UserPermissions For(ClaimsPrincipal? user)
    {
        if (!Enabled || user is null)
            return UserPermissions.Everything;

        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (user.Identity is { IsAuthenticated: true })
        {
            foreach (var name in NamesOf(user))
            {
                if (_userRoles.TryGetValue(name, out var assigned))
                    roles.UnionWith(assigned);
            }
            foreach (var claim in user.FindAll(_rolesClaim))
            {
                if (_roles.ContainsKey(claim.Value))
                    roles.Add(claim.Value);
            }
        }
        if (roles.Count == 0)
            roles.UnionWith(_defaultRoles);

        var ordered = roles.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return ordered.Any(r => _roles[r].All)
            ? UserPermissions.Everything
            : UserPermissions.From(ordered.SelectMany(r => _roles[r].Grants), ordered);
    }

    private static IEnumerable<string> NamesOf(ClaimsPrincipal user) =>
        new[] { user.Identity?.Name, user.FindFirst("preferred_username")?.Value, user.FindFirst("email")?.Value,
                user.FindFirst("sub")?.Value, user.FindFirst(ClaimTypes.Name)?.Value }
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)!;

    /// <summary>"read;publish:Billing.*,Order*;request:Get*;republish:orders-*_error;respond" or "*".</summary>
    public static RoleDefinition ParseRole(string role, string? definition)
    {
        var grants = new List<Grant>();
        var all = false;
        foreach (var entry in SplitList(definition, ';'))
        {
            if (entry == AllPermissions)
            {
                all = true;
                continue;
            }

            var colon = entry.IndexOf(':');
            var name = (colon < 0 ? entry : entry[..colon]).Trim();
            var patterns = colon < 0 ? [] : SplitList(entry[(colon + 1)..], ',');

            if (!Enum.TryParse<Permission>(name, ignoreCase: true, out var permission) || !Enum.IsDefined(permission)
                || int.TryParse(name, out _))
                throw new StartupException(
                    $"RabbitMcp:Roles:{role}: unknown permission '{name}'. Use read, publish, request, republish, respond or *.");
            if (permission == Permission.Read && patterns.Length > 0)
                throw new StartupException($"RabbitMcp:Roles:{role}: 'read' cannot be limited to patterns.");
            if (colon >= 0 && patterns.Length == 0)
                throw new StartupException($"RabbitMcp:Roles:{role}: '{entry}' has an empty pattern list.");

            grants.Add(new Grant(permission, patterns));
        }

        if (grants.Count == 0 && !all)
            throw new StartupException($"RabbitMcp:Roles:{role}: no permissions (e.g. \"read\" or \"read;publish:Order*\").");
        return new RoleDefinition(all, grants);
    }

    /// <summary>"alice=operator;bob@example.com=viewer,billing" — names may contain anything but '=' and ';'.</summary>
    public static IEnumerable<(string User, string[] Roles)> ParseUserRoles(string? value)
    {
        foreach (var entry in SplitList(value, ';'))
        {
            var eq = entry.LastIndexOf('=');
            if (eq <= 0 || eq == entry.Length - 1)
                throw new StartupException($"RabbitMcp:UserRoles: '{entry}' must look like name=role1,role2.");
            yield return (entry[..eq].Trim(), SplitList(entry[(eq + 1)..], ','));
        }
    }

    private string[] CheckDefined(string[] roles, string where)
    {
        foreach (var role in roles)
        {
            if (!_roles.ContainsKey(role))
                throw new StartupException($"{where}: role '{role}' is not defined in RabbitMcp:Roles.");
        }
        return roles;
    }

    private static string[] SplitList(string? value, char separator) =>
        (value ?? "").Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary><c>*</c> wildcard patterns, case-insensitive (same syntax as PublishAllow/PublishDeny).</summary>
public static class Glob
{
    public static Regex ToRegex(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
