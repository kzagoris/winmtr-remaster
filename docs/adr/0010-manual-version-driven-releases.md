# Releases are started by hand and driven by the version in the repository

A release begins as a manual `workflow_dispatch` run on `main`. The operator
gives the version. The workflow checks that version against
`Directory.Build.props`, builds the ten archives, starts the binaries it can
start, and publishes the Release. It creates the tag `v<version>` at the
released commit as its last act.

A tag push was the previous trigger. It was rejected because it holds the
version in a ref name. That name is easy to mistype, hard to correct once
pushed, and separate from the number the binaries carry. Two sources of truth
for one version is one too many. The version now lives in the repository, under
review, and the tag records what shipped.

A GitHub Release cannot exist without a tag, so the tag does not disappear. It
changes role: it is the record of a release, never the trigger.

The run also refuses a ref other than `main`, a version whose numeric shape the
macOS bundle assembler would later reject, and a version that is already
released. A `dry_run` input defaults to true, builds everything, stamps
`-dev.<sha>`, and publishes nothing.

## The two retired gates

This decision supersedes two policies in
[ADR 0007](0007-native-aot-release-matrix.md) and
[ADR 0009](0009-macos-shell-bundle-signing-and-ci.md): the rule that every
release below `1.0.0` is marked as a prerelease, and the rule that a hand smoke
on both Mac architectures gates a release. Both records stay unmodified. The
Native AOT choice, the runner matrix, and the bundle and signing policy stay in
force.

ADR 0007 held those two gates against one narrow risk: a Native AOT trimming
break shows itself only at run time, and no packaging assertion finds it. An
automated launch smoke replaces both gates. Each runner starts what it built
and can execute:

- The console binary runs with `--version`. It prints its stamped version and
  exits zero. This proves that the Native AOT binary starts, that it loads, and
  that it carries the version the run asked for.
- The desktop binary is started and must stay alive. Window construction loads
  Avalonia and `DataGrid`, which is where a trimming break would show. Linux
  supplies a virtual display.

The smoke covers `win-x64`, `linux-x64`, `linux-arm64` and `osx-arm64`. A
machine performs it on every release, which the hand smoke never guaranteed.

## Consequences

`osx-x64` is published with no execution evidence. The macOS runner is Apple
Silicon, nothing in the matrix runs Intel code, and ADR 0007 already rejected a
second Mac job on cost and on the August 2027 retirement of the last Intel
image. The README states this plainly, so an Intel user can judge the download.
Shipping it untested and labelled is better than removing a download that most
probably works.

Every release is a full release. A defect found after publication is corrected
by a new version, not by a label. The version must therefore rise in a commit
before each release, which makes every release visible in the history.

Release notes come from `docs/release-notes/<version>.md` when that file
exists, and from the commit history when it does not. Notes are written in the
same commit that sets the version, because the workflow publishes at once and
leaves no draft to edit.

The tag trigger is removed from `ci.yml` as well. The release workflow runs the
fast suite on all three platforms before it publishes, so a tag push would only
repeat work on a commit that just passed.
