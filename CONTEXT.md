# WinMTR tracing

WinMTR observes a route through repeated probes and presents hop identities, probe results, and accumulated statistics.

## Language

**Probe outcome**:
The broad result of one probe attempt: the destination replied, a router replied because the transit TTL expired, no reply arrived, the destination was reported unreachable, or probing failed.

**Probe error reason**:
The specific condition explaining an unreachable or failed probe, such as host unreachable or insufficient resources. It describes the probe attempt and does not necessarily identify a fault in the remote hop.
_Avoid_: Hop fault, hostname error

**Probe error description**:
The human-readable wording of a probe error reason, presented separately from the hop's hostname or address.

**Latest probe state**:
The outcome and optional error reason of the most recent probe attempt for a hop. Each new attempt replaces the previous state; this is not a history of past errors.

**Probe interval**:
The shortest time between two probes of the same hop, measured from the end of one probe to the start of the next. It is a minimum, not a schedule: a hop sends again only after its last probe gives an answer or times out, so a hop that loses answers probes less often than a hop that does not. Hops therefore do not share one observation window.
_Avoid_: probe rate, sampling rate, ping interval

**Responding hop**:
A hop with a known address because it replied at least once.
_Avoid_: answering hop, live host

**Silent hop**:
A hop with no known address because it never replied.
_Avoid_: empty host, missing host

**Trailing silence**:
Silent hops after the last responding hop. Snapshots trim these so reports grow organically; intermittent silence between responders is kept.
_Avoid_: empty hosts, filler rows

**Hop limit**:
The maximum TTL probed in a session, bounding discovery even when the destination stays silent.
_Avoid_: MaxHops, max hops

**Route length**:
The number of hops in the current trimmed snapshot, from the first TTL through the last responding hop or confirmed destination; trailing hops repeating the previous responder are trimmed.
_Avoid_: hop count, path length

**Active extent**:
The span of TTLs a trace session currently probes, from the first TTL through headroom past the last responding hop or a confirmed destination, never beyond the hop limit. It rises with responding or sweep evidence and falls only when the confirmed destination moves nearer or is invalidated.
_Avoid_: probe window, working set

**Route snapshot**:
The immutable state of the whole route at one instant, published repeatedly while a trace runs. Two snapshots taken at the same instant are indistinguishable to a reader; a snapshot never changes after publication.
_Avoid_: trace state, route update, tick

**Trace session**:
One run of a trace, from the moment a target is accepted through to stopping. It carries the settings the run began with and outlives no snapshot. Settings belong to the session and do not change while it runs.
_Avoid_: trace, connection, job

**Retained hop rows**:
The hops shown at least once during a trace session. Whether a presentation keeps showing rows that later snapshots trim is a consumer choice, unchanged by this change; this is not the **Active extent**, which is what the session probes.
_Avoid_: high-water mark, max rows, displayed extent

**Accepted settings**:
The settings a confirmation established, held between trace sessions and copied into the next one at its start. Editing settings produces new accepted settings only on confirmation; a running **Trace session** keeps the settings it began with.
_Avoid_: current settings, global settings, live settings

**Target history**:
The targets accepted for a trace before, ordered newest first and free of duplicates. Accepting a target already present moves it to the newest position instead of adding a second entry.
_Avoid_: LRU, recent hosts, MRU list

**History size**:
The maximum number of entries the **Target history** keeps. Lowering it drops the oldest entries at once.
_Avoid_: MaxLRU, history depth

**Legacy import**:
The one-time adoption of **Accepted settings** or **Target history** from the previous WinMTR version, done only while this application has stored none of its own. It reads the previous version's store and never writes to it.
_Avoid_: migration, upgrade, sync

**Latency outlier**:
A latency measurement that is a spike above the same hop's typical latency. A hop that is merely far away is not an outlier.
_Avoid_: slow hop, high latency

**Loss level**:
The grade of a hop's packet loss, from none through low, moderate, and high to total.
_Avoid_: loss severity, loss class
