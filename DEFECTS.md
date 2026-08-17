Deep audit result: not release-ready. Several earlier regressions fixed; multiple critical defects remain. Benchmark/README claims currently untrustworthy.

## Critical unresolved defects

- Solver failure → infinite “Crafting” stall/spin. `VulcanSkill.None` represents calculating, finished, failed, and protected-quality abort. Same-state deduplication only applies to real actions, so terminal `None` gets resubmitted forever. No `AutomationFaulted` propagation. Direct match for the stuck-after-Miracle symptom. [Vulcan.DonatelloSolver.cs](/mnt/XFSNVME/Projects/personal/GatherBuddyAscended/GatherBuddy/Vulcan/Vulcan.DonatelloSolver.cs:355), [Vulcan.CraftingProcessor.cs](/mnt/XFSNVME/Projects/personal/GatherBuddyAscended/GatherBuddy/Vulcan/Vulcan.CraftingProcessor.cs:207)

- Unsafe native-solver lifetime:
  - Replaced/finished solvers merely lose managed references; native work continues.
  - Repeated manual intervention can accumulate overlapping solves.
  - Plugin shutdown saves coordinator state but neither cancels nor drains solver tasks.
  - Resume can call `Interrupt` after the task has already freed its interrupt pointer → native use-after-free window.
  [Vulcan.CraftingProcessor.cs](/mnt/XFSNVME/Projects/personal/GatherBuddyAscended/GatherBuddy/Vulcan/Vulcan.CraftingProcessor.cs:161), [Vulcan.DonatelloSolver.cs](/mnt/XFSNVME/Projects/personal/GatherBuddyAscended/GatherBuddy/Vulcan/Vulcan.DonatelloSolver.cs:220), [GatherBuddy.cs](/mnt/XFSNVME/Projects/personal/GatherBuddyAscended/GatherBuddy/GatherBuddy.cs:627)

- Material Miracle ignores solver ownership:
  - Bootstrap enabled unconditionally whenever a charge exists.
  - Applies to Standard, Pure Raphael, and macros—not only ICE/Donatello.
  - After acknowledgement, live adoption directly creates Donatello, silently replacing the selected solver.
  - Previous minimum-step/multi-use controls were removed.
  [CraftingGameInterop.cs](/mnt/XFSNVME/Projects/personal/GatherBuddyAscended/GatherBuddy/Crafting/CraftingGameInterop.cs:1631), [Vulcan.CraftingProcessor.cs](/mnt/XFSNVME/Projects/personal/GatherBuddyAscended/GatherBuddy/Vulcan/Vulcan.CraftingProcessor.cs:95)

- Generic reload recovery remains incomplete. It now identifies the exact active recipe, reads live state, and can solve from that root regardless of ICE. But:
  - No persisted Raphael plan/action index → empty incumbent; normal-craft quality protection cannot be claimed.
  - No immediate warning that the baseline guarantee was lost.
  - Synthesis open + temporarily unavailable recipe ID waits forever.
  - Previous Stellar Steady Hand use count becomes zero, potentially exceeding configured per-craft limits.
  - Recovery material planning treats every remaining expanded queue entry as an independent recipe; precraft outputs can be required before their queued production.
  :codex-annotation{index="1"} [CraftingRecoveryTicket.cs](/mnt/XFSNVME/Projects/personal/GatherBuddyAscended/GatherBuddy/Crafting/CraftingRecoveryTicket.cs:128), [CraftingExecutionPlan.cs](/mnt/XFSNVME/Projects/personal/GatherBuddyAscended/GatherBuddy/Crafting/CraftingExecutionPlan.cs:105), [SynthesisReader.cs](/mnt/XFSNVME/Projects/personal/GatherBuddyAscended/GatherBuddy/Crafting/SynthesisReader.cs:163)

- Action admission lacks a whitelist. Native/cache IDs are directly cast to `VulcanSkill`; unknown enum values are accepted by the simulator’s default branch as legal zero-effect steps. Executor then falls back to issuing the raw numeric enum as an action ID. Corrupt/stale cache or bad FFI output can cross into game execution. [DonatelloNative.cs](/mnt/XFSNVME/Projects/personal/GatherBuddyAscended/GatherBuddy/Vulcan/DonatelloNative.cs:176), [Vulcan.Simulator.cs](/mnt/XFSNVME/Projects/personal/GatherBuddyAscended/GatherBuddy/Vulcan/Vulcan.Simulator.cs:149), [CraftingActionExecutor.cs](/mnt/XFSNVME/Projects/personal/GatherBuddyAscended/GatherBuddy/Crafting/CraftingActionExecutor.cs:33)

## Benchmark/test defects

- Current benchmark is invalid for the claimed comparison:
  - Raphael baseline removes Final Appraisal.
  - Adaptive solver enables Final Appraisal.
  - Runtime Raphael enables it.
  - Therefore action sets differ.
  [main.rs](/mnt/XFSNVME/Projects/personal/GatherBuddyAscended/donatello/donatello-bench/src/main.rs:745)

- Benchmark bypasses the actual defective runtime path. It invokes `solve_from_state_with_condition_and_incumbent_anytime`; normal runtime invokes FFI `solve_staged_progress`. The benchmark could not have caught the staged-plan regression by construction. [main.rs](/mnt/XFSNVME/Projects/personal/GatherBuddyAscended/donatello/donatello-bench/src/main.rs:661), [lib.rs](/mnt/XFSNVME/Projects/personal/GatherBuddyAscended/donatello/donatello-ffi/src/lib.rs:353)

- “Real” corpus uses real recipe data, but not real game execution: same simulator on both sides, deterministic injected single-condition roots, no UI/live-state reader, FFI lifecycle, timing, cache, reconciliation, reload, or staged runtime integration. It cannot detect simulator↔game divergence.

- New never-worse test calls a helper predicate with fabricated scores. It does not exercise native staged solving → FFI → C# admission → executed plan. It violates the newly documented runtime-boundary requirement. [Program.cs](/mnt/XFSNVME/Projects/personal/GatherBuddyAscended/GatherBuddy.Vulcan.Tests/Program.cs:901)

- Liveness tests largely validate parameter forwarding/static selectors. No injectable clock; repair/vendor/job-switch/recovery state machines lack cheap deterministic no-progress/deadlock tests.

- README table, graph, 55% claim, and “replaced by standard solve” statement are stale/false. Runtime retains Raphael or should stop; it does not replace worse plans with Standard Solver. [README.md](/mnt/XFSNVME/Projects/personal/GatherBuddyAscended/README.md:38)

The requested benchmark rerun was stopped. Rerunning it would produce authoritative-looking nonsense. Harness must first use equal action masks and the real FFI staged path.

## Other unresolved liveness/contract defects

- Vendor: removing the destructive 15-second whole-operation timeout fixed long travel, but introduced an unbounded post-navigation wait. Manager’s “screen not ready” path returns before its timeout check; some successful menu-selection paths reset the timer indefinitely. Needs phase/progress watchdog. [LiveVendorPurchaseAdapter.cs](/mnt/XFSNVME/Projects/personal/GatherBuddyAscended/GatherBuddy/Crafting/Acquisition/LiveVendorPurchaseAdapter.cs:123), [VendorPurchaseManager.cs](/mnt/XFSNVME/Projects/personal/GatherBuddyAscended/GatherBuddy/Vulcan/Vendors/VendorPurchaseManager.cs:600)

- Repair:
  - Absolute 60-second route timeout penalizes valid long routes even with progress.
  - Between-areas/Lifestream/player-unavailable branches bypass timeout checks indefinitely.
  - Path restarts do not use a no-progress clock.
  [RepairNPCNavigator.cs](/mnt/XFSNVME/Projects/personal/GatherBuddyAscended/GatherBuddy/Crafting/RepairNPCNavigator.cs:29)

- Job switching retries every two seconds forever. Screen-not-ready/zone-busy, gearset-module failure, equip exception, or unacknowledged equip has no terminal attempt/progress watchdog. Matches the supplied retry log. [CraftingQueueProcessor.cs](/mnt/XFSNVME/Projects/personal/GatherBuddyAscended/GatherBuddy/Crafting/CraftingQueueProcessor.cs:901)

- Specialist resource model caps Crafter’s Delineations to 0–2 based only on Heart and Soul/Quick Innovation availability. Careful Observation consumes the same resource. Valid specialist roots/actions are removed; tests currently enshrine this approximation. [RaphaelSolveData.cs](/mnt/XFSNVME/Projects/personal/GatherBuddyAscended/GatherBuddy/Crafting/RaphaelSolveData.cs:24), [effects.rs](/mnt/XFSNVME/Projects/personal/GatherBuddyAscended/donatello/raphael-sim/src/effects.rs:67)

- Artisan shim compatibility is misleading:
  - Only exact “Progress Only Solver” changes behavior.
  - Standard/Raphael/Expert/Macro names still force Donatello.
  - Expert profile ID is stored but never used.
  - Material Miracle IPC setters are explicit no-ops.
  [ArtisanIpcShim.cs](/mnt/XFSNVME/Projects/personal/GatherBuddyAscended/GatherBuddy/Crafting/ArtisanIpcShim.cs:155)

- Queue pause does not explicitly suspend an already-active CraftingGameInterop solver. Contract unclear; potentially “pause queue” continues the current craft.

- Live and coordinator native solves are not globally serialized. Background warmups, live replans, and detached obsolete solves can overlap, consume CPU, and compete against deadlines.

- Build warning remains: `CSCore 1.2.1.2` restored through legacy .NET Framework compatibility (`NU1701`). Repository rules classify unresolved warnings as defects.

## Changes already present in the worktree

- Added repository-level never-worse/user-deference rules. [AGENTS.md](/mnt/XFSNVME/Projects/personal/GatherBuddyAscended/AGENTS.md:2)

- Fixed the largest quality regression: staged progress plans can no longer bypass Raphael admission for protected normal crafts. Candidate must complete, use guaranteed actions, and be strictly better; otherwise retain Raphael.

- Fixed hardcoded gearset level `100`; now reads the actual crafting job level and fails closed when stats/items/materia/caps are unavailable. The `/100/...` versus `/91/...` logs were our GatherBuddy integration bug—not Raphael’s solver result. Original live Raphael did not require this fake gearset context.

- Fixed live synthesis max-quality index and authoritative live dimensions; fixed exact Final Appraisal reconciliation.

- Added exact active-recipe identity checks, live-state adoption, manual-action detection/replan, ICE restored-queue deduplication, and Material Miracle initial-solve deferral.

- Fixed crystal/mixed-node gathering job resolution; Botanist/Miner now derive from actual nodes instead of aggregate `Unknown → job 0`.

- Fixed repair target selection: loaded current-zone mender first; preferred NPC identity now includes territory, preventing duplicate DataId restoration to Mare Lamentorum.

- Split vendor travel timeout from purchase handling; added verified vendor-interaction exit retries.

- Added cache version bump and native/plugin final-quality validation.

These changes are useful but not sufficient for release.

## Verification

- `just build`: pass; 6 projects, 0 errors, 1 `NU1701` warning.
- `just test`: pass; 327 assertions, warnings remain.
- Targeted native simulator/FFI tests: 98/98 passed, 10 suites, 7.48s.
- Benchmark crate unit tests: 3/3 passed.
- `git diff --check`: pass.
- Full workspace test result: unavailable; detached command exited without retained status/output.
- Publish artifacts: deliberately not built/installed. Critical gates remain open.
