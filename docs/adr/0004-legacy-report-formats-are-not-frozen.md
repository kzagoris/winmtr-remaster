# Legacy report formats are not frozen

The remaster's original design pinned the text and HTML reports to v0.92 byte-for-byte, and `HopStatistics.LegacyLossPercent` exists solely to reproduce v0.92's integer loss arithmetic for them. That constraint was already abandoned in practice: both renderers emit a `Status` column that v0.92 never had, so the golden file pins the remastered format rather than the legacy one. We are retiring the constraint explicitly instead of leaving stale documentation asserting a fidelity the code does not provide.

## Consequences

The text and HTML renderers become ordinary formats alongside CSV and JSON, free to change their headers, column widths, and null handling. `LegacyLossPercent` loses its only caller and is removed, leaving `LossPercent` as the single loss formula in the codebase; the golden report file is regenerated, and the design spec's byte-exactness claim is rewritten. Output pasted into a ticket from a remastered build will not match output from v0.92. The fixed-width text layout is kept because it is what makes these reports pasteable, which is their actual use.
