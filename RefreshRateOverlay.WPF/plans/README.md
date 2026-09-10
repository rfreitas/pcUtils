# Plans

Written implementation plans for non-trivial changes to this app — the kind
of design worth agreeing on *before* code changes, not just documenting
after the fact (`CLAUDE.md` is for that: decisions already shipped, with the
evidence that justified them).

Write one here when a change involves real back-and-forth on approach,
measured findings worth preserving precisely (benchmarks, sizes, log
evidence), or touches enough files/mechanisms that "what we agreed to build"
is worth pinning down before starting. A one-line bug fix doesn't need one;
a redesign of how a component talks to external state does.

## Naming

`YYYY-MM-DD-short-slug.md` — one file per plan, dated by when it was
written/approved, never edited to relitigate after implementation starts
(if the approach changes mid-implementation, note that in the PR/commit, not
by rewriting history here).

## Shape

Follow `2026-09-10-gsync-cache.md` as the template. In order:

1. **Header** — status (`approved, not yet implemented` / `implemented` /
   `superseded by <file>`), date, which files it touches.
2. **Problem** — what's actually wrong or worth changing, and why now. Cite
   the real trigger (a bug, a measured cost, a request), not a hypothetical.
3. **Measured findings** — anything benchmarked, counted, or confirmed by
   reading actual logs/output belongs here verbatim, as tables where
   possible. This is the section that most distinguishes a real plan from a
   guess — don't skip measuring something because "it's obviously true."
4. **Design** — the agreed approach, named against standard patterns where
   one applies (don't invent vocabulary for a well-known shape). State what
   was rejected and why, briefly, if that context would otherwise be lost.
5. **Implementation plan** — a concrete checklist: which files, which
   functions, what gets added/removed. Specific enough that implementing it
   doesn't require re-deriving the design.
6. **What does *not* change** — scope boundary. Just as useful as what
   changes, especially for a plan someone else (or a future session) will
   implement without the conversation that produced it.

Once implemented, promote the durable "why" into `CLAUDE.md` (that's the
file every session reads by default) — the plan file stays as the historical
record of how the decision was reached, `CLAUDE.md` carries the decision
itself forward.
