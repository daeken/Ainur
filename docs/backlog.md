# Confirmed product backlog

Product directions recorded on **2026-10-06**. This is a backlog, **not an implementation claim, feature authorization, release plan or specification**. Runtime reliability and recovery work remain the implementation priority; optional documentation must not delay them.

The original milestones in [the specification](spec.md) are historical plans and may already be implemented in part. They are not a current outstanding-work checklist. Consult [source status](../README.md#source-status-and-release-boundaries), feature documentation and exact verification evidence for implementation status; source acceptance is not deployment.

## Workbooks

The [first workbook increment](workbooks.md) documents PowerShell execution in fresh runspaces without cross-cell variables or a C# cell runner. The confirmed follow-on backlog is:

- **Persistent variables between cells:** stateful cell execution. This does **not** require or promise variable serialization across server restarts.
- **C# workbook support:** a C# kernel/execution option alongside PowerShell.
- **Pluggable kernels:** a language/kernel extension mechanism that keeps the public/core installation separable from optional internal implementations.
- **Future Python and JavaScript kernels:** optional language implementations through the pluggable-kernel contract. These are future integration directions, not bundled dependencies or available integrations. Access, licensing and distribution rights must be verified independently; no external code retrieval is authorized.

Open design questions: kernel/session ownership and lifecycle, reset/restart behavior, concurrency, cancellation, state scope and language-extension contracts. Cross-restart persistence remains undecided, not a confirmed requirement.

## Platform and knowledge

- **Database backups:** general durable backup capability, separate from the current minimal recovery/cutover safety work. Retention, consistency, restore rehearsal and operator experience remain open. This entry authorizes no live backup, restore or deletion operation.
- **Arda-to-Arda delegation:** supported delegation between projects, beyond within-project agent delegation. Authority, ownership, evidence, budget allocation, return and cancellation semantics remain open.
- **Advanced knowledgebase capabilities:** further retrieval, organization and versioning improvements; exact enhancements are still to be refined.
- **Explicitly scoped knowledgebase sharing across Ardar:** share a KB with one or more selected projects, such as general development guidance or security-tool knowledge. These are examples, not existing shared stores or permission to import their contents. Access, ownership, provenance, revision and revocation semantics remain open. This entry authorizes neither cross-project reads nor external-content import.

These open questions are prompts for later bounded design and acceptance work, not settled designs or permission to access internal repositories, services, credentials or data.

## User-confirmed next UI work — 2026-10-06

These are next-work backlog items, not implemented features, a specification or permission to change providers, models, budgets or credentials. Runtime reliability and recovery work remain the priority.

- **Settings and appearance:** add a settings menu with a configurable theme. The user strongly prefers pastel pink, while retaining user choice. Allow showing or hiding the verbose left-hand status display.
- **Available-model list:** provide a way to view and edit the available-model list. Editing semantics and authority remain open; this does not authorize provider/credential changes or changes to active model assignments.
- **Compact accounting:** make the top-right value/budget display smaller and less verbose, with detailed accounting still accessible. Preserve correct distinctions between accounting values rather than collapsing them into a misleading total.
- **Compact composer and attachments:** replace the separate attachment section with a simple attachment button to the left of the message input. Keep Send on the same line and reduce the composer's vertical footprint, while preserving attachment selection/removal feedback, keyboard usability and narrow-screen usability.
- **Conversation navigation:** entering a conversation tab or switching Ardas should land directly at the intended conversation position, without a visible hard-scroll journey. Restore-position versus latest-message policy remains open; do not impose blanket force-to-bottom behavior or disrupt reading older messages.
