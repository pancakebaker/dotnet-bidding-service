# Outbox Publisher

Implemented in Phase 4 as a small .NET 10 worker.

The publisher polls PostgreSQL for unpublished transactional outbox rows, claims batches with `FOR UPDATE SKIP LOCKED`, publishes UTF-8 JSON envelopes to RabbitMQ exchange `auction.events`, waits for publisher confirmation, and only then marks rows as published.

Supported routing keys:

| Event | Routing key |
| --- | --- |
| BidAccepted | `auction.bid.accepted` |
| AuctionClosed | `auction.closed` |
| WinnerSelected | `auction.winner.selected` |
| AuctionCancelled | `auction.cancelled` |

The worker provides at-least-once publication semantics. Duplicate delivery remains possible if the process crashes after RabbitMQ confirms a message but before PostgreSQL records `PublishedAtUtc`, so consumers must deduplicate by `eventId`.
