# Auction Scheduler

Phase 7 implements this worker as a small .NET 10 background service.

The scheduler closes auctions using authoritative server UTC and PostgreSQL state. It only processes auctions where `Status = Open` and `EndTimeUtc <= UTC now`; scheduled, cancelled, and already closed auctions are ignored.

Each auction close runs in a PostgreSQL transaction:

1. Claim one eligible auction row with `FOR UPDATE SKIP LOCKED`.
2. Re-check the locked row is still open and expired.
3. Determine the winner from accepted bid state.
4. Set `Auction.Status = Closed`.
5. Increment `Auction.Version` exactly once.
6. Persist `AuctionClosed` to the transactional outbox.
7. Persist `WinnerSelected` when a winning bid exists.
8. Commit.

The scheduler does not publish to RabbitMQ directly. The existing outbox publisher later sends lifecycle events to the `auction.events` topic exchange.

Development defaults live in `appsettings.json` and may be overridden through standard .NET configuration keys such as `ConnectionStrings__BiddingDb`, `Scheduler__PollIntervalSeconds`, and `Scheduler__BatchSize`.