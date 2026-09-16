# The release matrix is Native AOT on four runners, and one of them is a Mac

Both products ship as Native AOT binaries on all five runtime identifiers:
`win-x64`, `linux-x64`, `linux-arm64`, `osx-x64` and `osx-arm64`. The console
already set `PublishAot=true`. The desktop application sets
`IsAotCompatible=true` and demotes IL2104/IL3053, because
`Avalonia.Controls.DataGrid` is not trim-clean and is third-party code we
cannot annotate.

A self-contained framework publish was considered for the desktop application.
It is the safe path, and the application AOT build was proven only on Windows
and Linux. It was rejected because it makes the user download a runtime that
Native AOT removes, and because it would give the desktop application a
different publish shape from the console for no gain the user can see. The
risk is real but narrow: an AOT trimming break appears at run time, on macOS
only, and it is held by two guards. Every Release below `1.0.0` is marked as a
prerelease, and the macOS binaries are run by hand before a release is
announced.

Native AOT cannot cross-compile between operating systems, so each operating
system needs its own runner. It can cross-compile between architectures on the
same operating system, because the Apple clang toolchain holds both Mac
architectures. One `macos-latest` runner therefore builds `osx-arm64` and
`osx-x64`.

Two macOS jobs were considered, one on `macos-15-intel` and one on
`macos-latest`. It was rejected for two reasons. The repository is private, so
macOS minutes bill at ten times the rate, and a second Mac job doubles the most
expensive line in the matrix. More important, GitHub states that
`macos-15-intel` is the last x86_64 macOS image and that it ends in August
2027; a matrix built on it breaks on that date. A dedicated Intel runner buys
nothing, because CI only *builds* the Intel binary there and never runs it.

A universal binary joined with `lipo` was considered and rejected: it doubles
the download size for every user to serve the smaller half of the Mac
population, and it needs a bundle to be worth having.

## Consequences

Four jobs cover five runtime identifiers and ten archives. The Intel Mac binary
is never run on Intel hardware before release, which is also true of the
rejected two-job shape. The workflow needs no change in August 2027, and none
when the repository becomes public.

Each build job runs the fast test suite on its own platform, so the
Windows-only and Linux-only branches are tested where they matter. The matrix
sets `fail-fast: false` so one run reports every failure, and the Release job
needs all four builds, so a Release never ships with a platform missing.

`win-x64-aot.pubxml` is no longer the publish path. The workflow uses one
command shape for all five runtime identifiers, and the profile stays as a
local developer convenience. The IL2104/IL3053 demotion inside the profile is
now duplicated for that use alone.

Binaries are named for the product and the interface, never for the UI
framework: the desktop application ships as `winmtr` and the console as
`winmtr-cli`. A framework name in a binary states an implementation detail that
can change, and that binary is what the user sees in a download folder, in a
task bar and in a `.desktop` file. Each name now agrees with the archive that
holds it. The `AssemblyName` property carries the rename, so project folders,
namespaces and the solution keep their existing names.

One coupling follows from that split. The authority of an `avares://` URI is
the assembly name, not the namespace, so every embedded-asset URI moved with
the binary. `AppAssets.Locate` now reads the name from the running assembly and
is the single place that builds such a URI, so the next rename needs no edit.

The macOS `.app` bundle stays follow-up work. Until it exists, a macOS user
starts `winmtr` from the extracted archive.
