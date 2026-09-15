# Bidding Integration Contracts

This project contains the C# event envelope and payload types emitted by the
authoritative Bidding runtime. It is kept local to this standalone repository
until the repository boundaries and package distribution strategy are settled.

## Contract boundary

Bidding owns the producer-side domain semantics and publishes integration
events through the transactional outbox. The Scheduler writes lifecycle
events to that outbox, and the Outbox Publisher sends them to RabbitMQ.
Consumers such as Live Feed and the Operations Portal remain external services.
This project does not contain their implementations or transport code.

Event envelopes preserve the event identifier, event type, aggregate type and
identifier, aggregate version, occurred-at timestamp, correlation identifier,
tenant context where applicable, and the event-specific payload.

## Versioned fixtures

`fixtures/v1/` contains human-readable golden JSON baselines for the current
wire contract:

- `auction-bid-accepted.json`
- `auction-closed.json`
- `winner-selected.json`

The v1 fixtures are compatibility baselines. Producers and consumers in other
languages must remain compatible with them. A breaking wire change requires a
new fixture version rather than silently changing the existing v1 files.

The fixtures are tested by the Bidding contract tests. Independent NuGet/npm
contract packaging and generated schemas are intentionally deferred until the
repository boundaries are established.
