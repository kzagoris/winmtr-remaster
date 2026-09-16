// tests/WinMtr.TestSupport/ScriptedProbeChannel.cs
using System.Net;
using WinMtr.Core;
using WinMtr.Core.Probing;

namespace WinMtr.TestSupport;

/// <summary>
/// A single probe call, recorded for assertions.
/// </summary>
public sealed record ProbeCall(IPAddress Dest, int Ttl, int PayloadBytes, TimeSpan Timeout);

/// <summary>
/// Deterministic offline <see cref="IProbeChannel"/> substitute. Supplies the
/// probe outcomes the console and front-end contracts need — responding hops,
/// silent hops, unreachable reports with a probe error description, and
/// changing hop identities — without sending traffic.
/// </summary>
/// <remarks>
/// Glossary: responding hop, silent hop, latest probe state, probe error
/// description. A silent hop is a <see cref="ProbeResult"/> with a null
/// responder (ordinarily <see cref="ProbeOutcome.TimedOut"/>); a changing hop
/// identity is successive results with different non-null responders for the
/// same TTL.
/// </remarks>
public sealed class ScriptedProbeChannel : IProbeChannel
{
    private readonly Func<IPAddress, int, int, TimeSpan, CancellationToken, Task<ProbeResult>>? _sendAsync;
    private readonly IReadOnlyList<ProbeResult>? _script;
    private readonly List<ProbeCall> _calls = [];
    private int _count;

    /// <summary>
    /// A channel that times every probe out. Keeps the constructor call sites
    /// for the common "no traffic, no answers" case to one line.
    /// </summary>
    public ScriptedProbeChannel(ProbeCapabilities capabilities)
        : this(capabilities, (IReadOnlyList<ProbeResult>?)null)
    {
    }

    /// <summary>
    /// A channel replaying a scripted outcome sequence. When the script is
    /// exhausted the last outcome repeats, so a long trace session observes a
    /// stable answer rather than throwing. Null or empty script times out.
    /// </summary>
    public ScriptedProbeChannel(ProbeCapabilities capabilities, IReadOnlyList<ProbeResult>? script)
    {
        Capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _script = script;
    }

    /// <summary>
    /// A channel answering via a delegate, for per-TTL or per-call behaviour
    /// such as a hop identity that changes mid-session.
    /// </summary>
    public ScriptedProbeChannel(
        ProbeCapabilities capabilities,
        Func<IPAddress, int, int, TimeSpan, CancellationToken, Task<ProbeResult>> sendAsync)
    {
        Capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _sendAsync = sendAsync ?? throw new ArgumentNullException(nameof(sendAsync));
    }

    public ProbeCapabilities Capabilities { get; }

    /// <summary>How many probes have been sent through this channel.</summary>
    public int TotalProbes => Volatile.Read(ref _count);

    /// <summary>Each probe call in order, for assertions on TTL coverage.</summary>
    public IReadOnlyList<ProbeCall> Calls
    {
        get
        {
            lock (_calls)
                return _calls.ToArray();
        }
    }

    public Task<ProbeResult> SendAsync(
        IPAddress dest,
        int ttl,
        int payloadBytes,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dest);

        // Probe implementations must honor cancellation promptly; shutdown
        // drains owned work, and a non-cancellable seam can stall it.
        ct.ThrowIfCancellationRequested();

        lock (_calls)
            _calls.Add(new ProbeCall(dest, ttl, payloadBytes, timeout));
        int index = Interlocked.Increment(ref _count) - 1;

        if (_sendAsync is not null)
            return _sendAsync(dest, ttl, payloadBytes, timeout, ct);

        if (_script is null || _script.Count == 0)
            return Task.FromResult(new ProbeResult(ProbeOutcome.TimedOut, null, 0));

        ProbeResult result = _script[Math.Min(index, _script.Count - 1)];
        return Task.FromResult(result);
    }
}
