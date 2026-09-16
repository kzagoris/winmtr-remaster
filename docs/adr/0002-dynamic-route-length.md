# Dynamic route length under a fixed hop limit

Status: Accepted

Snapshots already trim trailing silence so reports grow organically, but `TraceState` still pre-allocates `Hop[MaxHops]` and the engine pre-spawns that many workers. We keep the user-configured ceiling as `HopLimit` (ex-`MaxHops`, default 30, 1..255) and make route length dynamic beneath it: 3-TTL growth (headroom 1 plus a 2-TTL silent reach) with fast-start 8, shrink trims display while backing rows and parked workers are kept, workers spawn lazily and never terminate, with `HopLimit` renames kept behind `[Obsolete]` aliases.

## Considered Options

- Remove the limit entirely: rejected, a silent destination would grow workers unboundedly.
- Compact backing rows and terminate workers on shrink: rejected, loses stats history and churns tasks on flap.
- Pure lazy growth from TTL 1: rejected, it converges one hop per cadence (~15 cadences for a 15-hop route vs ~1 today). The chosen fast-start 8 plus headroom needs ~4 cadences for the same route: 8 probed immediately, about one cadence per 3 further hops.

## Consequences

- `PathLength.Determine` drops the ceiling parameter; only `TraceState`/`ProbeSettings` know the limit, and `HopLimitObserved` stays `ttl == ceiling` so trimming never erases limit evidence.
