// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using RabbitMcp.Samples.Orders;
using RabbitMcp.Server;
using RabbitMcp.Server.Contracts;
using RabbitMcp.Server.Hosting;
using RabbitMcp.Server.Messaging;
using RabbitMcp.Server.Security;

namespace RabbitMcp.Tests;

/// <summary>declare_exchange / declare_queue: what may be created, by whom, and with which settings.</summary>
public class TopologyTests
{
    private static readonly ContractRegistry Registry = ContractRegistry.FromAssemblies(typeof(OrderSubmitted).Assembly);
    private static readonly string SubmittedExchange = Registry.Get(typeof(OrderSubmitted)).Exchange;

    private static TopologyPolicy Policy(string? allow = null, string? deny = null, string? arguments = null,
        string? rules = null, params (string Name, string Definition)[] templates)
    {
        var options = new RabbitMcpOptions { TopologyAllow = allow, TopologyDeny = deny, TopologyArguments = arguments, TopologyRules = rules };
        foreach (var (name, definition) in templates)
            options.TopologyTemplates[name] = definition;
        return new TopologyPolicy(options, Registry);
    }

    private const string BillingQueue =
        "kind=queue;type=quorum;x-dead-letter-exchange=billing.dlx;x-message-ttl=60000;bind=billing.events;routingKey=paid";

    private static Dictionary<string, JsonElement> Args(object values) =>
        JsonSerializer.SerializeToElement(values).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    private static Dictionary<string, JsonElement> Arg(string name, object value) =>
        new() { [name] = JsonSerializer.SerializeToElement(value) };

    [Fact]
    public void Defaults_create_a_durable_fanout_exchange_and_classic_queue()
    {
        var exchange = Policy().Exchange("billing.events", null, null, durable: true, autoDelete: false, null, null, null);
        Assert.Equal("fanout", exchange.Type);
        Assert.Empty(exchange.Arguments);
        Assert.Null(exchange.BindToExchange);

        var queue = Policy().Queue("billing-audit", null, null, durable: true, null, "billing.events", null);
        Assert.Equal("classic", queue.QueueType);
        Assert.Equal("classic", Assert.Single(queue.Arguments).Value); // explicit, so an existing quorum queue differs
        Assert.Equal("billing.events", queue.BindExchange);
        Assert.Equal("", queue.RoutingKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" padded")]
    [InlineData("amq.custom")]
    [InlineData("line\nbreak")]
    public void Reserved_empty_or_odd_names_are_refused(string name)
    {
        Assert.Throws<McpException>(() => Policy().Queue(name, null, null, true, null, null, null));
        Assert.Throws<McpException>(() => Policy().Exchange(name, null, null, true, false, null, null, null));
    }

    [Fact]
    public void Allow_and_deny_patterns_are_case_sensitive_and_deny_wins()
    {
        var policy = Policy(allow: "billing.*", deny: "billing.secret*");

        policy.Queue("billing.audit", null, null, true, null, null, null);
        Assert.Contains("TopologyAllow", Assert.Throws<McpException>(() => policy.Queue("Billing.audit", null, null, true, null, null, null)).Message);
        Assert.Contains("TopologyDeny", Assert.Throws<McpException>(() => policy.Queue("billing.secrets", null, null, true, null, null, null)).Message);
    }

    [Fact]
    public void Default_arguments_cannot_drop_messages()
    {
        var policy = Policy();
        var queue = policy.Queue("q", null, null, true, Args(new Dictionary<string, object> { ["x-dead-letter-exchange"] = "dlx" }), null, null);
        Assert.Equal("dlx", queue.Arguments["x-dead-letter-exchange"]);

        var refused = Assert.Throws<McpException>(() => policy.Queue("q", null, null, true, Arg("x-message-ttl", 60000), null, null));
        Assert.Contains("TopologyArguments", refused.Message);
        Assert.Contains("x-dead-letter-exchange", refused.Message); // lists what is allowed
        Assert.Throws<McpException>(() => policy.Queue("q", null, null, true, Arg("x-max-length", 10), null, null));
        Assert.Throws<McpException>(() => policy.Queue("q", null, null, true, Arg("x-expires", 1000), null, null));
    }

    [Fact]
    public void Configured_arguments_replace_the_defaults_and_star_allows_any()
    {
        var ttl = Policy(arguments: "x-message-ttl").Queue("q", null, null, true, Arg("x-message-ttl", 60000), null, null);
        Assert.Equal(60000L, ttl.Arguments["x-message-ttl"]);
        Assert.Throws<McpException>(() => Policy(arguments: "x-message-ttl").Queue("q", null, null, true, Arg("x-dead-letter-exchange", "dlx"), null, null));

        var any = Policy(arguments: "*").Queue("q", null, null, true, Arg("x-custom", true), null, null);
        Assert.Equal(true, any.Arguments["x-custom"]);
        Assert.Throws<McpException>(() => Policy(arguments: "none").Queue("q", null, null, true, Arg("x-dead-letter-exchange", "dlx"), null, null));
        Policy(arguments: " ").Queue("q", null, null, true, Arg("x-dead-letter-exchange", "dlx"), null, null); // empty: the defaults
    }

    [Fact]
    public void Argument_values_are_strings_whole_numbers_or_booleans()
    {
        Assert.Throws<McpException>(() => Policy(arguments: "*").Queue("q", null, null, true, Arg("x-custom", new { a = 1 }), null, null));
        Assert.Throws<McpException>(() => Policy(arguments: "*").Queue("q", null, null, true, Arg("x-custom", 1.5), null, null));
    }

    [Fact]
    public void Queue_type_is_a_parameter_and_quorum_or_stream_are_durable()
    {
        var quorum = Policy().Queue("q", null, "Quorum", true, Arg("x-dead-letter-exchange", "dlx"), null, null);
        Assert.Equal("quorum", quorum.QueueType);
        Assert.Equal("quorum", quorum.Arguments["x-queue-type"]);

        Assert.Throws<McpException>(() => Policy().Queue("q", null, "stream", durable: false, null, null, null));
        Assert.Throws<McpException>(() => Policy().Queue("q", null, "lazy", true, null, null, null));
        Assert.Throws<McpException>(() => Policy(arguments: "*").Queue("q", null, null, true, Arg("x-queue-type", "quorum"), null, null));
        Assert.Throws<StartupException>(() => Policy(arguments: "x-queue-type"));
    }

    [Fact]
    public void Quorum_queues_need_somewhere_to_dead_letter()
    {
        // RabbitMQ 4 drops a quorum queue's message after 20 deliveries unless it can be dead-lettered.
        Assert.Contains("x-dead-letter-exchange", Assert.Throws<McpException>(() => Policy().Queue("q", null, "quorum", true, null, null, null)).Message);
        Policy(arguments: "x-delivery-limit").Queue("q", null, "quorum", true, Arg("x-delivery-limit", -1), null, null);
        Policy().Queue("q", null, "stream", true, null, null, null); // streams keep messages until retention
    }

    [Fact]
    public void Dead_letter_and_alternate_exchanges_are_reported_for_an_existence_check()
    {
        var args = Policy().Queue("q", null, null, true, Args(new Dictionary<string, object>
        {
            ["x-dead-letter-exchange"] = "billing.dlx",
            ["x-dead-letter-routing-key"] = "failed",
        }), null, null).Arguments;
        Assert.Equal(new[] { ("x-dead-letter-exchange", "billing.dlx") }, TopologyPolicy.ReferencedExchanges(args));

        var defaultExchange = Policy().Queue("q", null, null, true, Arg("x-dead-letter-exchange", ""), null, null).Arguments;
        Assert.Empty(TopologyPolicy.ReferencedExchanges(defaultExchange)); // the default exchange always exists
    }

    [Fact]
    public void MassTransit_exchanges_only_with_MassTransits_settings()
    {
        var policy = Policy();
        Assert.True(policy.IsMassTransitExchange(SubmittedExchange));
        Assert.True(policy.IsMassTransitExchange("RabbitMcp.Samples.Orders:OrderStatus")); // a response type
        Assert.False(policy.IsMassTransitExchange("billing.events"));

        policy.Exchange(SubmittedExchange, null, "fanout", durable: true, autoDelete: false, null, null, null);
        Assert.Contains("PRECONDITION_FAILED", Assert.Throws<McpException>(() =>
            policy.Exchange(SubmittedExchange, null, "direct", true, false, null, null, null)).Message);
        Assert.Throws<McpException>(() => policy.Exchange(SubmittedExchange, null, "fanout", durable: false, false, null, null, null));
        Assert.Throws<McpException>(() => policy.Exchange(SubmittedExchange, null, "fanout", true, autoDelete: true, null, null, null));
        Assert.Throws<McpException>(() => policy.Exchange(SubmittedExchange, null, "fanout", true, false,
            Arg("alternate-exchange", "unrouted"), null, null));

        // Another exchange bound to it would deliver foreign messages to the contract's consumers.
        Assert.Contains("bindToExchange", Assert.Throws<McpException>(() =>
            policy.Exchange(SubmittedExchange, null, "fanout", true, false, null, "billing.events", null)).Message);
    }

    [Fact]
    public void Bindings_need_a_real_source_exchange()
    {
        Assert.Throws<McpException>(() => Policy().Queue("q", null, null, true, null, "", null));
        Assert.Throws<McpException>(() => Policy().Queue("q", null, null, true, null, "amq.default", null));
        Assert.Throws<McpException>(() => Policy().Exchange("e", null, null, true, false, null, "e", null));

        var bound = Policy().Exchange("orders.all", null, "topic", true, false, null, "amq.topic", "orders.#");
        Assert.Equal("amq.topic", bound.BindToExchange); // binding to built-in exchanges is fine, creating them is not
        Assert.Equal("orders.#", bound.RoutingKey);
    }

    [Fact]
    public void Urns_map_to_MassTransit_exchange_names()
    {
        Assert.Equal("Billing.Contracts:PaymentCaptured", TopologyPolicy.ExchangeOfUrn("urn:message:Billing.Contracts:PaymentCaptured"));
        Assert.Null(TopologyPolicy.ExchangeOfUrn("PaymentCaptured"));
    }

    // --- configurable rules

    [Fact]
    public void Safety_rules_are_on_by_default_and_can_be_switched_off()
    {
        var defaults = Policy();
        Assert.True(defaults.RequireDeadLetter && defaults.CheckReferences && defaults.ProtectMassTransit);
        Assert.False(defaults.BindExisting);

        var none = Policy(rules: "none");
        none.Queue("q", null, "quorum", true, null, null, null); // no DLX required
        none.Exchange(SubmittedExchange, null, "direct", true, false, null, null, null); // MassTransit not protected
        Assert.False(none.CheckReferences);

        var some = Policy(rules: "protect-masstransit;bind-existing");
        Assert.True(some.BindExisting);
        Assert.False(some.RequireDeadLetter);
        Assert.Throws<McpException>(() => some.Exchange(SubmittedExchange, null, "direct", true, false, null, null, null));
    }

    [Theory]
    [InlineData("require-dead-lettr")]
    [InlineData("templates-only")] // needs templates
    public void Invalid_rules_stop_the_server(string rules) =>
        Assert.Throws<StartupException>(() => Policy(rules: rules));

    // --- templates

    [Fact]
    public void Templates_set_settings_the_caller_does_not_pass()
    {
        var policy = Policy(templates: [("billing-queue", BillingQueue)]);

        var queue = policy.Queue("billing-paid", "Billing-Queue", null, null, null, null, null);
        Assert.Equal("billing-queue", queue.Template);
        Assert.Equal("quorum", queue.QueueType);
        Assert.Equal(60000L, queue.Arguments["x-message-ttl"]); // operator's template: not limited by TopologyArguments
        Assert.Equal("billing.dlx", queue.Arguments["x-dead-letter-exchange"]);
        Assert.Equal("billing.events", queue.BindExchange);
        Assert.Equal("paid", queue.RoutingKey);

        // What the caller passes wins, and what it adds is still limited.
        Assert.Equal("refunded", policy.Queue("billing-refunded", "billing-queue", null, null, null, null, "refunded").RoutingKey);
        Assert.Throws<McpException>(() => policy.Queue("q", "billing-queue", null, null, Arg("x-max-length", 5), null, null));
    }

    [Fact]
    public void Unknown_or_mismatched_templates_list_the_available_ones()
    {
        var policy = Policy(templates: [("billing-queue", BillingQueue), ("events", "kind=exchange;type=topic")]);

        Assert.Contains("billing-queue", Assert.Throws<McpException>(() => policy.Queue("q", "nope", null, null, null, null, null)).Message);
        Assert.Contains("events", Assert.Throws<McpException>(() => policy.Exchange("e", "billing-queue", null, null, null, null, null, null)).Message);
        Assert.Equal("topic", policy.Exchange("e", "events", null, null, null, null, null, null).Type);
    }

    [Fact]
    public void Templates_only_lets_the_caller_pick_just_a_name_and_a_template()
    {
        var policy = Policy(rules: "templates-only", templates: [("billing-queue", BillingQueue)]);

        policy.Queue("billing-paid", "billing-queue", null, null, null, null, null);
        Assert.Throws<McpException>(() => policy.Queue("q", null, null, null, null, null, null));
        Assert.Throws<McpException>(() => policy.Queue("q", "billing-queue", "classic", null, null, null, null));
        Assert.Throws<McpException>(() => policy.Queue("q", "billing-queue", null, null, null, "other", null));
    }

    [Theory]
    [InlineData("type=quorum")] // no kind
    [InlineData("kind=topic")]
    [InlineData("kind=queue;type=topic")]
    [InlineData("kind=queue;colour=blue")]
    [InlineData("kind=queue;durable=maybe")]
    [InlineData("kind=queue;autoDelete")]
    [InlineData("kind=queue;x-queue-type=quorum")]
    public void Malformed_templates_stop_the_server(string definition) =>
        Assert.Throws<StartupException>(() => TopologyPolicy.ParseTemplate("t", definition));

    [Fact]
    public void Template_values_are_typed()
    {
        var t = TopologyPolicy.ParseTemplate("t", "kind=exchange;durable=false;autoDelete;alternate-exchange=unrouted;x-flag=true;x-n=7");
        Assert.False(t.Durable);
        Assert.True(t.AutoDelete);
        Assert.Equal("unrouted", t.Arguments["alternate-exchange"]);
        Assert.Equal(true, t.Arguments["x-flag"]);
        Assert.Equal(7L, t.Arguments["x-n"]);
    }

    // --- who may call it

    private static ClaimsPrincipal User(string name) => new(new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], "test"));

    private static (ToolAccess Tools, AccessControl Access) Access(params (string Role, string Definition)[] roles)
    {
        var options = new RabbitMcpOptions { UserRoles = "alice=builder;root=admin;vera=viewer" };
        foreach (var (role, definition) in roles)
            options.Roles[role] = definition;
        var access = new AccessControl(options);
        return (new ToolAccess(access, Registry, new ServiceCollection().BuildServiceProvider()), access);
    }

    [Fact]
    public void Topology_permission_is_limited_by_name_and_bindings_need_it_unrestricted()
    {
        var (tools, access) = Access(("builder", "read;topology:billing.*"), ("admin", "topology"), ("viewer", "read"));
        var alice = access.For(User("alice"));

        Assert.True(tools.IsVisible("declare_queue", alice));
        Assert.False(tools.IsVisible("declare_exchange", access.For(User("vera"))));
        Assert.Null(tools.Check("declare_queue", Args(new { name = "billing.audit" }), alice));
        Assert.NotNull(tools.Check("declare_queue", Args(new { name = "orders.audit" }), alice));
        Assert.NotNull(tools.Check("declare_queue", Args(new { name = "Billing.audit" }), alice)); // case-sensitive
        Assert.Contains("on exchange 'orders.events'", tools.Check("declare_exchange", Args(new { name = "orders.events" }), alice)!.Reason);
        Assert.Contains("on queue 'orders.audit'", tools.Check("declare_queue", Args(new { name = "orders.audit" }), alice)!.Reason);

        // Binding to another exchange copies its messages: not with a name-limited grant.
        Assert.NotNull(tools.Check("declare_queue", Args(new { name = "billing.audit", bindExchange = "payments" }), alice));
        Assert.NotNull(tools.Check("declare_exchange", Args(new { name = "billing.all", bindToExchange = "payments" }), alice));
        Assert.Null(tools.Check("declare_queue", Args(new { name = "x", bindExchange = "payments" }), access.For(User("root"))));
    }

    [Fact]
    public void Topology_is_a_role_permission_and_a_rate_limit_kind()
    {
        Assert.Contains(AccessControl.ParseRole("r", "topology:billing.*").Grants, g => g.Permission == Permission.Topology);

        var rule = new ToolRule(Permission.Topology, []);
        Assert.Null(RateLimiter.KindOf("declare_queue", rule, Args(new { name = "q" }))); // previews are free
        Assert.Equal("topology", RateLimiter.KindOf("declare_queue", rule, Args(new { name = "q", confirm = true })));
        Assert.True(new RateLimiter(new RabbitMcpOptions { RateLimits = "topology:10/h" }, TimeProvider.System).Enabled);
    }

    [Fact]
    public void Executions_are_confirmed_by_the_person_by_default()
    {
        var human = new HumanConfirmation(new RabbitMcpOptions());
        Assert.False(human.Applies("declare_exchange", Args(new { name = "e" })));
        Assert.True(human.Applies("declare_exchange", Args(new { name = "e", confirm = true })));
        Assert.True(human.Applies("declare_queue", Args(new { name = "q", confirm = true })));
    }
}
