# FeedMeUpdates — notes for AI coding sessions

This repository is being rewritten from scratch as **FeedMeUpdates 2.0**. The v1 code in the root
(`FeedMeUpdates.cs`, `Application/Program.cs`) is reference material only: do not extend it.

## Read first, in this order

1. `docs/v2/DESIGN.md` — architecture, invariants (INV-1…INV-10), open decisions.
   `docs/v2/reviews/` holds the review it was revised against; its minor findings are still open.
2. `docs/v2/ROADMAP.md` — phases, gates, working agreement.
3. `docs/v2/TESTING.md` — test levels, lab plan, assumptions to validate (LAB-n).
4. `docs/v1/DEFECTS.md` and `docs/v1/KNOWLEDGE.md` — what v1 got wrong and what it learned.
5. `docs/research/` — sourced facts about hosting, Rust/Oxide/Carbon, and the ecosystem.
6. `docs/v1/spec/` — line-level behaviour of v1; consult per topic, not cover to cover.

## Ground rules

- The maintainer wants the best possible v2 and does not need v1 compatibility.
- Nothing is "done" without a test or lab report proving it (see ROADMAP working agreement).
- Facts marked UNVERIFIED in `docs/research/` or **[LAB-n]** in the design are hypotheses: validate
  them on a real server before building on them, and write the result back into the docs.
- Lab servers (Windows + Ubuntu, reached over SSH from the maintainer's laptop) are the only place
  destructive actions may run. Never touch anything outside the lab server directories.
- Never put secrets (RCON passwords, API keys, webhook URLs) in the repo, logs or commit messages.
- The maintainer writes in Italian; repository documents and code are in English.

## Current state

Phase 0 (foundations). The design is at revision 3 after two independent review rounds
(`docs/v2/reviews/`); DESIGN §15 lists the decisions the maintainer still has to make. The first
time-critical item is the timeline recorder (ROADMAP phase 0).
