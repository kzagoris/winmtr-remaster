namespace WinMtr.Core.Probing;

/// <summary>
/// Turns what the user typed into something probeable, exactly once.
/// v0.92 resolved twice -- <c>InitMTRNet</c> looked the name up to validate it and
/// threw the result away (WinMTRDialog.cpp:965), then <c>PingThread</c> looked it up
/// again and dereferenced the answer unchecked (WinMTRDialog.cpp:1008) -- so a DNS
/// blip between the two calls crashed the trace. One seam, one lookup.
/// This is the forward direction; <see cref="INameResolver"/> is the reverse one.
/// </summary>
public interface ITargetResolver
{
    Task<Target> ResolveAsync(TargetExpression expression, CancellationToken ct);
}
