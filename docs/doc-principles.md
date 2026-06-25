# Documentation principles — the density rubric

> **For a doc / code-comment review pass: the checklist to run each file against. Secondarily, a first-pass writing rubric — write at this density from the start to avoid the rewrite.** Distilled from a full trim pass over `docs/`. The governing model is [CLAUDE.md](../CLAUDE.md)'s project-memory rule — *fact + consequence + pointer* — applied to the docs themselves.

## The core test

Every chunk must justify its tokens. The test: **could a teammate act on this, fetching the doc or code only for detail?** If a passage isn't actionable — context-for-context's-sake, a restatement, or mechanism that lives in code — it's waste. The reader's attention (human or LLM context window) is the scarce resource; spend it only on what's load-bearing. Denser is better *until* you'd lose a learning — the limit is comprehension, not character count.

Three waste-shapes recur, and the fixes are mechanical once you see them.

## 1. Say each thing once

The single biggest waste is **a principle stated 3–5 times across a doc** (a "derive per case" rule restated in the intro, the rules list, a table caption, and every dossier). Pick the **canonical home**, state it fully there, reference it elsewhere — don't re-explain.

- **Cross-section restatement** — one fact/fix described in several sections (an interpolation fix appearing in "what's determined," "current assessment," "tooling," *and* a catalogue row). State it once; the other sites get a clause + a pointer.
- **Recap / summary sections are suspect.** A "current assessment" that restates what's above earns its place only by the *net-new* it adds (a consolidated open-items list). If it says "documented in full above," compress it to the delta.
- **Mechanism re-explained at multiple granularities** — an N-step list, then the callbacks, then a matrix table, then per-case. Keep the **densest single representation** (usually the table) plus the per-case *wrinkles*; cut the prose that re-walks the table.

## 2. Mechanism and numbers live in code; docs carry the reasoning

The doc's job is the **why / when / the trap** — the durable lesson. The code's job is the **exact arithmetic, formula, and line-by-line**. A copy of the implementation in prose is a second source of truth that **drifts** — the canonical staleness bug was a doc asserting `ceil` while the code computed `floor+1`.

- **Math / equations → a pointer.** Replace evaluated numbers (`=3`, `30.0000019`) and derivation formulas with the *qualitative* derivation (the operator, the rounding direction) + a pointer to the call site. Keep the *lesson about* the math ("the check operator decides the count; a wrong one is a silent ±1").
- **Volatile / build-pinned numbers live in ONE place** (the version doc, or the code) and are referenced — never restated, or you get N copies that age at different rates.
- **Tables, code-blocks, and code-pointers are dense signal — keep them.** A capability matrix or a constants table earns its tokens; the paragraph that walks through it does not.

## 3. Delete history that isn't a lesson

A doc states **what is**, not how it got there (git holds the path).

- **Drop dead / renamed-artifact references** — an old file name, a thing's former name, "migrated from X," removal narration. A reader gains nothing from a name that no longer exists; it only costs attention. Keep the current names + a pointer.
- **Cut resolved-progression narratives** — "baseline → tried X → tried Y → frame-perfect," with the step-by-step measurement numbers, is git's job once the thing is resolved. Keep the settled fact + the durable lesson; drop the breadcrumbs and the evidence for a closed question.
- **The one durable "why" to keep:** the reasoning behind a *non-obvious* decision — why we *didn't* do the obvious thing. That prevents re-litigation; the session-by-session history does not.
- **Cut hedging / throat-clearing** — "it's worth noting," "honestly stated," "a deliberate call flagged as likely to bite." State the fact and the flag; drop the framing.

## What MUST survive the trim (do not cut these)

Density is not deletion. These notes read as "extra" but are load-bearing — a hard trim that removes them makes the doc *wrong* for a reader who will otherwise treat a partial list as complete:

- **Incompleteness flags.** "This is a *starting checklist*, not a closed set — review each case in depth, expect the rule-set to grow." Without it, a reader treats the listed cases as exhaustive and skips the per-case investigation.
- **Edge-case / exception / fallback handling.** Both boundary directions of any lifecycle (started-mid-X *and* stopped-mid-X), the guards, and the fallback path — which **deserves the same scrutiny as the happy path**. Compress the prose, never the coverage.
- **Honesty flags on confidence.** "Proven" vs "reasoned-but-not-measured" vs "assumed, unverified" must stay distinct — collapsing them to a uniform confident tone is, for a determinism project, a lie. Keep the "open / owed / not-yet-proven" markers.
- **Version-fragility / re-verify-on-update notes** for anything that short-circuits, mirrors, or reflects into game internals.

When trimming, these get *tighter*, not *removed*. If a kept note ends up slightly longer than its neighbours, that's correct weighting — the exception deserves the words.

## Recurring structural patterns

- **Triple-listing** — the same capability described in "what exists today" + "the planned design" + "build order." Once the work is built, those collapse into **one section per area** (current state, future folded in) + a single "what's not built yet" list. A roadmap/build-order section is a planning artifact: shrink it to "what's left" as items land.
- **The doc that grew a second copy of another doc** — a routing/subset doc re-defining terms the comprehensive doc owns. Point to the owner; state only the subset's delta.
- **The orphaned cross-reference** — a pointer to a section/symbol that a prior edit removed or renamed. Every restructure should re-grep its own outbound and inbound links.

## Process discipline

- **Verify against the actual code/tree, not the doc.** Before trusting any doc claim, grep the codebase — removed files get referenced for months, and formulas drift. The doc is the hypothesis; the code is ground truth. Fix *wrong/stale* before *dense*.
- **Preserve anchors when restructuring.** If other docs link to a section (`grep -rn 'thatdoc.md#'`), keep the header text verbatim or update every inbound link. A silent broken anchor is the most common restructure regression.
- **One canonical home per topic; everything else points.** The doc index ([README.md](README.md)) assigns homes; topic docs own their topic; references are cheap.
- **The live-investigation-doc exception.** A "working notes" doc ([determinism.md](determinism.md)) legitimately holds open theories, evidence, and ordered next-experiments — but even there, **settled facts graduate out** to the stable docs and **resolved theories compress to the lesson + a pointer**, not kept as detailed evidence. Distinguish "open" (keep the evidence) from "resolved" (compress it).

## Code comments — the same lens, one direction flipped

The rubric applies to comments, with one inversion: in a doc, prose is the content; in code, **the code is the content and a comment earns its tokens only on what the code can't say** — the *why*, the gotcha, the non-obvious derivation. The pass that worked:

**The floor: every function gets a one-line summary — even a mechanical one.** This is the single comment you keep *even when it feels redundant*: it surfaces in IDE tooltips/intellisense at every call site, and a one-liner ("Create all virtual input devices — convenience wrapper over the individual creates") gives instant clarity for something only *mostly* obvious from the name. A bare signature with no summary is the one *under*-commenting failure; everything below is about *over*-commenting.

**Cut everything inferable from name + signature + context:**
- **Per-field / per-param notes that restate the type or name** — `MonoBehaviour Instance; // the game actor`, `int frames; // frame count`. Annotate a field/param only when its meaning *isn't* clear from name+type: a vague name, a unit, a non-obvious constraint or range.
- **Comments that re-narrate the code** — `onFrame?.Invoke(ctx, isFinal); // per-frame work`. The code already says it.
- **The "said three times" function** — a method summary, then param notes, then body notes all conveying the same thing. Say it once, cut the rest. (Per-colour visual-name comments — `// cyan` on an RGB triple — are an exception worth keeping: you can't read the colour off the numbers.)

**Keep:** section dividers; the non-obvious *why* (an ordering that matters, why a value is null, a tripwire); genuinely-complex-logic explanation; and the canonical derivations the docs point *to*.

**Headers and home:**
- **The usual big waste is the file/class HEADER re-explaining a concept the doc owns** (architecture, catalogue, "why we built it this way"). Compress to a one-line orientation + a pointer. A reader in the code wants to *navigate*, not re-read the design doc.
- **But heavily-commented code is often the canonical HOME, not waste** — the per-case derivations and exact numbers the docs deliberately point *to* (an inline frame-count `floor(dur*Hz)+1` with its operator reasoning) live in code by design. Keep them; tighten prose only.
- **A volatile list duplicated in a header drifts *and* goes incomplete** (an env-var inventory re-described in the code that reads it). Point to the one inventory.
- **Staleness lives in comments too** — removed-file names, renamed-doc-anchor references, "old X" history. Sweep comments against the tree and docs in the same pass.

## Using this doc

1. **Review pass (primary):** run each file against §1–§3, then check nothing in "What MUST survive" was lost. Order matters — fix wrong/stale first (verify against code), then density.
2. **First-pass writing (secondary):** write at this density from the start. The cheapest rewrite is the one you don't do.
