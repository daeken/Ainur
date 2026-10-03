# Strict subscription-only release + automatic rollback composition (SOURCE-ONLY PROPOSAL)

Authority: `decisions/explicit-subscription-only-operating-mode` r1 (`kr_01a0ff3d391e7a7393df9a08c812bbfb`): while enforced reject direct paid OpenAI selection, within-attempt subscription→paid fallback **and manager/helper paid paths**, without mislabeling quota exhaustion or disabling separately authorized API-key use outside mode. Selected running candidate **and every permitted automatic rollback/recovery target** must satisfy strict policy. Prior accepted narrow 766 and 01d6 do NOT confer strict-mode approval; legacy e022 too is not a safe automatic rollback. No strict source/binary SHA was independently accepted at time of this proposal; do not substitute historical release IDs or promise an automatic rollback.

## Dual-target immutable manifest schema (no physical hashes supplied)

```json
{
  "planVersion": "one-outage-strict-v1-NOT-EXECUTABLE",
  "home": "<exact separately accepted immutable existing home path>",
  "scope": { "uid": 501, "label": "gui/501/com.ainur.supervisor.5181", "port": 5181, "sevenAgentBaselineSha256": "<fresh source-reviewed SHA>" },
  "supervisor": { "sourceSha": "<accepted exact SHA>", "binarySha256": "<new offline staged SHA>", "subscriptionOnlyModePin": "<explicit strict-mode configuration + signed value, not just AINUR_OPENAI_ROUTE>" },
  "running": {
    "sourceSha": "<accepted Aule strict-enabled source SHA>", "releaseId": "<new unique immutable release>", "coreSha256": "<reviewed physical Core hash>",
    "serverSha256": "<reviewed physical Server hash>", "strictTestReceiptSha256": "<Yavanna independently accepted offline exact-source strict paid-path rejection>",
    "sameProcessStrictAttestation": "<independently reviewed endpoint/other mechanism proving strict mode; route_class=subscription alone insufficient>"
  },
  "automaticRollback": {
    "sourceSha": "<accepted Aule strict-enabled rollback source SHA>", "releaseId": "<distinct new unique immutable release>", "coreSha256": "<reviewed physical Core hash>",
    "serverSha256": "<reviewed physical Server hash>", "strictTestReceiptSha256": "<Yavanna independently accepted offline exact-source strict paid-path rejection>",
    "sameProcessStrictAttestation": "<independently reviewed endpoint/other mechanism proving strict mode; route_class=subscription alone insufficient>"
  },
  "recovery": { "permittedAutomaticTargets": ["running", "automaticRollback"], "legacyTargetsForbidden": ["e022", "766", "01d6-without-strict-acceptance"],
    "onMissingStrictProof": "STOP_DETACHED_MANUAL_INTERVENTION; no auto-restart, no DB restore, no lease clear" },
  "gates": { "managerGoSha256": "<unissued>", "independentSourceAndBinaryGateSha256": "<unissued>", "nativeAdapterSha256": "<unissued>", "launchdLoadedKeepAliveProofSha256": "<unissued>" }
}
```

The placeholders are **intentionally invalid**: SHA values MUST be exactly 64 hexadecimal digits with independent provenance and real accepted source/binary pairing; an unfilled field is a fail-closed NO-GO, never a prompt to infer SHA from older binaries. There is no safe auto-rollback if only one strict binary exists. Distinct immutable release IDs avoid a retained old `previous` silently pointing to unsafe 766/e022. A safety-only strict target may precede title/image integration; only include UI/title candidate after independently accepted strict acceptance. Any older release remains immutable for forensic/manual recovery, not eligible as automatic target under active strict policy.

## Re-review mandatory before executable controller
- The published v2 controller `expectedRelease=r20261002014730-766fa9f930` and its rollback validation implement a now-disallowed historical mock condition; new source must replace all `expectedRelease`, `expectedCoreHash`, rollback source and candidate hashes, native install and supervisor `previous` semantics, generation binding, detached recovery lookup, and path security. Simply filling the proposed JSON cannot make v2 executable: non-TestMode is hard-aborted until explicit new review.
- A future native adapter must attest **same-process strict-mode enforcement** in addition to bearer route class, provider policy and loaded Core hash, not merely `AINUR_OPENAI_ROUTE=subscription`. If strict flag is missing/unknown or paid fallback remains reachable, stop with an intervention receipt rather than restarting either child. Independently gate tests for direct API model/tool/helper/manager path, within-attempt subscription failure, ordinary authorized API use outside strict mode, false quota classification, cash admission preserved and rollback start under strict mode. No paid probes.
- During controlled maintenance, previously-running e022 is a TEMPORAL old child only: stop under owner checkpoint decision and never reuse as automatic replacement. If strict candidate fails before validated rollback target is available, fail to contained detached outage, not weak binary. Supervisor source must be reviewed for its startup `previous` and rollback fallback behavior and pinned to strict-only allowlist; a job supervisor auto-restarting on crash must not reload legacy default release by stale `current` pointer.
- New strict binary source+physical hashes, actual-schema ledger backup, independent FD route proof, launchd loaded KeepAlive semantics, isolated staging safety, spawn lease and separate go remain unsatisfied. No live controls or OS registration authorized by this proposal.
