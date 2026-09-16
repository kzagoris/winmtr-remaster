# AGENTS.md — instructions for coding agents

This file guides coding agents that change this repo. `README.md` describes
the product for humans. This file describes how to change it safely.

## Project overview

WinMTR remaster combines traceroute and ping in one window. It probes a
route with rising TTL and pings each discovered hop. One window shows which
hop loses packets.

Stack: C# on .NET 10, Avalonia UI, Native AOT releases. See `CONTEXT.md`
for domain language. Use words from `CONTEXT.md`: route snapshot, trace
session, responding hop, silent hop, trailing silence, probe interval,
hop limit, latest probe state. Write in short technical sentences.

| Project | Role |
| --- | --- |
| `src/WinMtr.Core` | Platform-free domain: hops, statistics, trace engine, report renderers |
| `src/WinMtr.Infrastructure` | Machine touchpoints: ICMP probe channel, DNS resolver |
| `src/WinMtr.Console` | Command-line front-end over core plus infrastructure |
| `src/WinMtr.Desktop` | Desktop application for Windows, macOS, Linux |
| `tests/` | Fast unit suites plus `WinMtr.TestSupport` fakes |

Design rules: deep modules with small seams (`ITraceEngine`,
`IReportRenderer`, `ISettingsStore`, `IProbeChannel`, `INameResolver`).
Engine delegates judgements to pure functions. Route snapshots are
immutable. Settings belong to the trace session and do not change mid-run.

## Build and test commands

SDK version lives in `global.json`. Run from the repo root:

```
dotnet restore WinMtr.Remaster.sln
dotnet build WinMtr.Remaster.sln -c Release --no-restore
dotnet test WinMtr.Remaster.sln -c Release --no-build
```

Integration tests send real ICMP and stay excluded by default through
`Directory.Build.props`. Run them only on a host with network and rights:

```
dotnet test WinMtr.Remaster.sln -p:VSTestTestCaseFilter="Category=Integration"
```

Check formatting and whitespace before you finish:

```
dotnet format WinMtr.Remaster.sln --verify-no-changes --no-restore
git diff --check
```

Keep the suite fast. Use fakes for time and network. Golden files in
`tests/WinMtr.Core.Tests/golden/*.txt` pin format, not values. Update them
only for an intentional format change.

## Conventions

- DDD lightly: domain language plus value objects, no ceremony.
- TDD bottom-up: red, green, refactor. Add or update a focused test with
  each behavior change.
- Concurrency: immutable snapshots, cadence as floor, cancel plus drain.
- Line endings are LF (`.gitattributes`, `.editorconfig`). Exception:
  `tests/WinMtr.Core.Tests/golden/*.txt` is `-text` and must stay
  byte-stable.
- Commit on `main`. Do not make a branch unless the user asks for one.
- Commit message holds subject plus body only. Never add an agent
  attribution trailer: no `Claude-Session:`, no `Co-Authored-By:` for an
  agent, no `Generated with` line, no tool link.

## Boundaries

- Always do: read `CONTEXT.md` and the relevant `docs/adr/` record before
  you change domain logic. Keep changes small and focused.
- Ask first: before you change a public API, a report format, a golden
  file, CI or release workflows, or the `.app` and installer story.
- Never do: commit secrets or tokens. Touch `bin/`, `obj/`, `TestResults/`,
  or `*.nupkg`. Edit generated assets by hand when
  `scripts/build-icons.cs` owns them. Claim network failures as code
  defects.

## Verification

A change is done only when these pass: build in Release, fast test suite,
format check, and `git diff --check`. For UI behavior, add or update an
Avalonia headless test where practical. Re-run the affected scope after
each fix. State the commands and their results in your summary.

## Pull Requests

Read `.github/pull_request_template.md` first. Use its exact section
structure as the PR body. Fill every section. Do not invent your own
format.

The template holds a REQUIRED Authorship and follow-up gate. Ask the user
who owns review follow-up. Do not infer it. Do not add an agent
attribution trailer to the commit or the PR body.
