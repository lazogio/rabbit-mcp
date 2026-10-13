// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;

namespace RabbitMcp.Server.Contracts;

/// <summary>
/// Which contracts may be sent (publish_* / request_*). Patterns use <c>*</c> as a wildcard and are
/// matched case-insensitively against the contract name, tool name, full type name and URN, e.g.
/// <c>Order*</c>, <c>Billing.Contracts.*</c>, <c>request_*</c>. Deny wins over allow; an empty allow
/// list allows everything. Other tools (validate_message, peek, ...) are not affected.
/// </summary>
public sealed class PublishPolicy
{
    private readonly (string Pattern, Regex Regex)[] _allow;
    private readonly (string Pattern, Regex Regex)[] _deny;

    public PublishPolicy(string? allow, string? deny)
    {
        _allow = Parse(allow);
        _deny = Parse(deny);
    }

    public static PublishPolicy AllowAll { get; } = new(null, null);

    public bool IsRestricted => _allow.Length > 0 || _deny.Length > 0;

    public bool IsAllowed(ContractDescriptor contract) =>
        !_deny.Any(p => Matches(p.Regex, contract)) &&
        (_allow.Length == 0 || _allow.Any(p => Matches(p.Regex, contract)));

    /// <summary>Patterns that match no contract — almost always a typo worth a startup warning.</summary>
    public IReadOnlyList<string> UnmatchedPatterns(IEnumerable<ContractDescriptor> contracts)
    {
        var all = contracts.ToArray();
        return _allow.Concat(_deny)
            .Where(p => !all.Any(c => Matches(p.Regex, c)))
            .Select(p => p.Pattern)
            .ToArray();
    }

    private static bool Matches(Regex regex, ContractDescriptor c) =>
        regex.IsMatch(c.Name) || regex.IsMatch(c.ToolName) ||
        regex.IsMatch(c.Type.FullName ?? c.Name) || regex.IsMatch(c.MessageTypeUrn);

    private static (string, Regex)[] Parse(string? patterns) =>
        string.IsNullOrWhiteSpace(patterns)
            ? Array.Empty<(string, Regex)>()
            : patterns
                .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(p => (p, new Regex("^" + Regex.Escape(p).Replace("\\*", ".*") + "$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
                .ToArray();
}
