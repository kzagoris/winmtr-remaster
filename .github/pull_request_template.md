# Pull request

## Summary

What changes, and why. Link the issue: `Closes #NNN` or `Related to #NNN`.

## Changes

- Area: Core / Infrastructure / Console / Avalonia / docs / CI.
- Behavior: what a user or an agent sees differently (route snapshot, grid, report, settings).
- Format impact: does any report renderer output change? Are golden files updated intentionally?

## Tests

Commands run and results. Paste numbers, not feelings.

```
dotnet build WinMtr.Remaster.sln -c Release --no-restore
dotnet test WinMtr.Remaster.sln -c Release --no-build
dotnet format WinMtr.Remaster.sln --verify-no-changes --no-restore
git diff --check
```

- [ ] Build passes in Release.
- [ ] Fast suite passes. Integration exclusions noted separately if touched.
- [ ] Format check and `git diff --check` pass.
- [ ] New or updated tests cover the behavior. Golden changes are intentional.

## Checklist

- [ ] No secrets, tokens, or private host names in diff, logs, or screenshots.
- [ ] Domain language from `CONTEXT.md` used correctly.
- [ ] Docs updated where behavior changed (`README.md`, `CONTEXT.md`, ADR, or spec).
- [ ] GPL-2.0-only license kept. No new dependency with a conflicting license.

## Authorship and follow-up (REQUIRED)

Who owns review follow-up? Name the human GitHub handle that answers reviewer
questions and pushes fixes on this PR. Do not infer it. Ask when unsure.

- Owner: @<!-- handle -->
- Agent used (if any): <!-- tool plus model, for context only; never added as a commit trailer -->
