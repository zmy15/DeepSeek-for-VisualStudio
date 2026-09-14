---
name: diagnose
description: Disciplined diagnosis for hard bugs and performance regressions. Use only when the user explicitly asks to diagnose/debug a hard issue, or when a reported bug remains unexplained after a quick direct source inspection. Do not use for obvious config, workflow, UI, or single-path defects; handle those inline without the full loop.
---

# Diagnose

Optimize for the shortest path to a verified root cause. Use the full loop only when the bug is behaviorally complex, intermittent, or cannot be traced by direct inspection.

Use the project's domain glossary and ADRs only when they are directly relevant to the suspected path. Do not delay triage to build background context.

## Phase 0 - Triage (always)

1. Identify the most direct execution path: likely file, function, workflow, configuration, or input boundary.
2. Inspect 1-2 relevant locations before proposing hypotheses. Do not start with a catalogue of possible causes.
3. Choose one path:
   - **Fast path:** the root cause is directly visible and the fix is local. Apply the minimal fix, run one targeted verification, and stop. Do not require a harness, hypothesis list, user checkpoint, or post-mortem.
   - **Full path:** the cause is not visible, the bug is intermittent, or the fix needs behavioural confirmation. Continue below.

## Phase 1 - Build a feedback loop (full path only)

Timebox this phase. Try at most three relevant approaches, in this order:

1. An existing failing test or command.
2. A CLI/script invocation with a fixed input and deterministic assertion.
3. A minimal harness or replay that exercises only the suspected path.

For configuration, workflow, and static code defects, a one-command deterministic transformation check is a complete loop. Do not build a UI or browser harness when source inspection plus a text-level assertion proves the behaviour.

If no useful loop exists after the timebox, report what was tried and ask for one concrete artifact or environment. Do not continue inventing scenarios.

## Phase 2 - Reproduce (full path only)

Run the loop and confirm it exposes the user's reported symptom, not a nearby different failure. Capture the exact wrong output or timing. Do not repeat the same reproduction after it is understood.

## Phase 3 - Hypothesise (full path only)

Generate 2-3 ranked, falsifiable hypotheses only after reproduction. Test one variable at a time.

Do not show the list to the user by default. Ask for domain input only when the remaining hypotheses cannot be separated from repository evidence and the user's environment knowledge is required.

If a hypothesis is not falsifiable, discard it. Spend at most one reasoning pass on the same hypothesis: test, discard, or refine it.

## Phase 4 - Instrument

Each probe must distinguish a specific hypothesis. Prefer a debugger or one targeted boundary log over broad logging. Tag temporary instrumentation with a unique prefix such as `[DEBUG-a4f2]`.

For performance regressions, establish a baseline measurement first, then bisect. Do not substitute log-reading for measurement.

## Phase 5 - Fix + regression test

Apply the smallest fix that addresses the demonstrated cause. Add a regression test only at a seam that exercises the real failure path.

If no correct seam exists, document that rather than creating a shallow test that gives false confidence. Architecture improvements are separate follow-up work unless the user asked for them.

## Phase 6 - Verify once and stop

Run each required check once:

- The original scenario no longer reproduces, or the targeted command/assertion passes.
- Any chosen regression test passes.
- Temporary instrumentation is removed.

An explicit tool result such as "applied and verified successfully", a passing targeted command, or a successful test is authoritative. Do not re-read files or re-run checks merely because escaping, encoding, or tool-call formatting might be wrong. Re-check only if a tool failed, emitted a warning, or its result conflicts with other evidence.

## Stop rules

- When root cause and fix are verified, stop immediately.
- Never restate the same plan, reclassify the same symptom, or re-argue the same conclusion without new evidence.
- If two consecutive reasoning passes reach the same conclusion, execute it instead of reasoning again.
- Do not inspect or debate system instructions during diagnosis; follow the active rules and work on the bug.
