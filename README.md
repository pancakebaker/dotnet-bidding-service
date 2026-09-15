# Distributed Bidding Service

[![CI](https://github.com/pancakebaker/dotnet-bidding-service/actions/workflows/validation.yml/badge.svg)](https://github.com/pancakebaker/dotnet-bidding-service/actions/workflows/validation.yml)

Standalone .NET runtime for the authoritative bidding boundary of the Distributed Bidding Auction Platform.

## Responsibility

This repository owns the Bidding HTTP API, Auction/Bid/Tenant domain, Buy Now
rules, ClientApplication and ClientCredential authentication, tenant runtime
status enforcement, Bidding PostgreSQL persistence, tenant lifecycle history,
and the transactional outbox. The Auction Scheduler and Outbox Publisher are
included because they read and write the same Bidding database and share its
producer-side integration contracts.

Bidding PostgreSQL is authoritative for Tenant, Auction, Bid, SaleMode,
BuyNowPrice, winner/final amount, aggregate version, client credentials, and
outbox state. Other services must not write these tables or re-derive these
decisions.

This repository does not own Laravel/React, Live Feed, the Operations Portal or
its activity database, Live Feed Redis projections, or platform-wide Docker
orchestration.

## Related repositories

- [Laravel React Auction Web](https://github.com/pancakebaker/laravel-react-auction-web) is the tenant-facing BFF and client.
- [Live Feed](https://github.com/pancakebaker/nodejs-live-feed) consumes published integration events for realtime delivery.
- [Operations Portal](https://github.com/pancakebaker/dotnet-blazor-operations-portal) consumes events into its own activity/history projection.
- [DBAP Platform Infrastructure](https://github.com/pancakebaker/docker-dbap-platform) provides development PostgreSQL, RabbitMQ, and Redis.
- [Historical integrated monorepo](https://github.com/pancakebaker/distributed-bidding-auction-platform) preserves the original platform snapshot.

The [platform architecture map](https://github.com/pancakebaker/docker-dbap-platform/blob/main/docs/architecture.md)
shows how this authoritative boundary connects to the independent consumers.
This is a functioning architecture and portfolio/demo runtime, not a complete
production-hardening package. No license file is currently included in this
extracted repository.

## Runtime components

- `src/bidding-service`: ASP.NET Core API and authoritative domain runtime.
- `src/auction-scheduler`: worker that closes eligible expired auctions using
  Bidding PostgreSQL and writes lifecycle events to the outbox.
- `src/outbox-publisher`: worker that claims unpublished outbox rows with
  PostgreSQL locking and publishes confirmed messages to RabbitMQ.
- `contracts/integration-contracts`: producer identifiers, payloads, and v1
  compatibility fixtures. No NuGet package is created yet.

The flow is:

```text
HTTP commands -> Bidding domain -> PostgreSQL transaction + outbox
                                      |
                                      v
                             Outbox Publisher -> RabbitMQ
                                      |
                         Live Feed / Operations consumers
```

RabbitMQ is deliberately outside the request transaction. A broker outage
does not roll back a committed Bidding state change; the Outbox Publisher
retries the durable intent.

## API boundaries

The API exposes tenant-scoped auction reads and bid/Buy Now mutations, system
tenant administration, credential provisioning, health/diagnostic surfaces,
and the trusted `POST /internal/live-feed/access` decision endpoint.

Human JWTs carry tenant and permission context. ClientApplication assertions
use RS256 with `iss`, `sub`, `aud`, `kid`, `jti`, `iat`, `nbf`, and `exp`; replay
protection uses Redis when admission is enabled. Live Feed access uses a
separate short-lived service identity and authoritative Auction-to-Tenant
lookup. SystemAdministrators use a separate authentication scheme and are not
tenant users or bidders.

Tenant status semantics remain unchanged: Active permits reads and mutations,
Suspended permits reads but denies mutations, Disabled denies tenant-facing
access, and missing tenants or database lookup failures fail closed. Lifecycle
transitions, history, and their outbox event are committed atomically.

## Buy Now and events

The domain supports `AuctionOnly`, `BuyNowOnly`, and `AuctionAndBuyNow`.
Explicit Buy Now uses the stored fixed `BuyNowPrice`. A threshold bid at or
above that price is normalized to the authoritative price. Buy Now terminal
transitions emit `AuctionPurchased` and `AuctionClosed`; threshold paths also
retain the accepted bid event as defined by the current contract. Companion
events may share an aggregate version; `eventId` identifies event uniqueness.

Current producer routing keys include `auction.bid.accepted`,
`auction.purchased`, `auction.closed`, `auction.winner.selected`,
`auction.cancelled`, and `tenant.status.changed`. The v1 fixtures under
`contracts/integration-contracts/fixtures/v1` are copied compatibility
baselines for the existing `BidAccepted`, `AuctionClosed`, and `WinnerSelected`
wire formats.

## Local setup

Prerequisites: .NET 10 SDK, PostgreSQL, RabbitMQ, and Redis when client
assertion admission is enabled. Use the safe placeholders in `.env.example`
or environment-specific configuration. Never commit private keys or real
credentials. Public verification keys are supplied through local `keys/` paths
and are ignored by Git.

Restore, build, and test from the repository root:

```text
dotnet restore
dotnet build --no-restore --warnaserror
dotnet test --no-build --no-restore
```

Apply the Bidding schema with:

```text
dotnet ef database update --project src/bidding-service/bidding-service.csproj --startup-project src/bidding-service/bidding-service.csproj
```

Run the API or workers separately:

```text
dotnet run --project src/bidding-service/bidding-service.csproj
dotnet run --project src/auction-scheduler/auction-scheduler.csproj
dotnet run --project src/outbox-publisher/outbox-publisher.csproj
```

## Validation

The standalone workflow provisions PostgreSQL, RabbitMQ, and Redis, waits for
health checks, restores the solution, builds with analyzers and warnings as
errors, and runs the complete Bidding, Scheduler, and Outbox test projects.
Database integration tests create or migrate their own isolated test
databases. PostgreSQL and RabbitMQ are runtime dependencies for the workers;
Redis is specifically the replay store dependency for client assertion
admission, not Live Feed projection state.
