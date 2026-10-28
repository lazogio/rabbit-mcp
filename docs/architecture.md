# How rabbit-mcp works

**English** · [ქართული](architecture.ka.md)

Three diagrams for people working on the code: the components, the path of a `request_*` call, and recovery from an
`_error` queue. The rules behind them are in [CLAUDE.md](../CLAUDE.md).

## Components

Every call passes the call-tool filters first, then the tool validates the message through the registry and sends it
with `RabbitConnection`. The management HTTP API is used only by `list_queues`.

```mermaid
flowchart LR
    client["MCP client<br/>Claude, IDE, agent"]
    contracts["Contracts<br/>ContractAssemblies (DLL)<br/>AsyncAPI files<br/>or bundled Samples"]

    subgraph server["rabbit-mcp (ServerComposition.Compose)"]
        filters["call-tool filters<br/>roles → rate limit → human confirmation<br/>BrokerErrors → McpException · AuditLog"]
        send["Send<br/>publish_* · request_* · reply_*"]
        recover["Recover<br/>peek_messages · republish_* · dead_letter_*"]
        observe["Observe<br/>list_queues · queue_stats · read_stream"]
        registry["ContractRegistry + ContractValidator<br/>names, URN, exchange · DataAnnotations / JSON Schema"]
        messaging["RabbitConnection<br/>MassTransit envelope or Raw JSON · ReplyListener<br/>publisher confirms + mandatory · QueueBrowser"]
        filters --> send & recover & observe
        send & recover --> registry --> messaging
    end

    subgraph rabbit["RabbitMQ"]
        exchange["exchange Namespace:Type"]
        queue["queue submit-order"]
        error["submit-order_error"]
        reply["temporary reply exchange<br/>autoDelete, fanout"]
    end

    services["Services<br/>MassTransit 8 consumers<br/>or RabbitMQ.Client (Raw)"]

    client -- "stdio / HTTP" --> filters
    contracts -- "load" --> registry
    messaging -- "AMQP 5672" --> exchange
    observe -. "HTTP API 15672" .-> rabbit
    exchange --> queue
    queue -- "consume" --> services
    services -- "fault" --> error
    services -- "respond" --> reply
```

## The `request_*` path

Raw contracts take the same path as RabbitMQ RPC: `reply_to` is the reply queue, matching is by `correlation_id`, and
the reply type comes from the AMQP `type`, never from the body. `publish_*` stops at the confirm.

```mermaid
sequenceDiagram
    participant M as Model
    participant S as rabbit-mcp
    participant R as RabbitMQ
    participant C as Consumer
    M->>S: request_CheckStock {…}
    S->>S: role, rate limit, confirmation
    S->>S: validation (invalid → isError)
    S->>R: declare reply exchange + queue (temporary)
    S->>R: envelope: requestId, responseAddress
    R-->>S: publisher confirm
    R->>C: Namespace:Type → queue
    C->>R: RespondAsync → responseAddress
    R->>S: ReplyListener matches requestId
    S-->>M: structuredContent (or Fault / timeout)
```

## Recovering from an `_error` queue

Every move is get → confirmed publish → ack; the source is acked only after the confirm, and there is no delete tool.
All three are off by default (`EnableRepublish` / `EnablePublish`) and audited. Republish refuses when
`MT-Fault-InputAddress` disagrees with the error queue's owner.

```mermaid
flowchart LR
    consumer["consumer<br/>exception"] -- "fault" --> error["submit-order_error<br/>MT-Fault-* / x-death"]
    error -- "get + nack" --> peek["peek_messages<br/>diagnose_errors"]
    peek --> republish["republish_from_error<br/>back to the original queue"]
    peek --> corrected["resend_corrected<br/>a new, corrected message"]
    peek --> dead["dead_letter_from_error<br/>parked, all headers kept"]
```
