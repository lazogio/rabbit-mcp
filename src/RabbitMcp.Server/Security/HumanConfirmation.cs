// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RabbitMcp.Server.Hosting;

namespace RabbitMcp.Server.Security;

/// <summary>What the person answered.</summary>
public enum PersonAnswer { Approved, Declined, Unavailable }

/// <summary>Asks the person behind the MCP client a yes/no question (the model never sees or answers it).</summary>
public interface IPersonPrompt
{
    Task<PersonAnswer> AskAsync(string message, CancellationToken ct);
}

/// <summary>
/// MCP elicitation: a form with one checkbox, shown by the client to the person. Unavailable when the client did not
/// declare the capability or the transport cannot send server→client requests (stateless HTTP).
/// </summary>
public sealed class ElicitationPrompt(McpServer? server) : IPersonPrompt
{
    public const string Field = "approve";

    public async Task<PersonAnswer> AskAsync(string message, CancellationToken ct)
    {
        if (server?.ClientCapabilities?.Elicitation is null)
            return PersonAnswer.Unavailable;

        ElicitResult result;
        try
        {
            result = await server.ElicitAsync(new ElicitRequestParams
            {
                Message = message,
                RequestedSchema = new ElicitRequestParams.RequestSchema
                {
                    Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                    {
                        [Field] = new ElicitRequestParams.BooleanSchema
                        {
                            Description = "Tick to let rabbit-mcp do this. Leave it empty or decline to stop.",
                            Default = false,
                        },
                    },
                },
            }, ct);
        }
        catch (InvalidOperationException)
        {
            return PersonAnswer.Unavailable; // e.g. stateless HTTP: no way to reach the person
        }

        return Approves(result.Action, result.Content) ? PersonAnswer.Approved : PersonAnswer.Declined;
    }

    public static bool Approves(string? action, IDictionary<string, JsonElement>? content) =>
        string.Equals(action, "accept", StringComparison.OrdinalIgnoreCase)
        && content is not null && content.TryGetValue(Field, out var value)
        && (value.ValueKind == JsonValueKind.True
            || (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var b) && b));
}

/// <summary>The outcome for one tool call.</summary>
/// <param name="Proceed">Run the tool.</param>
/// <param name="Refusal">Why not (shown to the model) when it does not proceed.</param>
/// <param name="How">person (elicitation) · argument (confirm=true from the model) · not-required; for the audit.</param>
public sealed record ConfirmationDecision(bool Proceed, string? Refusal, string How);

/// <summary>
/// RabbitMcp:HumanConfirmation and ConfirmTools: before a listed tool changes anything, the person is asked through the
/// MCP client. A confirm=true written by the model is no longer enough when the person can be asked.
/// </summary>
public sealed class HumanConfirmation
{
    /// <summary>Tools with a preview: calls without confirm=true change nothing and are never asked about.</summary>
    public static readonly IReadOnlySet<string> PreviewTools =
        new HashSet<string>(StringComparer.Ordinal) { "republish_from_error", "dead_letter_from_error", "resend_corrected" };

    private const int MaxSummaryChars = 1500;

    private readonly Regex[] _tools;

    /// <exception cref="StartupException">Unknown mode.</exception>
    public HumanConfirmation(RabbitMcpOptions options)
    {
        Mode = (options.HumanConfirmation ?? "auto").Trim().ToLowerInvariant() switch
        {
            "auto" or "" => "auto",
            "required" => "required",
            "off" => "off",
            var other => throw new StartupException(
                $"RabbitMcp:HumanConfirmation must be auto, required or off, not '{other}'."),
        };
        _tools = [.. (options.ConfirmTools ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => Glob.ToRegex(p, ignoreCase: false))];
    }

    /// <summary>auto | required | off</summary>
    public string Mode { get; }

    /// <summary>Whether this call changes something the person has to confirm.</summary>
    public bool Applies(string tool, IDictionary<string, JsonElement>? arguments)
    {
        if (Mode == "off" || !_tools.Any(r => r.IsMatch(tool)))
            return false;
        if (!PreviewTools.Contains(tool))
            return true;
        return arguments is not null && arguments.TryGetValue("confirm", out var confirm) && confirm.ValueKind == JsonValueKind.True;
    }

    public async Task<ConfirmationDecision> DecideAsync(
        string tool, IDictionary<string, JsonElement>? arguments, string? caller, IPersonPrompt prompt, CancellationToken ct)
    {
        if (!Applies(tool, arguments))
            return new ConfirmationDecision(true, null, "not-required");

        switch (await prompt.AskAsync(Summary(tool, arguments, caller), ct))
        {
            case PersonAnswer.Approved:
                return new ConfirmationDecision(true, null, "person");
            case PersonAnswer.Declined:
                return new ConfirmationDecision(false,
                    $"The person declined {tool}. Nothing was changed. Do not retry unless they ask you to.", "person");
        }

        // The person cannot be asked from here.
        return Mode == "required"
            ? new ConfirmationDecision(false,
                $"{tool} needs a person's confirmation (RabbitMcp:HumanConfirmation=required), but this MCP client cannot ask " +
                "(no elicitation support, or stateless HTTP). Nothing was changed.", "unavailable")
            : new ConfirmationDecision(true, null, "argument");
    }

    /// <summary>What the person is asked: the tool and its arguments, one per line, shortened.</summary>
    public static string Summary(string tool, IDictionary<string, JsonElement>? arguments, string? caller)
    {
        var text = new StringBuilder();
        text.Append("rabbit-mcp: ").Append(caller is null ? "the assistant" : caller).Append(" wants to run ").Append(tool).Append('.');
        foreach (var (name, value) in arguments ?? new Dictionary<string, JsonElement>())
        {
            if (name == "confirm")
                continue;
            var shown = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
            text.Append('\n').Append(name).Append(": ").Append(shown);
        }
        text.Append("\n\nAllow it?");
        var summary = text.ToString();
        return summary.Length <= MaxSummaryChars ? summary : summary[..MaxSummaryChars] + " …";
    }
}
