---
title: Messaging brokers (NATS, AMQP 1.0, Kafka)
translation-status: synced
---

# Messaging brokers: NATS, AMQP 1.0 and Kafka

**Summary.** Plants and fleets often already run a message broker, and gateways have to feed it. These three adapters
follow the curation rule of IoTCom.Net: the mature client library is wrapped, not rebuilt, so each protocol becomes one
more `IClientEndpoint` with `IPublisher<ReadOnlyMemory<byte>>`, `ISubscriber<ReadOnlyMemory<byte>>`, the traffic tap,
metrics (`MessagesPublished`, `MessagesReceived`) and hosting helpers. Switching a gateway from MQTT to one of them
changes the endpoint, not the code around it.

- `IoTCom.Net.Adapters.Nats`: `NatsEndpoint` over NATS.Client.Core 3.3.0.
  - subjects and wildcards (`plant.*.temp`, `plant.>`), headers and content type
  - `ReceiveAsync` with an optional queue group (each message goes to one member)
  - `RequestAsync` and `ServeAsync` for request/reply, with `DeviceException` when nobody listens and
    `IoTComTimeoutException` when the answer is late
  - user and password, token, or a `.creds` file
- `IoTCom.Net.Adapters.Amqp`: `AmqpEndpoint` over AMQPNetLite.Core 2.5.4 (AMQP 1.0, the OASIS standard).
  - publish to an address; `AtMostOnce` sends pre-settled, `AtLeastOnce` and `ExactlyOnce` wait for the broker's
    outcome (`DeviceException` on reject)
  - a subscription is a receiver link; a message is accepted when the consumer has processed it and moves on, so a
    consumer that stops mid-way leaves it for redelivery
  - content type in the message properties, credit-based flow control (`ReceiverCredit`)
  - `AmqpMiniBroker`: a small in-process broker (in-memory queues, competing consumers, redelivery, optional SASL
    PLAIN credentials) for tests, notebooks and demos. It is not for production and persists nothing.
- `IoTCom.Net.Adapters.Kafka`: `KafkaEndpoint` over Confluent.Kafka 2.16.0 (librdkafka).
  - QoS maps to producer settings: `AtMostOnce` is acks 0, `AtLeastOnce` acks 1, `ExactlyOnce` acks all with the
    idempotent producer
  - `PublishAsync` with key and headers; `ReceiveAsync` returns topic, key, value, partition, offset and headers
  - subscribe to a topic, or to every topic matching a regular expression when the filter starts with `^`
  - consumer group, start offset (earliest by default), offsets committed after each record has been handed over
    (at-least-once), TLS and SASL options

Hosting: `AddNats`, `AddAmqp` and `AddKafka` register an endpoint that connects when the host starts. They live in each
adapter package, so none of the three is in the `IoTCom.Net` meta-package. The Kafka adapter is not trimmable or
AOT-compatible (like OPC UA).

## When to use it

- A gateway that reads Modbus, CAN or BLE devices and publishes to the plant's existing NATS, Kafka or AMQP broker.
- Request/reply with edge services over NATS, without writing a protocol.
- Fan-out and replay of telemetry through Kafka topics with consumer groups.
- Testing AMQP code in-process with `AmqpMiniBroker`, with no broker to install.

For small devices and constrained links MQTT or CoAP is usually the better fit; for peer-to-peer data across a network
without a broker see [Zenoh](zenoh.md).

## Installation

```bash
dotnet add package IoTCom.Net.Adapters.Nats --prerelease
dotnet add package IoTCom.Net.Adapters.Amqp --prerelease
dotnet add package IoTCom.Net.Adapters.Kafka --prerelease
```

## Quickstart

NATS:

```csharp
using IoTCom.Net.Adapters.Nats;

await using var nats = NatsEndpoint.Create(o => o.UseServer("nats://localhost:4222"));
await nats.ConnectAsync();
await nats.PublishAsync("plant.line1.temp", "21.5"u8.ToArray());

await foreach (var m in nats.ReceiveAsync("plant.>", queueGroup: "loggers", ct))
    Console.WriteLine($"{m.Subject}: {Encoding.UTF8.GetString(m.Data.Span)}");

var reply = await nats.RequestAsync("plant.line1.info", ReadOnlyMemory<byte>.Empty);   // needs a responder
```

AMQP 1.0, with the in-process broker:

```csharp
using IoTCom.Net.Adapters.Amqp;

await using var broker = AmqpMiniBroker.Create();
await broker.StartAsync();
await using var amqp = AmqpEndpoint.Create(o => o.UseBroker(broker.Address));
await amqp.ConnectAsync();
await amqp.PublishAsync("plant.temp", "21.5"u8.ToArray(), new PublishOptions { QualityOfService = QualityOfService.AtLeastOnce });
await foreach (var m in amqp.SubscribeAsync("plant.temp", ct)) { /* ... */ }
```

Kafka:

```csharp
using IoTCom.Net.Adapters.Kafka;

await using var kafka = KafkaEndpoint.Create(o => o.UseBootstrap("localhost:9092").WithGroup("gateway"));
await kafka.ConnectAsync();
await kafka.PublishAsync("plant-temp", key: "line1"u8.ToArray(), value: "21.5"u8.ToArray());

await foreach (var r in kafka.ReceiveAsync("plant-temp", ct))
    Console.WriteLine($"{r.Topic}[{r.Partition}]@{r.Offset} {Encoding.UTF8.GetString(r.Value.Span)}");
```

## Security

User names, passwords, tokens, `.creds` files, SASL credentials and TLS key passwords are secrets: keep them in
configuration or a secret store, never in source or logs. Brokers on a plant network should require authentication.
For transport security:

- NATS: the URL and credentials are handed to NATS.Client.Core; use its TLS URL schemes where your server requires TLS.
- AMQP: the address is handed to AMQPNetLite; use an `amqps://` address for TLS. `AmqpMiniBroker` speaks plain TCP and
  is for local tests only.
- Kafka: `WithTls(caLocation)` and `WithSasl(mechanism, user, password, tls: true)` set the librdkafka security
  protocol; `Extra` passes any other librdkafka setting.

Publishing to a broker can command real equipment through whatever consumes the topic. Treat the subjects, addresses
and topics that drive actuators like write access to a PLC: restrict them on the broker.

## Tools

There is no CLI command for these brokers yet. Use the in-process `AmqpMiniBroker` for demos, and the notebook for a
hands-on tour.

## Testing

AMQP tests run against `AmqpMiniBroker` in every build and cover:
- confirmed (at-least-once and exactly-once) and pre-settled publishes round-tripping, and a subscription started
  before the publish;
- content type in the message properties;
- competing consumers sharing a queue without duplicates, and addresses being independent queues;
- an unprocessed message redelivered to the next subscriber;
- a rejected message throwing `DeviceException` when confirmed but not when pre-settled;
- a closed port giving `TransportException`, and credentials checked by the broker;
- disposal ending running subscriptions (and being idempotent), the traffic tap, and the option defaults.

NATS and Kafka tests need a real broker and skip without one. Set `IOTCOM_NATS_URL` (for example
`nats://localhost:4222`) and `IOTCOM_KAFKA_BOOTSTRAP` (for example `localhost:9092`) to run them; the CI job `brokers`
starts both as service containers. The NATS tests cover publish/subscribe, wildcards, queue groups, headers,
request/reply, no responder, a slow responder, and disposal. The Kafka tests cover key, headers and content type, the
three QoS levels, regular-expression subscriptions, committed offsets not being redelivered to the same group, and
disposal. Without a broker, only the option defaults and the failure paths (unreachable broker, empty subject or topic)
run.

## Limitations

- Payloads are bytes (use a payload codec such as SenML or Protobuf for structure); there is no schema registry
  support for Kafka.
- NATS: core NATS only, no JetStream (persistence, replay) API. Kafka: producer and consumer only, no admin or
  transactions API; topics are created on demand if the broker allows it. AMQP: sender and receiver links on named
  addresses only; no transactions or dynamic nodes.
- `AmqpMiniBroker` is a test double, not a broker to deploy.
- The Kafka adapter is not trimmable or AOT-compatible; none of the three is in the meta-package.

## Learn more

Notebook `notebooks/messaging/23-brokers.en.ipynb` · [Zenoh](zenoh.md) · [Endpoints and transports](../concepts/endpoints-and-transports.md)
