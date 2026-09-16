# History clear lives in Settings with inline Undo

The toolbar `Clear history` button sat beside Start. One click removed every entry from the **Target history** and wrote an empty list to `history.json`. The button stayed enabled when empty. There was no prompt and no restore.

A modal confirm was considered. It blocks a rare action with friction on every use. It also needs a new dialog service seam for headless tests. An Undo banner in the main window was considered. It needs an action slot on `BannerViewModel` and a backup that outlives the Settings dialog.

The fix moves clear to the Settings dialog, below **History size**. The dialog already gates on idle, so no extra session gate is needed. Clear records a request; it does not touch the store. Inline text shows `Will clear N targets.` with an `Undo` button that withdraws the request. The future tense is deliberate: the entries are still there while the dialog is open. OK empties the store, the same way it publishes **History size**. Cancel, close, or Escape drops the editor, so an unconfirmed request never lands. The button is disabled when empty through `CanExecute`.

## Consequences

Clear follows the dialog's copy-edit-publish rule, so `ITargetHistoryStore` needs no new write path and cancel needs no restore. `SettingsViewModel` holds one flag, `IsHistoryCleared`, plus the entry count seeded when the dialog opens, and owns the one sentence the user reads. `MainWindowViewModel` clears the store on OK and says nothing: the dialog already reported the count, so the status text stays free for trace diagnostics. Undo and cancel cost no disk write. No main-window banner is added. `History size` shrink keeps its own OK gate and stays out of scope.
