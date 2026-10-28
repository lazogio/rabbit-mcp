# როგორ მუშაობს rabbit-mcp

[English](architecture.md) · **ქართული**

სამი დიაგრამა კოდზე მომუშავეთათვის: კომპონენტები, `request_*` გამოძახების გზა და `_error` რიგიდან აღდგენა. წესები,
რომლებსაც ისინი ეყრდნობა, [CLAUDE.md](../CLAUDE.md)-შია.

## კომპონენტები

ყოველი გამოძახება ჯერ call-tool ფილტრებს გადის, მერე ხელსაწყო რეესტრით ამოწმებს შეტყობინებას და `RabbitConnection`-ით
აგზავნის. Management HTTP API მხოლოდ `list_queues`-ს იყენებს.

```mermaid
flowchart LR
    client["MCP კლიენტი<br/>Claude, IDE, აგენტი"]
    contracts["კონტრაქტები<br/>ContractAssemblies (DLL)<br/>AsyncAPI ფაილები<br/>ან ჩაშენებული Samples"]

    subgraph server["rabbit-mcp (ServerComposition.Compose)"]
        filters["call-tool ფილტრები<br/>როლები → ლიმიტი → ადამიანის დასტური<br/>BrokerErrors → McpException · AuditLog"]
        send["გაგზავნა<br/>publish_* · request_* · reply_*"]
        recover["აღდგენა<br/>peek_messages · republish_* · dead_letter_*"]
        observe["ხედვა<br/>list_queues · queue_stats · read_stream"]
        registry["ContractRegistry + ContractValidator<br/>სახელები, URN, exchange · DataAnnotations / JSON Schema"]
        messaging["RabbitConnection<br/>MassTransit envelope ან Raw JSON · ReplyListener<br/>publisher confirms + mandatory · QueueBrowser"]
        filters --> send & recover & observe
        send & recover --> registry --> messaging
    end

    subgraph rabbit["RabbitMQ"]
        exchange["exchange Namespace:Type"]
        queue["queue submit-order"]
        error["submit-order_error"]
        reply["დროებითი reply exchange<br/>autoDelete, fanout"]
    end

    services["სერვისები<br/>MassTransit 8 consumers<br/>ან RabbitMQ.Client (Raw)"]

    client -- "stdio / HTTP" --> filters
    contracts -- "ჩატვირთვა" --> registry
    messaging -- "AMQP 5672" --> exchange
    observe -. "HTTP API 15672" .-> rabbit
    exchange --> queue
    queue -- "consume" --> services
    services -- "fault" --> error
    services -- "respond" --> reply
```

## `request_*` გზა

Raw კონტრაქტებისთვის იგივე გზაა RabbitMQ RPC-ით: `reply_to` = reply რიგი, დამთხვევა `correlation_id`-ით, პასუხის ტიპი
AMQP `type`-დან და არასდროს body-დან. `publish_*` confirm-ზე ჩერდება.

```mermaid
sequenceDiagram
    participant M as მოდელი
    participant S as rabbit-mcp
    participant R as RabbitMQ
    participant C as Consumer
    M->>S: request_CheckStock {…}
    S->>S: როლი, ლიმიტი, დასტური
    S->>S: ვალიდაცია (უარი → isError)
    S->>R: reply exchange + queue (temporary)
    S->>R: envelope: requestId, responseAddress
    R-->>S: publisher confirm
    R->>C: Namespace:Type → queue
    C->>R: RespondAsync → responseAddress
    R->>S: ReplyListener: requestId-ით
    S-->>M: structuredContent (ან Fault / timeout)
```

## აღდგენა `_error` რიგიდან

ყოველი გადატანა: get → confirmed publish → ack; წყარო ack-დება მხოლოდ confirm-ის შემდეგ, წაშლის ხელსაწყო არ არსებობს.
სამივე ნაგულისხმევად გამორთულია (`EnableRepublish` / `EnablePublish`) და audit-ში იწერება. Republish უარს ამბობს, თუ
`MT-Fault-InputAddress` error რიგის მფლობელს არ ემთხვევა.

```mermaid
flowchart LR
    consumer["consumer<br/>exception"] -- "fault" --> error["submit-order_error<br/>MT-Fault-* / x-death"]
    error -- "get + nack" --> peek["peek_messages<br/>diagnose_errors"]
    peek --> republish["republish_from_error<br/>თავდაპირველ რიგში"]
    peek --> corrected["resend_corrected<br/>ახალი, გასწორებული შეტყობინება"]
    peek --> dead["dead_letter_from_error<br/>პარკინგი, ყველა header"]
```
