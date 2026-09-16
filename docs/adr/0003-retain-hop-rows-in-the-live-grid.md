# Retain hop rows in the live grid

ADR 0001 trims trailing silence from snapshots, so route length grows and shrinks while a trace runs. The Avalonia grid instead retains every hop row once it has appeared, for the rest of the trace session, because a row disappearing beneath the pointer reads as a defect rather than as honesty about the route. This is a presentation rule only: snapshots and every report stay trimmed, so the divergence never reaches a file the user exports.

## Consequences

The grid deliberately shows more rows than the current snapshot contains, and a reader comparing the window against an exported report will find extra trailing rows in the window. Retention is scoped to the trace session and resets when a new one starts. Rows are reconciled by hop index against stable row view models, which is what makes retention expressible at all; a design that rebuilt the collection per snapshot could not implement this rule.
