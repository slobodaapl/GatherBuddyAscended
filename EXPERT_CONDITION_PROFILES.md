# Expert Recipe Condition Probability Profiles

Status: research prerequisite, 2026-08-16. RLT `776` now has an accepted working vector inferred from local measurements; cross-context validation remains incomplete. Do not use this note as solver evidence. Donatello/Gabriel/simulator work remains downstream of validating these data.

## Current finding

Current public game data contains 544 `Recipe` rows with `IsExpert=True`. They collapse to:

- 44 distinct `RecipeLevelTable` rows.
- 64 distinct observable `(durability, progress, quality, conditions flag)` signatures after applying recipe factors.
- 25 distinct known base probability vectors among the 43 currently mapped `RecipeLevelTable` rows.

The public empirical data set below covers 43/44 `RecipeLevelTable` rows and 63/64 observable signatures. Local measurements now supply the formerly missing RLT `776` profile, giving working coverage of 44/44 rows and 64/64 signatures. No covered `RecipeLevelTable` row or signature maps to conflicting vectors.

Strong inference: an expert recipe's base condition profile is selected by `RecipeLevelTable`, or by a hidden profile associated one-to-one with it. This is not yet proven from authoritative server code or data.

Current-client tracing resolves the normal action path: the client sends every craft action to the server, then receives the resulting craft state and one realized next condition. The request contains the action ID but no locally rolled condition, condition weights, or craft PRNG seed. The response contains the realized condition but no vector or profile ID. Exact client extraction is therefore no longer the primary acquisition path for RLT `776`; authoritative server data would be exact, otherwise interception plus empirical inference is required.

### RLT 776 — locally inferred profile

- `RecipeLevelTable`: `776`
- Recipe IDs: `38246` through `38253`
- Results: Crumbling Aqueduct Fragment, Metal, and Resin
- Durability/progress/quality: `60/11250/31520`
- Conditions flag: `1523`
- Allowed conditions: Normal, Good, Centered, Sturdy, Pliant, Malleable, Primed, Robust
- Accepted working base vector: `(20, 10, 0, 15, 10, 15, 10, 10, 10)`
- Canonical order: `(Normal, Good, Good Omen, Centered, Sturdy, Pliant, Malleable, Primed, Robust)`
- Evidence label: `empirically-inferred`; treated as the exact working vector by project decision on 2026-08-16, not claimed as an authoritative extraction of server constants

Local sampler evidence:

- 11 Trial Synthesis sessions for recipe `38247` (Crumbling Aqueduct Metal), all RLT `776`, conditions flag `1523`
- 868 recorded transitions total
- Excluded from base-distribution inference: 83 forced `Robust -> Sturdy` successors and one transition not issued by the sampler's `Observe` action (`ActionId=0`)
- `Good Omen` is unavailable for this profile, so no `Good Omen -> Good` forced successors occurred
- 784 eligible base-distribution draws remained

| Condition | Count | Observed | Accepted working value |
|---|---:|---:|---:|
| Normal | 152 | 19.39% | 20% |
| Good | 78 | 9.95% | 10% |
| Good Omen | 0 | 0.00% | 0% |
| Centered | 109 | 13.90% | 15% |
| Sturdy | 70 | 8.93% | 10% |
| Pliant | 127 | 16.20% | 15% |
| Malleable | 84 | 10.71% | 10% |
| Primed | 81 | 10.33% | 10% |
| Robust | 83 | 10.59% | 10% |

The largest observed deviation from the accepted integer vector is 1.20 percentage points. Pearson goodness-of-fit is approximately `chi-square=3.19` with `7` degrees of freedom, strongly consistent with that vector. Finite samples cannot prove the underlying constants; the exactness claim is an explicit working decision supported by a clean integer candidate and the observed fit.

### Crumbling Aqueduct final assembly

The final furnishing is also present in client recipe data and must not be confused with its expert components:

- Recipe ID: `38264`
- Result item: `51272`, Crumbling Aqueduct
- `RecipeLevelTable`: `770`
- `IsExpert`: `False`
- Durability/progress/quality: `70/10040/21200`
- Conditions flag: `15` — Normal, Good, Excellent, Poor
- Required quality: `0`

Thus the unresolved expert vector belongs to component RLT `776`. Final assembly RLT `770` is a separate ordinary four-star recipe. A complete Aqueduct workflow catalog must retain both and obtain/verify the ordinary vector separately rather than applying the expert profile to the final assembly.

Useful same-flags controls:

- RLT `759`, flags `1523`: `(20, 12, 0, 15, 8, 15, 10, 10, 10)`
- RLT `773`, flags `1523`: `(51, 12, 0, 8, 3, 7, 5, 5, 9)`
- RLT `776`, flags `1523`: `(20, 10, 0, 15, 10, 15, 10, 10, 10)` (`empirically-inferred`, accepted working exact vector)

These prove that `ConditionsFlag` alone does not determine probabilities and provide clean controls for finding a hidden profile identifier.

## Vector definition

Canonical base-vector order:

```text
[Normal, Good, Good Omen, Centered, Sturdy, Pliant, Malleable, Primed, Robust]
```

Base vectors exclude:

- Excellent and Poor, which are non-expert conditions.
- Deterministic/forced successor behavior.
- Material Miracle's temporary replacement distribution.
- Careful Observation rerolls.
- Mission actions or manual interjections that alter condition generation.

A complete model must retain the full predecessor-to-successor transition matrix until evidence proves that one marginal base vector plus forced edges is sufficient.

## Sources

Current public game-data extraction:

- Recipe: <https://raw.githubusercontent.com/xivapi/ffxiv-datamining/master/csv/en/Recipe.csv>
- RecipeLevelTable: <https://raw.githubusercontent.com/xivapi/ffxiv-datamining/master/csv/en/RecipeLevelTable.csv>
- WKSMissionRecipe: <https://raw.githubusercontent.com/xivapi/ffxiv-datamining/master/csv/en/WKSMissionRecipe.csv>
- WKSMissionUnit: <https://raw.githubusercontent.com/xivapi/ffxiv-datamining/master/csv/en/WKSMissionUnit.csv>

Existing empirical vectors:

- Spreadsheet: <https://docs.google.com/spreadsheets/d/1YJVm9XkE7vLI4BXUeRnRgBSpwvALUeX0p-UxxZu8SPs/edit?usp=sharing>
- Methodology/source thread: <https://www.reddit.com/r/ffxiv/comments/1sfzcwn/the_precise_odds_of_expert_recipe_conditions/>

Client structure evidence:

- CraftEventHandler: <https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/Game/Event/CraftEventHandler.cs>
- Reverse-engineering rename database: <https://github.com/aers/FFXIVClientStructs/tree/main/ida>

The mapped client structure contains `ConditionsFlag` and the currently realized `Condition`, but no probability vector. The standard EXD rows likewise contain allowed-condition flags, not weights.

Local client provenance used during investigation:

```text
ffxiv_dx11.exe PE timestamp: 2026-08-10 19:13:37
SHA-256: 74f0408ad357ba35b20b6fad8c5bfa70c4b07a5a345f8840b3ea368ab395bdf0
```

Searching that executable for a known Oizys vector in obvious byte, little-endian `u16`, and `f32` encodings produced no matches. This is weak negative evidence only. It does not exclude encoded/compressed tables, indirect profile parameters, packed client data, event scripts, or generated native logic.

### Proven craft action packet path

Static decompilation plus a live RLT `776` capture on the current client establishes this request/response chain:

```text
ActionManager.UseAction(CraftAction, actionId)                     0x1408EC540
  -> EventFramework craft-action dispatch                         0x140B78290
  -> CraftEventHandler action request                             0x140E9DE20
  -> build scene payload [10, actionId, 0, 0]                     0x140EA3B90
  -> EventSceneModule.AddFinalizeSceneTask                        0x1417C44A0
  -> FinalizeSceneTask invokes EventHandler vtable slot 41        0x1417BCD70
  -> PacketDispatcher.SendEventCompletePacket                     0x140B2E890
  -> NetworkModuleProxy.SendPacket

server response

PacketDispatcher.HandleEventPlayPacket                            0x140B2F2D0
  -> EventFramework.ProcessEventPlay                              0x140B7C270
  -> EventFramework.ProcessInitializeScene                        0x140B89400
  -> copy scene data into g_InitializeSceneDataValues             0x142ACD590
  -> InitializeSceneTask invokes CraftEventHandler slot 35        0x140B864D1
  -> CraftEventHandler.vf35                                       0x140EA0430
  -> store response payload +0x38 into handler Condition +0x427
```

The outgoing payload for ordinary `Observe` is four `u32` values: event operation `10`, action ID `100023`, `0`, `0`. It contains no condition result, weights, profile ID, PRNG state, progress, quality, or durability.

The live response capture used recipe `38247`, RLT `776`, conditions flag `1523`, and event operation `9`:

```text
CRAFT_EVENT recipe=38247 rlt=776 flags=1523 event=9
payload +0x10: 100023       action ID: Observe
payload +0x18: 3            step
payload +0x30: 60           durability
payload +0x38: 1            realized condition: Normal
payload +0x44: 0x30000012   result/state flags
```

Observation: the client request communicates the selected action; the inbound packet supplies the authoritative resulting state and realized condition. Inference: the condition transition is evaluated beyond the traced client request path, normally by the game server. Unknown: the server's exact profile table, PRNG implementation, and seed. A separate dormant client table cannot be disproved globally, but it is neither referenced nor needed by this live action path.

## Reproducing the profile catalog

1. Read every `Recipe` row where `IsExpert=True`.
2. Join `Recipe.RecipeLevelTable` to `RecipeLevelTable.#`.
3. Calculate observable recipe values with the recipe factors:

   ```text
   progress   = floor(RecipeLevelTable.Difficulty * Recipe.DifficultyFactor / 100)
   quality    = floor(RecipeLevelTable.Quality * Recipe.QualityFactor / 100)
   durability = floor(RecipeLevelTable.Durability * Recipe.DurabilityFactor / 100)
   ```

4. Preserve both identities:
   - Candidate profile identity: `RecipeLevelTable` row ID.
   - Observable signature: `(durability, progress, quality, conditions flag)`.
5. For Cosmic recipes, additionally retain `WKSMissionUnit`, `WKSMissionRecipe`, recipe slot/stage, planet, and active mission action. Do not collapse mission contexts until equality is tested.
6. Parse the empirical spreadsheet's probability columns and reconstruct its conditions flag from non-zero condition entries.
7. Join spreadsheet rows to observable signatures, then verify that every `RecipeLevelTable` maps consistently to one vector.
8. Unknown, absent, ambiguous, or conflicting mappings remain explicitly unknown. Never substitute the generic flag-based probabilities currently used by `Vulcan.GameStateBuilder`.

## Obtaining missing or newly added vectors

Use this order. It distinguishes exact authoritative data from observed outcomes and statistical inference.

### 1. Dynamic event-path reverse engineering — completed for the normal action path

The current client path is documented above. Repeat the capture after game updates to verify signatures, offsets, packet layout, and behavior. Use RLT `759` and `773` as known same-flags controls and RLT `776` as the unknown target.

Interpretation:

- Payload contains weights: extract them directly.
- Payload contains a stable hidden profile ID: map each `RecipeLevelTable` to that ID, then locate the referenced table.
- Client calls local RNG/table code: reverse-engineer that routine and extract every profile.
- Current result: outgoing action payload contains only operation/action ID/zeroes; inbound `EventPlay` supplies the realized result. No client RNG, weights, seed, or profile key occurs on this path. Treat intercepted outcomes as samples, not extracted constants.

The comparison is deliberately between profiles with identical allowed-condition flags but different known vectors. Any stable differing field is therefore a strong profile-key candidate rather than merely another copy of `ConditionsFlag`.

### 2. Static client and packed-data reverse engineering

Import the matching `ffxiv_dx11.exe` into Ghidra and apply the matching FFXIVClientStructs rename database. Trace:

- Writes to `CraftEventHandler.Condition`.
- Reads of `CraftEventHandler.ConditionsFlag`.
- Craft-start and action-result event handlers.
- RNG calls, lookup tables, profile IDs, and network/event payload copies on that path.

Also inspect the matching client `sqpack` data, event scripts, scenario/custom-define resources, and any table referenced by the traced native path. Search by structural references and known control profiles, not only by literal decimal/float vectors. Include both component RLT `776` and final-assembly RLT `770` in the trace.

Record the executable hash and game build for every extracted result. This investigation used Ghidra `12.1.2` with the matching FFXIVClientStructs rename data.

### 3. Empirical live collection and validation

Use this when exhaustive client tracing cannot recover the weights, and independently to validate extracted behavior. Use a dedicated acquisition path, not a solver or simulator:

1. Start Trial Synthesis for the target recipe.
2. Intercept event-operation `9` action results and use ordinary `Observe` repeatedly to advance conditions without progress, quality, or durability changes.
3. When CP can no longer pay for `Observe`, abort and restart Trial Synthesis.
4. Record every transition before deriving any vector.

Do not use Careful Observation or Material Miracle while collecting the base distribution. Both require separate experiments.

Each raw event must include at least:

- Game build and executable hash.
- Session/run identifier and timestamp.
- Recipe ID and `RecipeLevelTable` ID.
- Effective durability/progress/quality and conditions flag.
- WKS mission/unit/slot/stage and active mission action, when applicable.
- Trial versus real synthesis.
- Step index and action ID.
- Previous and next condition.
- Whether the transition was forced or eligible for base-distribution inference.
- Relevant temporary condition-altering effects.

Raw events are immutable evidence. Derived counts/vectors must remain reproducible from them.

## Required validity checks

Trial Synthesis and `Observe` are collection conveniences, not assumed truth. Validate:

- Trial Synthesis versus real synthesis on affordable known profiles.
- `Observe` transitions versus ordinary step-advancing actions.
- Multiple recipes/jobs sharing the same `RecipeLevelTable`.
- Multiple independent sessions.
- Transition distributions stratified by predecessor condition.
- Initial craft condition separately from post-action transitions.
- Forced Good Omen/Robust successors separately from base sampling.
- Material Miracle as its own temporary transition model.

If any stratification differs materially, store the richer context-specific transition matrix. Do not force the observations into one vector.

## Statistical acceptance

- Start with at least 10,000 eligible transitions for an unknown profile.
- Continue adaptively when any probability remains ambiguous.
- Use simultaneous 99% multinomial confidence intervals.
- Fit candidate integer-percentage vectors by multinomial likelihood; require a unique supported candidate before reporting an integer vector.
- Test recipe, action, predecessor, trial/real, and session homogeneity with multiple-testing correction before pooling samples.
- Reproduce several known same-flags/different-vector profiles before trusting the collector on RLT `776`.

Finite sampling cannot prove exact server constants. Labels:

- `extracted-exact`: weights obtained from authoritative client/server data or executable logic.
- `empirically-inferred`: statistically supported vector with raw counts and confidence bounds.
- `provisional-public`: imported empirical result not yet reproduced locally.
- `unknown`: insufficient or conflicting evidence.

## Completion gate

Condition-profile acquisition is complete only when:

- All 44 current expert `RecipeLevelTable` rows map to a provenance-bearing profile.
- RLT `776` is extracted or empirically inferred.
- Trial/real and action-independence checks pass, or their differences are modeled.
- Forced transitions and Material Miracle are represented separately.
- Raw evidence and derived mappings are versioned by game build/hash.
- No unknown profile silently falls back to condition-flag guesses.

Only after this gate should the data be wired into the faithful simulator or any adaptive expert solver.
