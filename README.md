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

### Prerequisites

Install the .NET 10 SDK. A local runtime also needs PostgreSQL for the API,
Scheduler, and Outbox Publisher. RabbitMQ is needed by the Outbox Publisher to
deliver events. Redis is needed only when client-assertion admission is
enabled. The [DBAP Platform Infrastructure repository](https://github.com/pancakebaker/docker-dbap-platform)
provides the development PostgreSQL, RabbitMQ, and Redis services used by the
sample configuration.

The API's Development launch profile uses `http://localhost:5000` and
`https://localhost:7102`. Swagger is enabled only in Development. The health
endpoint is `GET http://localhost:5000/health`; the development Swagger UI is
`http://localhost:5000/swagger/index.html`.

### Local configuration

This repository uses standard .NET JSON configuration and environment
variables. It does **not** load `.env` files automatically. `.env.example` is
therefore a reference template, not a file that becomes active merely by being
copied. You can copy it for reference, but apply overrides through environment
variables, `appsettings.Development.json`, or an ignored
`appsettings.Development.local.json` file:

Unix/macOS:

```bash
cp .env.example .env
export ConnectionStrings__BiddingDb='Host=127.0.0.1;Port=55432;Database=auction_demo;Username=auction_app;Password=change_me_in_local_env'
export ConnectionStrings__ClientAssertionRedis='localhost:6379'
```

Windows PowerShell:

```powershell
Copy-Item .env.example .env
$env:ConnectionStrings__BiddingDb = 'Host=127.0.0.1;Port=55432;Database=auction_demo;Username=auction_app;Password=change_me_in_local_env'
$env:ConnectionStrings__ClientAssertionRedis = 'localhost:6379'
```

The API and workers already contain Development defaults for the demo database
and RabbitMQ. Review the values before running locally. The important
configuration names are:

| Setting | Used by | Purpose | Required locally? |
| --- | --- | --- | --- |
| `ConnectionStrings__BiddingDb` | API, Scheduler, Publisher | Authoritative Bidding PostgreSQL | Yes |
| `ConnectionStrings__ClientAssertionRedis` | API | Client-assertion replay protection | Only when `ClientAssertionAdmission__Enabled=true` |
| `ClientAssertionAdmission__Enabled` | API | Enables signed client-assertion admission | No; Development default is `false` |
| `Authentication__BiddingService__*` | API | Laravel/BFF JWT issuer, audience, key ID, and public key path | Needed for authenticated tenant API calls |
| `Authentication__SystemAdmin__*` | API | System-administrator JWT trust settings and public key path | Needed for admin API calls |
| `LiveFeedServiceAuthentication__*` | API | Trusted Live Feed service issuer, subject, audience, key ID, and public key path | Needed for Live Feed internal access calls |
| `Database__ApplyMigrations` | API | Applies EF migrations during API startup | Development default is `true` |
| `Database__SeedDemoData` | API | Seeds deterministic local demo data | Development default is `true` |
| `OUTBOX_PUBLISHER_RABBITMQ_*` | Publisher | RabbitMQ host, credentials, vhost, exchange, and debug queue | Required by Publisher when overrides are needed |

Use double underscores for nested .NET configuration keys. The complete
placeholder list, including the exact RabbitMQ names, is in `.env.example`.
Never commit real credentials or private keys.

### Local keys

The Bidding Service verifies tokens; it does not own the private keys used to
sign them. Place the corresponding public PEM files at the paths configured by
the API, normally:

```text
keys/bidding-service-public.pem
keys/system-admin-public.pem
keys/live-feed-service-public.pem
```

The Laravel/BFF and SystemAdministrator installations retain their private
signing keys. Live Feed also retains its private key. This repository needs
only the matching public verification material. No supported key-generation
command is included here, so obtain the public keys from the local service
installations or your development key-provisioning process. The `keys/`
directory and PEM files are ignored by Git.

In Development, missing API and system-admin public files do not prevent the
process from booting because the API uses a temporary development fallback
key; real authenticated requests still require matching configured public
keys. The Live Feed public key is required when validating an internal Live
Feed request. Production validates configured issuer, audience, key ID, and
public-key files and must not use these development fallbacks.

When client assertion admission is enabled, the client application presents a
signed assertion using its own private key. Register its public key with the
internal provisioning command; the private key is never read by or sent to
this repository:

```text
dotnet run --project src/bidding-service/bidding-service.csproj -- provision --client-id <client-id> --key-id <key-id> --public-key-path <public-key.pem>
```

### Restore, database, build, and test

From the repository root:

```text
dotnet restore
dotnet ef database update --project src/bidding-service/bidding-service.csproj --startup-project src/bidding-service/bidding-service.csproj
dotnet build --no-restore --warnaserror
dotnet test --no-build --no-restore
```

PowerShell-friendly migration command:

```powershell
dotnet ef database update --project src/bidding-service/bidding-service.csproj --startup-project src/bidding-service/bidding-service.csproj
```

The API applies migrations at startup when `Database__ApplyMigrations=true`.
The explicit EF command is useful for inspecting or preparing the schema first.
With `Database__SeedDemoData=true`, the initializer seeds the demo tenant,
client application, deterministic auctions, and bid history when the database
does not already contain auctions. It does not create human login accounts or
client private keys. The seed is intended for local/demo use; production
deployments should disable demo seeding and use controlled migrations and
provisioning.

### Run the API and workers

Start the API in one terminal:

```text
dotnet run --project src/bidding-service/bidding-service.csproj
```

Then start each worker in its own terminal as needed:

```text
dotnet run --project src/auction-scheduler/auction-scheduler.csproj
dotnet run --project src/outbox-publisher/outbox-publisher.csproj
```

The Scheduler reads PostgreSQL and closes eligible expired auctions, writing
their lifecycle events to the outbox. The Outbox Publisher reads PostgreSQL
and requires RabbitMQ to publish confirmed events. The API itself requires
PostgreSQL for startup initialization; Redis is contacted for client-assertion
replay protection only when that admission policy is enabled.

For a complete local run, start PostgreSQL first, then the API, and start the
Outbox Publisher with RabbitMQ available when event delivery is required. Add
Redis before enabling `ClientAssertionAdmission__Enabled`.

## Validation

The standalone workflow provisions PostgreSQL, RabbitMQ, and Redis, waits for
health checks, restores the solution, builds with analyzers and warnings as
errors, and runs the complete Bidding, Scheduler, and Outbox test projects.
Database integration tests create or migrate their own isolated test
databases. PostgreSQL and RabbitMQ are runtime dependencies for the workers;
Redis is specifically the replay store dependency for client assertion
admission, not Live Feed projection state.
