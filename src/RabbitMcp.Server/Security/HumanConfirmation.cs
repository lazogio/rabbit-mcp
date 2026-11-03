// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
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
    /// <summary>False when the person certainly cannot be asked: nothing is prepared for a question nobody would see.</summary>
    bool CanAsk { get; }

    Task<PersonAnswer> AskAsync(string message, CancellationToken ct);
}

/// <summary>
/// MCP elicitation: a form with one checkbox, shown by the client to the person. Unavailable when the client did not
/// declare the capability or the transport cannot send server→client requests (stateless HTTP).
/// </summary>
public sealed class ElicitationPrompt(McpServer? server) : IPersonPrompt
{
    public const string Field = "approve";

    public bool CanAsk => server?.ClientCapabilities?.Elicitation is not null;

    public async Task<PersonAnswer> AskAsync(string message, CancellationToken ct)
    {
        if (!CanAsk)
            return PersonAnswer.Unavailable;

        ElicitResult result;
        try
        {
            result = await server!.ElicitAsync(new ElicitRequestParams
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
/// MCP client. The model writes confirm=true (and every reply) itself, and message content it reads can talk it into
/// one, so an unset mode is "required": where the person cannot be asked (a client without elicitation, stateless HTTP)
/// the change is refused, and trusting the model instead ("auto", "off") is the operator's explicit choice.
/// </summary>
public sealed class HumanConfirmation
{
    /// <summary>Tools with a preview: calls without confirm=true change nothing and are never asked about.</summary>
    public static readonly IReadOnlySet<string> PreviewTools =
        new HashSet<string>(StringComparer.Ordinal) { "republish_from_error", "dead_letter_from_error", "resend_corrected", "declare_exchange", "declare_queue" };

    /// <summary>Arguments that redirect messages or override a safety check: shown first, whatever order the model used.</summary>
    private static readonly string[] FirstArguments =
        ["targetExchange", "targetQueue", "deadLetterQueue", "force", "count", "messageIds", "requestId"];

    private const int MaxValueChars = 300;
    private const int MaxDetailChars = 800;
    private const int MaxArguments = 20;

    private readonly Regex[] _tools;
    private readonly bool _requiredByDefault;

    /// <param name="http">The server runs over stateless HTTP, where the person can never be asked.</param>
    /// <exception cref="StartupException">Unknown mode.</exception>
    public HumanConfirmation(RabbitMcpOptions options, bool http = false)
    {
        Mode = options.HumanConfirmation?.Trim().ToLowerInvariant() switch
        {
            null or "" => "required",
            "auto" => "auto",
            "required" => "required",
            "off" => "off",
            var other => throw new StartupException(
                $"RabbitMcp:HumanConfirmation must be auto, required or off, not '{other}'."),
        };
        _requiredByDefault = string.IsNullOrWhiteSpace(options.HumanConfirmation);
        _tools = [.. (options.ConfirmTools ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => Glob.ToRegex(p, ignoreCase: false))];

        if (_tools.Length > 0 && Mode != "required" && (http || Mode == "off"))
            StartupWarning = (http
                    ? $"RabbitMcp:HumanConfirmation={Mode} over HTTP: stateless HTTP cannot ask a person, so "
                    : "RabbitMcp:HumanConfirmation=off: the person is never asked, so ") +
                "the model alone approves the ConfirmTools changes (its own confirm=true, its own replies), and message " +
                "content it reads can talk it into one. Leave it unset (required) unless every caller and every message is trusted.";
    }

    /// <summary>auto | required | off</summary>
    public string Mode { get; }

    /// <summary>Printed at startup when the configuration lets the model approve its own changes.</summary>
    public string? StartupWarning { get; }

    /// <summary>Tools that answer a pending request (responder mode): the question shows the request they answer.</summary>
    public static bool AnswersRequest(string tool) =>
        tool.StartsWith("reply_", StringComparison.Ordinal) || tool == "reject_request";

    /// <summary>Whether this call changes something the person has to confirm.</summary>
    public bool Applies(string tool, IDictionary<string, JsonElement>? arguments)
    {
        if (Mode == "off" || !_tools.Any(r => r.IsMatch(tool)))
            return false;
        if (!PreviewTools.Contains(tool))
            return true;
        return arguments is not null && arguments.TryGetValue("confirm", out var confirm) && confirm.ValueKind == JsonValueKind.True;
    }

    /// <param name="details">What the server found the call would do (<see cref="DescribePreview"/>, <see cref="DescribeRequest"/>).</param>
    public async Task<ConfirmationDecision> DecideAsync(
        string tool, IDictionary<string, JsonElement>? arguments, string? caller, IPersonPrompt prompt, CancellationToken ct,
        string? details = null)
    {
        if (!Applies(tool, arguments))
            return new ConfirmationDecision(true, null, "not-required");

        switch (await prompt.AskAsync(Summary(tool, arguments, caller, details), ct))
        {
            case PersonAnswer.Approved:
                return new ConfirmationDecision(true, null, "person");
            case PersonAnswer.Declined:
                return new ConfirmationDecision(false,
                    $"The person declined {tool}. Nothing was changed. Do not retry unless they ask you to.", "person");
        }

        // The person cannot be asked from here.
        var setting = _requiredByDefault
            ? "RabbitMcp:HumanConfirmation is required by default"
            : "RabbitMcp:HumanConfirmation=required";
        return Mode == "required"
            ? new ConfirmationDecision(false,
                $"{tool} needs a person's confirmation ({setting}), but this MCP client cannot ask " +
                "(no elicitation support, or stateless HTTP). Nothing was changed. The person can run it from an MCP client " +
                "that shows confirmation dialogs (e.g. Claude Code over stdio), or the server's operator can set " +
                "RabbitMcp:HumanConfirmation=auto to let your own call count as approval.", "unavailable")
            : new ConfirmationDecision(true, null, "argument");
    }

    /// <summary>
    /// What the person is asked: who, the tool, what the server found it would do, then every argument on its own line.
    /// Each value is shortened and kept on one line (line breaks, control and direction characters are escaped), so an
    /// argument can neither push the others out of the question nor imitate them. Arguments that redirect messages or
    /// override a safety check come first.
    /// </summary>
    public static string Summary(string tool, IDictionary<string, JsonElement>? arguments, string? caller, string? details = null)
    {
        var text = new StringBuilder();
        text.Append("rabbit-mcp: ").Append(caller is null ? "the assistant" : Shown(caller, 100))
            .Append(" wants to run ").Append(Shown(tool, 100)).Append('.');
        if (!string.IsNullOrEmpty(details))
            text.Append('\n').Append(Shown(details, MaxDetailChars));

        var shown = (arguments ?? new Dictionary<string, JsonElement>())
            .Where(a => a.Key != "confirm")
            .OrderBy(a => Rank(a.Key))
            .ThenBy(a => a.Key, StringComparer.Ordinal)
            .ToList();
        foreach (var (name, value) in shown.Take(MaxArguments))
            text.Append('\n').Append(Shown(name, 60)).Append(": ").Append(Shown(Text(value), MaxValueChars));
        if (shown.Count > MaxArguments)
            text.Append('\n').Append(CultureInfo.InvariantCulture, $"… and {shown.Count - MaxArguments} more arguments");

        text.Append("\n\nAllow it?");
        return text.ToString();
    }

    private static int Rank(string argument)
    {
        var index = Array.IndexOf(FirstArguments, argument);
        return index >= 0 ? index : FirstArguments.Length;
    }

    /// <summary>
    /// One line on what the tool's own preview (run by the server just before asking) says the call would do, so the
    /// person judges the effect rather than the model's arguments. Null for a result this does not know.
    /// </summary>
    public static string? DescribePreview(string tool, JsonElement preview)
    {
        if (preview.ValueKind != JsonValueKind.Object)
            return null;
        return tool switch
        {
            "republish_from_error" => Moves(preview, "republish", "republished", RepublishTargets(preview)),
            "dead_letter_from_error" => Moves(preview, "move", "moved", Str(preview, "deadLetterQueue")),
            "resend_corrected" => Correction(preview),
            "declare_exchange" or "declare_queue" => Declaration(preview),
            _ => null,
        };
    }

    /// <summary>One line on the pending request that a reply_* or reject_request call answers.</summary>
    public static string DescribeRequest(string contract, string? from, JsonElement? message) =>
        $"It answers the {contract} request" + (from is null ? "" : $" from {from}") + ": " +
        (message is { } body ? Text(body) : "(no message)");

    private static string? Moves(JsonElement result, string verb, string done, string? to)
    {
        var from = Str(result, "errorQueue") ?? "?";
        if (Obj(result, "selection") is { } selection)
        {
            var matched = Int(selection, "matched") ?? 0;
            var text = new StringBuilder();
            text.Append(CultureInfo.InvariantCulture, $"Preview just now: {Messages(matched)} in {from} match the filter and would be {done}");
            if (to is not null)
                text.Append(" to ").Append(to);
            text.Append('.');
            if (selection.TryGetProperty("moreMayRemain", out var more) && more.ValueKind == JsonValueKind.True)
                text.Append(" The scan stopped at its limit: more may match.");
            return text.ToString();
        }
        if (Obj(result, "preview") is not { } peek)
            return null;
        var count = Int(peek, "returned") ?? 0;
        return $"Preview just now: would {verb} {Messages(count)} from {from}" + (to is null ? "" : $" to {to}") + ".";
    }

    /// <summary>Where the previewed messages would go, e.g. "order-placed (2), queue:orders (1)".</summary>
    private static string? RepublishTargets(JsonElement result)
    {
        if (Obj(result, "preview") is not { } peek || !peek.TryGetProperty("messages", out var messages)
            || messages.ValueKind != JsonValueKind.Array)
            return null;
        var all = messages.EnumerateArray().ToList();
        var targets = all
            .GroupBy(m => Str(m, "republishTarget") ?? "no target", StringComparer.Ordinal)
            .Select(g => all.Count == 1 ? g.Key : $"{g.Key} ({g.Count().ToString(CultureInfo.InvariantCulture)})")
            .ToList();
        if (targets.Count == 0)
            return null;
        var warned = all.Count(m => Str(m, "republishWarning") is not null);
        return string.Join(", ", targets) +
               (warned == 0 ? "" : $"; {Messages(warned)} with a warning (see republishWarning in the preview)");
    }

    private static string Correction(JsonElement result)
    {
        var changes = result.TryGetProperty("changes", out var list) && list.ValueKind == JsonValueKind.Array
            ? string.Join(", ", list.EnumerateArray().Select(c => Str(c, "path")).OfType<string>().Take(10))
            : "";
        return $"Preview just now: would send a corrected {Str(result, "contract") ?? "message"} " +
               $"(of message {Str(result, "originalMessageId") ?? "?"}) to {Str(result, "target") ?? "?"}, changing " +
               $"{(changes.Length == 0 ? "nothing" : changes)}; the original is parked unchanged in {Str(result, "deadLetterQueue") ?? "?"}.";
    }

    private static string Declaration(JsonElement result)
    {
        var text = new StringBuilder("Preview just now: ");
        text.Append(Str(result, "outcome") == "exists" ? "finds an existing (left unchanged) " : "would create ")
            .Append(Str(result, "kind") ?? "entity").Append(' ').Append(Str(result, "name") ?? "?")
            .Append(" (").Append(Str(result, "type") ?? "?").Append(')');
        if (Str(result, "boundTo") is { } boundTo)
        {
            text.Append(", bound to ").Append(boundTo);
            if (Str(result, "routingKey") is { Length: > 0 } key)
                text.Append(" with routing key ").Append(key);
        }
        if (Obj(result, "arguments") is { } arguments && arguments.EnumerateObject().Any())
            text.Append(", arguments ").Append(string.Join("; ", arguments.EnumerateObject().Select(a => $"{a.Name}={Text(a.Value)}")));
        if (Str(result, "template") is { } template)
            text.Append(", template ").Append(template);
        return text.Append('.').ToString();
    }

    private static string Messages(int count) =>
        count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " message" : " messages");

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static JsonElement? Obj(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    /// <summary>A value as text: strings as they are, anything else as compact JSON.</summary>
    private static string Text(JsonElement value)
    {
        try
        {
            if (value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? "";
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                value.WriteTo(writer);
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return value.GetRawText(); // e.g. a lone surrogate: shown escaped by Shown
        }
    }

    /// <summary>
    /// One line of at most about <paramref name="max"/> characters. Line breaks, control characters and invisible
    /// formatting characters (direction overrides, zero-width and tag characters) are written as escapes, lone surrogates
    /// as U+FFFD: what the person reads is what the value contains.
    /// </summary>
    public static string Shown(string value, int max)
    {
        var text = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            if (text.Length >= max)
                return text.Append(" …").ToString();
            switch (rune.Value)
            {
                case '\n':
                    text.Append("\\n");
                    break;
                case '\r':
                    text.Append("\\r");
                    break;
                case '\t':
                    text.Append("\\t");
                    break;
                default:
                    if (Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format
                            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                        text.Append(CultureInfo.InvariantCulture, $"\\u{rune.Value:X4}");
                    else
                        text.Append(rune.ToString());
                    break;
            }
        }
        return text.ToString();
    }
}
