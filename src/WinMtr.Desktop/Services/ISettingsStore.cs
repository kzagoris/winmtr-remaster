namespace WinMtr.Desktop.Services;

/// <summary>
/// Holds the accepted settings between runs. Loading never throws: a store
/// that cannot read its content reports the defaults. Saving returns whether
/// the values reached their store, so a lost choice can be reported once.
/// </summary>
public interface ISettingsStore
{
    AcceptedSettings Load();

    bool Save(AcceptedSettings settings);
}
