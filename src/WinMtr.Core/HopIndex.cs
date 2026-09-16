namespace WinMtr.Core;

/// <summary>
/// A 0-based position on the route. Owns the conversion to TTL and to the
/// 1-based number shown to users -- in v0.92 that `+ 1` was repeated at every
/// call site and was a steady source of off-by-one errors.
/// </summary>
public readonly record struct HopIndex(int Value)
{
    public int Ttl => Value + 1;
    public int DisplayNumber => Value + 1;
}
