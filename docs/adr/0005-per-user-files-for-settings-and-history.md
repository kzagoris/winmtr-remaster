# Per-user files for settings and target history

v0.92 kept accepted settings and the host list in the Windows registry, under `HKCU\Software\WinMTR`. The remaster publishes for Windows and for Linux, so the registry cannot hold them. Accepted settings and target history are stored instead as two JSON files in the per-user local application data folder: `settings.json` and `history.json`, under a `WinMTR` folder that `Environment.SpecialFolder.LocalApplicationData` resolves per platform.

Avalonia offers no settings API; its own guidance is this same pattern, and `IStorageProvider` covers files the operator picks, not application state. `Microsoft.Extensions.Configuration` reads but cannot write, and `ConfigurationManager` is neither per-user nor cross-platform. The two files are separate because they are written at different moments — settings on confirmation, history when a target is accepted for a trace session or the history is cleared — so a damaged host list cannot cost the operator their settings.

The previous version's registry values are read once per absent file, as a **Legacy import**, and are never written back. Because the import runs only while a file is absent, no settings file is written until the operator makes a real choice.

## Consequences

The remaster reads the registry but never owns it, so the two versions can be installed together and v0.92 keeps working unchanged. The store is per machine, not roaming, which suits probe settings and traced hosts. The AOT publish forbids reflection-based serialization, so the files are read and written through a source-generated `JsonSerializerContext`; adding a persisted setting means extending that context, not only the record. A damaged or unreadable file falls back to defaults silently and is replaced by the next normal write, so no diagnostic path is kept for it. Two running instances do not coordinate: the last write wins.
