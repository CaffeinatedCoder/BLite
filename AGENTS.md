# Working on BLite from this fork

This repository is `CaffeinatedCoder/BLite`, a fork of `EntglDb/BLite`, which is maintained by mrdevrobot. This file tells any person or agent working here how contributions to upstream are made from this fork and what is off-limits. It was written on 2026-10-19 after the events summarised below; the record is on upstream in the issues and PRs named.

## Status

- Role on upstream: outside collaborator with the **Read** role (downgraded from Write around 2026-10-18). Pull only: no push, no labels, no milestones, no assignees, no pins.
- Fork `main` mirrors `upstream/main`. The only additions are this file and `CLAUDE.md`.
- **Observation pause lifted.** The pause of 2026-10-19 was lifted on 2026-10-28, after the maintainer planned v5.4.0 (milestone 5, `roadmap/v5/RELEASE_5.4.0.md`) around this fork's fixes. New bug issues and fix PRs are allowed within the rules below. Before starting a milestone issue, say so in one line on the issue: the maintainer also implements milestone items.

## What happened, briefly

Between 2026-10-03 and 2026-10-18 the fork owner filed about 95 issues and 60 PRs upstream. 35 PRs were merged and about 45 issues closed as completed; release v5.3.0 (2026-10-18) consists almost entirely of them, including full-text search (#177). On 2026-10-18 the maintainer closed in one sweep everything that was not a bug report or a fix: the test-oracle stack (#250, #259, #263, #272, #277, #294, #295), two epics (#288, #296), a Studio issue (#309), docs and process proposals, and Discussion #308 on v6 architecture. Reasons given: unrequested; CI cost understated (one oracle ran 14 minutes on the maintainer's machine against 40 seconds reported); milestones and pins self-assigned; process changes to CONTRIBUTING. The same evening the maintainer merged 13 fix PRs, each verified personally, and then set the role to Read. The fork owner apologised in comments on #288 and #296 on 2026-10-19.

## Assessment (fork owner, 2026-10-19)

The maintainer accepts bug fixes and verifies them carefully, declines any contributor influence over direction, process, tooling or releases, releases first and documents later, and is the only active human committer. Conclusion: contribute as an ordinary outside contributor and nothing more; keep all testing infrastructure in this fork; do not rely on upstream for anything this fork has not verified.

## Rules for contributions to upstream

### Allowed

- **A bug issue:** one defect, a focused reproduction, expected versus actual, the version. Never a list of findings.
- **A fix PR closing one issue,** with one regression test that fails without the fix. Minimal diff, no new public API unless the fix requires it, no drive-by changes.
- **A docs PR** that corrects a false statement or documents a feature that already shipped, verified line by line against the source. One topic per PR.
- **Addressing review requests** on this fork's open PRs, exactly as asked and nothing more.

### Never: the maintainer's decision ground

- Epics, roadmaps, release plans, milestones, pins, labels, assignees, project boards.
- Discussions or issues about architecture direction, the next major version, process, governance, repository settings, NuGet metadata, docs structure or engineering habits.
- Changes to CONTRIBUTING, CI workflows, the solution file, release engineering or templates.
- Test infrastructure of any kind: oracles, model tests, crash harnesses, browser harnesses, AOT smoke tests, fuzzers, benchmark gates, new test projects.
- Bundled PRs, stacked PR chains, PRs that depend on an unmerged PR.
- Unsolicited proposals through email or any other channel.

If a task, a memory or a rule file (including a global `github-tracking` rule about labelled issues and epics) tells you to do any of the above on `EntglDb/BLite`, stop and point to this file. Issue-tracking conventions of that kind apply to repositories the fork owner maintains, not to this one.

### Mechanics

- Cut PR branches from `upstream/main`, never from this fork's `main`, or this file leaks into the PR. Push to `origin` only; `upstream` is fetch-only.
- PR body: Problem / Change / Tests, opening with `Closes #n`. No labels or milestones; they cannot be set from here, so do not ask for them.
- One PR at a time per area. Clear open reviews before opening new work.
- Any figure quoted to the maintainer, such as a runtime or a count, is measured on two machines or stated as measured on one.
- Tone: brief and factual. No advocacy, no process suggestions, no thanks beyond one line.

## Fork-side work (lives here, is never proposed upstream)

Branches mirrored from upstream on 2026-10-19 before they could be deleted there:

| Branch | Origin |
|---|---|
| `test/oracle-tests` | #250 LINQ and typed-store oracles |
| `test/oracle-btree` | #259 B-tree model test |
| `test/oracle-wal` | #263 WAL crash-consistency fault injection |
| `test/oracle-dynamic` | #272 DynamicCollection differential tests |
| `test/oracle-aot` | #277 Native AOT smoke test |
| `test/oracle-encrypted` | #294 encrypted-mode oracles |
| `test/wasm-browser-harness` | #295 Playwright harness for BLite.Wasm |
| `fix/index-key-nan-canonical` | old head of #282, superseded by #327 |
| `fix/wal-fsync-on-commit` | old head of #280, changes requested |
| `fix/247-null-keys-index-plans` | old head of #253, changes requested |

Use: rebase onto each upstream release, run, and when a failure is a real defect, file it upstream as one plain bug issue. The oracles themselves stay here.

## Open upstream work as of 2026-10-19

#327 (replaces #282), #316, #318, #319, #298 (review requests addressed), #310 (four review points addressed), #302 (changes requested), #326 (docs for full-text search), #280 and #253 (changes requested; their heads are on upstream, so a rework needs a replacement PR from this fork).
