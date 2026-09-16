# Adopt Avalonia 12 and move the desktop tests to xUnit.net v3

The desktop front end moves from Avalonia 11.3.6 to the 12.1.2 line. The
macOS shell effort deferred this upgrade to its own decision, and 12 is a
stable line; nothing in that effort depended on staying on 11.

Avalonia 12's headless XUnit package builds on `xunit.v3.extensibility.core`
3.2.2, so `tests/WinMtr.Desktop.Tests` moves from xUnit.net v2 to xUnit.net
v3 on the 3.2 line. The project stays on VSTest, because the repository's
documented integration-test commands pass `VSTestTestCaseFilter` and the new
4.x packages default to Microsoft Testing Platform v2. The test project keeps
`Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio` and sets
`IsTestingPlatformApplication=false`.

Three 11-to-12 API breaks reached this code:

- Element-form compiled bindings. `CompiledBinding` is now a binding class
  whose `Path` is a typed `CompiledBindingPath`; element syntax cannot spell
  that path, while the XAML transformer still turns a `<Binding>` element
  into a compiled binding. The two `MultiBinding`s therefore keep their
  element form, spelled `<Binding>`.
- `AutoCompleteBox.Watermark` became `PlaceholderText`.
- `RenderOptions.TextRenderingMode` became `TextOptions.TextRenderingMode`.

The clipboard rewrite needed no source change: `IClipboard.SetTextAsync` is
still available as an extension method. No window-decoration migration was
needed either, because no view uses `ExtendClientAreaChromeHints`,
`SystemDecorations`, or custom chrome.

The direct `Tmds.DBus.Protocol` reference is removed. It existed to raise the
floor above the vulnerable 0.21.2 that Avalonia 11 resolved; Avalonia 12's
FreeDesktop package resolves 0.94.1, so the floor is now the transitive one.

## Consequences

`Avalonia.Controls.DataGrid` 12.1.2 still reports IL2104 and IL3053 against
itself during a Native AOT publish, so the demotion in `WinMtr.Desktop.csproj`
and in the `win-x64-aot` profile stays, and ADR 0007's reasoning is unchanged.
The win-x64 publish was re-run after the upgrade and the native binary passed
the launch probe (main window created, process responding, clean exit). The
next Avalonia upgrade should re-check whether the DataGrid warnings are gone
and, if so, drop the demotion.

xUnit.net v3 4.x adoption is deferred until `Avalonia.Headless.XUnit` moves to
it; mixing the 4.x MTP-v2 packages with the 3.2.2 core that package was built
against is the one combination this upgrade deliberately avoided.
