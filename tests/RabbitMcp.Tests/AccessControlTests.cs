// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RabbitMcp.Samples.Orders;
using RabbitMcp.Server;
using RabbitMcp.Server.Contracts;
using RabbitMcp.Server.Hosting;
using RabbitMcp.Server.Messaging;
using RabbitMcp.Server.Security;

namespace RabbitMcp.Tests;

public class AccessControlTests
{
    private static readonly ContractRegistry Registry = ContractRegistry.FromAssemblies(typeof(OrderSubmitted).Assembly);
    private static readonly ContractDescriptor Submitted = Registry.Get(typeof(OrderSubmitted));
    private static readonly ContractDescriptor GetStatus = Registry.Get(typeof(GetOrderStatus));

    private static AccessControl Create(
        string? userRoles = null, string? defaultRoles = null, params (string Role, string Definition)[] roles)
    {
        var options = new RabbitMcpOptions { UserRoles = userRoles, DefaultRoles = defaultRoles };
        foreach (var (role, definition) in roles)
            options.Roles[role] = definition;
        return new AccessControl(options);
    }

    private static ClaimsPrincipal User(string name, params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, name) }.Concat(claims.Select(c => new Claim(c.Type, c.Value))), "test"));

    private static readonly (string, string)[] StandardRoles =
    [
        ("viewer", "read"),
        ("operator", "read;publish:Order*;republish:*_error"),
        ("admin", "*"),
    ];

    [Fact]
    public void Without_roles_everyone_has_everything()
    {
        var access = Create();
        Assert.False(access.Enabled);
        Assert.True(access.For(User("anyone")).IsUnrestricted);
    }

    [Fact]
    public void Stdio_caller_has_everything_even_with_roles()
    {
        var access = Create("alice=viewer", null, StandardRoles);
        Assert.True(access.For(null).IsUnrestricted);
    }

    [Fact]
    public void Roles_come_from_user_name_token_claim_or_defaults()
    {
        var access = Create("alice=operator;bob@example.com=viewer", "viewer", StandardRoles);

        var alice = access.For(User("alice"));
        Assert.True(alice.Allows(Permission.Publish, Submitted));
        Assert.False(alice.Allows(Permission.Request, GetStatus));
        Assert.Equal(new[] { "operator" }, alice.Roles);

        // A verified email claim of a JWT user maps too; names are case-insensitive.
        var bob = access.For(User("bob", ("email", "Bob@Example.com"), ("email_verified", "true")));
        Assert.True(bob.Has(Permission.Read));
        Assert.False(bob.Has(Permission.Publish));

        // Token roles claim: only roles that are defined count.
        var carol = access.For(User("carol", ("roles", "admin"), ("roles", "undefined-role")));
        Assert.True(carol.IsUnrestricted);

        // Nobody assigned → DefaultRoles.
        Assert.Equal(new[] { "viewer" }, access.For(User("dave")).Roles);
    }

    private static ClaimsPrincipal Token(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "AuthenticationTypes.Federation",
            "preferred_username", "roles"));

    private static ClaimsPrincipal ApiKeyCaller(string name) =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, name) }, ApiKeyAuthenticationHandler.SchemeName));

    [Fact]
    public void Unverified_email_assigns_no_role()
    {
        // Many identity providers let users change their e-mail: only a verified one may pick up someone's roles.
        var access = Create("boss@example.com=admin", null, StandardRoles);

        Assert.False(access.For(Token(("sub", "u-1"), ("email", "boss@example.com"))).Has(Permission.Read));
        Assert.False(access.For(Token(("sub", "u-1"), ("email", "boss@example.com"), ("email_verified", "false"))).Has(Permission.Read));
        Assert.True(access.For(Token(("sub", "u-2"), ("email", "boss@example.com"), ("email_verified", "true"))).IsUnrestricted);
    }

    [Fact]
    public void Token_user_named_like_an_api_key_does_not_get_its_roles()
    {
        var options = new RabbitMcpOptions { ApiKeys = "ci:" + new string('k', 40), UserRoles = "ci=admin;sub-7=viewer" };
        foreach (var (role, definition) in StandardRoles)
            options.Roles[role] = definition;
        var access = new AccessControl(options);

        Assert.True(access.For(ApiKeyCaller("ci")).IsUnrestricted);
        // An identity-provider account called "ci" (username, sub or e-mail) is not the API key "ci".
        Assert.False(access.For(Token(("preferred_username", "ci"))).Has(Permission.Read));
        Assert.False(access.For(Token(("sub", "ci"))).Has(Permission.Read));
        Assert.Equal(new[] { "viewer" }, access.For(Token(("sub", "sub-7"), ("preferred_username", "ci"))).Roles);
        // ... and an API-key caller is only its key name, whatever other claims its principal has.
        Assert.False(access.For(ApiKeyCaller("sub-7")).IsUnrestricted);
    }

    [Fact]
    public void Without_default_roles_unassigned_users_get_nothing()
    {
        var access = Create("alice=viewer", null, StandardRoles);
        var mallory = access.For(User("mallory"));
        Assert.False(mallory.Has(Permission.Read));
        Assert.Empty(mallory.Roles);

        var anonymous = access.For(new ClaimsPrincipal(new ClaimsIdentity()));
        Assert.False(anonymous.Has(Permission.Read));
    }

    [Fact]
    public void Roles_claim_name_is_configurable()
    {
        var options = new RabbitMcpOptions { JwtRolesClaim = "groups" };
        options.Roles["viewer"] = "read";
        var access = new AccessControl(options);

        Assert.True(access.For(User("x", ("groups", "viewer"))).Has(Permission.Read));
        Assert.False(access.For(User("y", ("roles", "viewer"))).Has(Permission.Read));
    }

    [Theory]
    [InlineData("reed", "unknown permission")]
    [InlineData("1", "unknown permission")]
    [InlineData("read:orders*", "cannot be limited")]
    [InlineData("publish:", "empty pattern")]
    [InlineData("", "no permissions")]
    public void Invalid_role_definitions_stop_the_server(string definition, string message)
    {
        var ex = Assert.Throws<StartupException>(() => Create(null, null, ("broken", definition)));
        Assert.Contains(message, ex.Message);
    }

    [Theory]
    [InlineData("alice=ghost", null)]
    [InlineData(null, "ghost")]
    [InlineData("alice", null)]
    public void Undefined_roles_and_bad_assignments_stop_the_server(string? userRoles, string? defaultRoles)
    {
        Assert.Throws<StartupException>(() => Create(userRoles, defaultRoles, ("viewer", "read")));
    }

    [Fact]
    public void Assignments_without_roles_are_a_configuration_error() =>
        Assert.Throws<StartupException>(() => Create("alice=viewer", null));

    [Fact]
    public void Contract_patterns_match_name_tool_type_or_urn()
    {
        var access = Create("a=byName;b=byTool;c=byUrn", null,
            ("byName", "publish:OrderSub*"), ("byTool", "publish:publish_order_*"), ("byUrn", "publish:urn:message:RabbitMcp.Samples.*"));

        foreach (var user in new[] { "a", "b", "c" })
            Assert.True(access.For(User(user)).Allows(Permission.Publish, Submitted), user);
        Assert.False(access.For(User("a")).Allows(Permission.Publish, Registry.Get(typeof(OrderCancelled))));
    }

    // --- ToolAccess: which tools are listed and callable.

    private static ToolAccess Tools(AccessControl access) =>
        new(access, Registry, new ServiceCollection().BuildServiceProvider());

    private static Dictionary<string, JsonElement> Args(string name, string value) =>
        new() { [name] = JsonSerializer.SerializeToElement(value) };

    [Fact]
    public void Tools_are_listed_by_permission()
    {
        var access = Create("alice=operator;bob=viewer", null, StandardRoles);
        var tools = Tools(access);
        var alice = access.For(User("alice"));
        var bob = access.For(User("bob"));

        Assert.True(tools.IsVisible("peek_messages", bob));
        Assert.False(tools.IsVisible("publish_order_submitted", bob));
        Assert.False(tools.IsVisible("republish_from_error", bob));

        Assert.True(tools.IsVisible("publish_order_submitted", alice));
        Assert.False(tools.IsVisible("request_get_order_status", alice));
        Assert.True(tools.IsVisible("republish_from_error", alice));
        Assert.True(tools.IsVisible("dead_letter_from_error", alice));
        Assert.False(tools.IsVisible("dead_letter_from_error", bob));
        Assert.False(tools.IsVisible("pending_requests", alice));
        Assert.False(tools.IsVisible("some_unknown_tool", alice)); // no rule → denied while roles are on
    }

    [Fact]
    public void Calls_are_checked_with_their_arguments()
    {
        var access = Create("alice=operator;bob=viewer", null, StandardRoles);
        var tools = Tools(access);
        var alice = access.For(User("alice"));

        Assert.Null(tools.Check("republish_from_error", Args("errorQueue", "orders_error"), alice));
        var denied = tools.Check("republish_from_error", Args("errorQueue", "payments"), alice);
        Assert.Contains("on queue 'payments'", denied!.Reason);

        var bobDenied = tools.Check("publish_order_submitted", null, access.For(User("bob")));
        Assert.Contains("Permission 'publish'", bobDenied!.Reason);
        Assert.Contains("viewer", bobDenied.Reason);
        Assert.Equal("OrderSubmitted", bobDenied.Contract!.Name);
    }

    [Fact]
    public void Choosing_another_destination_needs_republish_without_patterns()
    {
        var access = Create("alice=operator;root=admin;rita=mover", null,
            [.. StandardRoles, ("mover", "read;republish")]);
        var tools = Tools(access);
        var alice = access.For(User("alice"));

        Dictionary<string, JsonElement> With(params (string Name, string Value)[] args) =>
            args.ToDictionary(a => a.Name, a => JsonSerializer.SerializeToElement(a.Value));

        // operator may republish *_error queues to where their messages came from ...
        Assert.Null(tools.Check("republish_from_error", With(("errorQueue", "orders_error")), alice));
        // ... but not send them anywhere else.
        var redirect = tools.Check("republish_from_error", With(("errorQueue", "orders_error"), ("targetQueue", "payments")), alice);
        Assert.Contains("without patterns (to choose targetQueue)", redirect!.Reason);
        Assert.NotNull(tools.Check("republish_from_error", With(("errorQueue", "orders_error"), ("targetExchange", "payments")), alice));
        Assert.NotNull(tools.Check("dead_letter_from_error", With(("errorQueue", "orders_error"), ("deadLetterQueue", "payments")), alice));
        Assert.Null(tools.Check("dead_letter_from_error", With(("errorQueue", "orders_error"), ("deadLetterQueue", " ")), alice));

        foreach (var name in new[] { "root", "rita" })
            Assert.Null(tools.Check("republish_from_error",
                With(("errorQueue", "orders_error"), ("targetQueue", "payments")), access.For(User(name))));
    }

    [Fact]
    public void Queue_patterns_are_case_sensitive_like_rabbitmq()
    {
        var access = Create("alice=operator", null, ("operator", "republish:orders_error;publish:order*"));
        var alice = access.For(User("alice"));

        Assert.True(alice.AllowsQueue(Permission.Republish, "orders_error"));
        Assert.False(alice.AllowsQueue(Permission.Republish, "Orders_Error")); // a different queue
        Assert.True(alice.Allows(Permission.Publish, Submitted)); // contract names stay case-insensitive
    }

    [Fact]
    public void Every_fixed_tool_has_a_rule()
    {
        var tools = Tools(Create(null, null, ("viewer", "read")));
        foreach (var name in new[] { "list_contracts", "validate_message", "queue_stats", "peek_messages", "list_queues",
                                     "contract_consumers", "find_message", "recent_activity", "republish_from_error", "dead_letter_from_error", "resend_corrected", "pending_requests",
                                     "reject_request" })
            Assert.NotNull(tools.RuleFor(name));
        foreach (var contract in Registry.Contracts)
            Assert.NotNull(tools.RuleFor(contract.ToolName));
    }

    [Fact]
    public void Reply_tools_follow_the_respond_patterns_of_their_request_contracts()
    {
        var services = new ServiceCollection().AddSingleton(ResponderContracts.From(Registry, "GetOrderStatus")).BuildServiceProvider();
        var access = Create("alice=status;bob=other", null, ("status", "respond:GetOrderStatus"), ("other", "respond:Billing*"));
        var tools = new ToolAccess(access, Registry, services);

        Assert.True(tools.IsVisible("reply_order_status", access.For(User("alice"))));
        Assert.False(tools.IsVisible("reply_order_status", access.For(User("bob"))));
        Assert.True(tools.IsVisible("pending_requests", access.For(User("bob")))); // filtered inside the tool
    }
}
