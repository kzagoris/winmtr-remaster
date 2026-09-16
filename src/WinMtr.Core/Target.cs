using System.Net;

namespace WinMtr.Core;

/// <summary>
/// What we are tracing. <paramref name="Expression"/> is what the user typed,
/// classified and kept for the report header and the target history;
/// <paramref name="Address"/> is what gets probed. v0.92 conflated the two.
/// <paramref name="CanonicalName"/> is the name DNS answered with, null when the
/// user gave a literal address and no lookup was needed.
/// </summary>
public sealed record Target(TargetExpression Expression, IPAddress Address, string? CanonicalName);
