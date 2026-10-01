# Ainur Project and Runtime Specification Draft

Ainur is a local, single-user agent orchestrator for building substantial projects over long periods. Each project has an organized team with persistent identities, explicit ownership, and autonomous management. The user works exclusively through the team's manager. Ainur owns agent execution from provider requests through tool execution, coordination, accounting, and recovery.

The first complete milestone is a team that can develop, verify, release, and autonomously upgrade Ainur itself, then continue its work after the upgrade. A working delegation demo alone does not satisfy that milestone.

## Confirmed product decisions

- Start with a local service and a simple web UI. Remote operation, management across systems, and a mobile client are future directions. Multiple users are an explicit non-goal.
- Support both persistent agents and ephemeral agents. A persistent agent belongs to one project and owns its identity file end-to-end, including its name, pronouns, and personal learnings. It may edit that file freely.
- User conversation happens exclusively with a team's manager. Users can inspect all work and redirect the organization through that manager.
- Managers have full organizational autonomy by default. They may create agents, choose models, delegate, restructure teams, and retire agents without requesting routine user approval. User direction can override those choices.
- Support OpenAI, Anthropic, xAI, Z.ai, and DeepSeek. Model choice is independent of agent identity and role. Higher management is expected to use stronger models and delegate detailed implementation and review.
- Evaluate resource efficiency in dollars, using actual charges for paid usage and a conservative dollar valuation for subscription quota consumption. The internal budget charge may exceed immediate cash expense; show both distinctly. Token counts, elapsed time, and code volume are not performance scores.
- Every agent maintains an evolving checklist. Users can inspect nested objectives and understand responsibility throughout the org chart.
- Agent consultations run in forks of the consulted agent's full available context. Forks can make changes and binding decisions. A fork can ask the scheduler to pause overlapping work in its original session.
- Project knowledge searches and edits normally run through ephemeral agents using inexpensive models. Results include summaries and exact references to the source material.
- Own the agent execution loop end-to-end, with built-in tools, MCP support, and tools agents can create and use at runtime.
- Use C# and .NET for the runtime. Run built-in and agent-authored tools inline in the runtime process, with a dedicated execution thread for each Ainu. Initial development favors simplicity over process isolation or minimizing memory use.
- Support direct .NET object composition between tools, including embedded PowerShell. New tools are expected to be added occasionally; extensibility is required, but a sophisticated plugin deployment system is not.
- Support full-context and rolling compaction, with policies primarily distinguished by persistent versus ephemeral agents. Elide tool results automatically after a configured number of turns and explicitly at an agent's request.
- Give each agent a small, goal-selected working set of tools, a cheap-model tool finder over the complete registry, and an LRU cache that bounds tool documentation and schemas in context.
- Assign subscription quota usage a nonzero dollar budget charge, with configurable premiums for uncertainty, scarcity, and possible provider subsidies.
- Use React with TypeScript for the local web UI. Seed model configuration from the existing FlatlineProxy catalog and routes.
- Self-development includes autonomous upgrades. Routine releases do not require a user approval step.

## Technical architecture

The selected stack is a C# and .NET runtime and supervisor, an ASP.NET Core local API, and a React web UI written in TypeScript. SQLite persistence, Git worktrees, a Vite frontend build, and the detailed protocols below are proposed implementation defaults. Built-in and agent-authored tools execute in the main runtime process; a separate supervisor remains responsible for restarting and upgrading that process.

The bootstrap does not introduce separate tool hosts, process-per-agent execution, or a tool sandbox. Ordinary command tools can still launch Git, compilers, and other external programs, and MCP can connect to external servers. A narrow tool execution interface leaves room for isolation later without building that infrastructure now. Future isolated execution would serialize or reconstruct objects at its boundary; arbitrary live .NET objects are only directly shared inside the current process.

Software development is the first supported workflow. Objectives, identities, knowledge, and tools should remain general enough to support other kinds of project later, without implementing those workflows in the bootstrap release.

## Vocabulary

| Term | Meaning |
| --- | --- |
| Arda | A project, including its team, objectives, repositories, knowledge, and configuration. |
| Ainu and Ainur | One agent and agents collectively. |
| Valar | Agents serving as managers. |
| Maiar | Agents serving as specialists. |
| Theme | A project objective that can be broken into owned work. |

Roles are changeable; an agent's identity survives reassignment. Interfaces should pair the mythology with ordinary labels such as project, manager, and objective. Runtime concepts such as sessions and tool invocations do not need mythology names.

## Organization and ownership

An Arda has one root manager. Every other active agent has one reporting manager, producing an acyclic organization tree. Managers can add subordinate managers as the project grows. A user's instruction enters through the root manager for that Arda; inspecting a subordinate does not open a second direct conversation channel with it.

Managers control their reporting subtrees and can negotiate reassignment across teams through their common manager. Reorganization is an atomic operation: it must preserve an acyclic reporting tree and leave every active objective with an accountable owner. Retiring an agent transfers or explicitly closes its unfinished responsibilities and preserves its history. Replacing the root manager transfers the user conversation and project authority explicitly.

The objective hierarchy is distinct from the organization tree. Each objective has one accountable owner, zero or more child objectives, and dependencies on other objectives. Dependencies cannot form cycles. A manager's checklist view includes work delegated through its team; delegation records both the accountable owner of the broader outcome and the owner of each child outcome.

An objective stores its description, completion conditions, owner, parent, dependencies, current state, evidence, and relevant spending. Proposed states are planned, ready, active, blocked, verifying, complete, and canceled. Checklists may grow, shrink, split, or transfer ownership as understanding improves. Removed work becomes canceled or superseded in history rather than disappearing.

Completion requires evidence appropriate to the outcome. Parent objectives identify which children are required; all required children must be complete or explicitly rescoped before the parent is complete. The relevant manager accepts the result or delegates verification. Managers do not need to perform detailed reviews themselves.

## Identity and continuity

Each persistent Ainu has a stable internal identifier and an agent-owned identity file. Names and pronouns are editable identity content, not database keys. The agent can rewrite its own identity file without manager approval; revision history preserves earlier versions without restricting its authorship.

Runtime-assigned role, reporting relationships, budgets, permissions, and objective ownership live outside the identity file. Editing personal content does not implicitly change those operational records. Conversely, reassignment or model replacement does not rewrite personal identity.

Personal learnings and larger memory documents may accompany the identity file. The agent chooses what to retain in its personal memory. Project facts and accepted decisions belong in shared knowledge, where other agents can find and verify them. A personal belief does not automatically become project policy.

An agent may have many sessions over its lifetime. A session contains the current conversation, checkpoints, pending operations, and working artifacts. Persistent agents can sleep without consuming model tokens and wake for assigned work or actionable events. Ephemeral agents have a bounded assignment and an explicit termination condition; their output and costs remain in project history after they terminate.

Long-running work requires deliberate context management. Session checkpoints preserve durable task state, decisions, unresolved questions, and artifact references. The runtime distinguishes the immutable available transcript and tool artifacts from the context view assembled for the next model request; neither is treated as hidden model state. Compaction, tool-result elision, and tool-cache eviction update that view without deleting source evidence.

## Context management

### Full and rolling compaction

Support two explicit compaction modes, selectable at spawn and changeable for later checkpoints. The proposed default is rolling compaction for named persistent Ainur and full compaction for ephemeral agents. A consultation fork inherits the original session's mode initially, despite its temporary lifetime; a manager may override it for the consultation. Ephemeral knowledge and tool-finder agents ordinarily finish before compaction becomes necessary.

Full compaction replaces the whole eligible active conversation with a bounded handoff summary. Rolling compaction replaces only the oldest configured N percent of eligible conversation tokens with a summary, preserving newer conversation verbatim. Measure N using the current provider's token estimate and round the cutoff to complete conversation and tool-exchange boundaries. The rolling summary covers the accumulated compacted prefix, remains bounded, and preserves references to original spans so repeated compaction does not leave an ever-growing chain of summaries.

Both modes preserve current goals, requirements, commitments, accepted decisions, unresolved questions, blockers, and exact evidence references. Summaries record which source ranges they cover and remain distinguishable from original statements. Required runtime instructions, the current identity revision, structured objective state, and pending invocation state are reattached as authoritative records rather than reconstructed solely from summary prose. A summary does not silently change an accepted decision or discard an unresolved user constraint.

Configure the trigger threshold, desired headroom, summary limit, and rolling fraction per context policy. Before admission, budget the complete request: instructions, active identity and objectives, conversation, tool schemas and documentation, expected tool-result space, and reserved output. First apply eligible tool elision and LRU eviction, then compact if necessary. If one rolling pass does not create enough room, perform bounded additional passes or explicitly choose full compaction; avoid repeated summarization that costs tokens without reducing the request. Never dispatch an oversized request because a policy target could not be met.

Compaction runs only at a committed boundary. Unfinished tool exchanges and provider-required reasoning or signature groups remain intact until they can be represented validly. Persist the new context view atomically with its source coverage, policy version, and summary provenance; retain the previous view if summarization fails. Compaction requests use a configured model and a bounded budget, and their actual and effective costs belong to the owning work. The source must fit that model's own window; use a compatible compactor or bounded source segments with provenance-preserving intermediate summaries instead of assuming a smaller model can accept the whole history. If an elided result contains essential detail, the compactor can retrieve a bounded source excerpt through its reference rather than treating an elision marker as the full evidence.

### Tool result elision

Persist each completed tool's model-facing result payload and any referenced durable artifacts before exposing it to the model. The context view may contain its full body, a bounded excerpt, or an explicit elision record with the invocation ID, tool version, outcome, concise description, original size, and immutable artifact reference. Live .NET objects keep their separate transient lifecycle; retaining an exact result payload does not imply serializing an arbitrary object graph. Elision never means deleting the durable result or pretending a truncated result is complete.

Automatically elide eligible result bodies after a configurable N subsequent completed model turns following their first consumption. For this policy, a turn is one completed model response within the session, including a response that requests tools; retries that never produce a committed response do not advance the counter. A result first enters the following request, and that request's committed response establishes its initial consumption point. Later committed responses advance its age; replaying the same body as part of conversation history does not reset the counter. An Ainu can explicitly elide one result or a set of completed results sooner, including immediately after extracting what it needs from a large response.

A per-result size limit and an aggregate budget for the entire result batch apply before responses are inserted into a model request. Oversized results produce a bounded preview and reference immediately, so the agent need not absorb the entire payload before asking to elide it. Multiple individually small results must also fit collectively; shorten previews or use reference-only bodies while preserving every required call/result pairing. An always-available context tool can retrieve a specified excerpt or range by reference, or temporarily retain a relevant result within the context budget. Retrieved excerpts are bounded and age normally; requesting an excerpt does not automatically restore the whole result or every previous excerpt.

Provider-facing reconstruction must preserve valid tool-call and tool-result relationships and any required native blocks. Replace only eligible content, retain matching identifiers and outcome information, or rebuild a valid summarized conversation boundary when a provider cannot accept surgical replacement. Do not leave orphaned results or mutate signed blocks. For stateful provider APIs, use an explicit context-edit facility or rebuild the request from the selected view; continuing a server-side conversation that still includes removed material is not effective elision.

Automatic and explicit elision, compaction, and discovery-result eviction use the same context builder. Their policy state, turn counters, and source references survive restart. Each change commits a new context revision for future requests; an already-dispatched request retains its snapshot. Consultation checkpoints capture the exact effective context, result ages, and loaded tool versions; forks then advance their counters and cache recency independently. Raw evidence remains available even when the visible context is compacted or elided.

## Execution and provider support

The scheduler owns the loop of selecting actionable work, preparing context, calling a model, executing requested tools, persisting results, and deciding whether to continue or sleep. Prompts explain roles; structured tools and runtime checks implement organizational authority, budget allocation, and communication rules.

Each live Ainu has a dedicated managed thread and a serial dispatcher for its primary session. Idle threads wait for work without spinning. Agent state transitions and inline tool entry run through that dispatcher. Asynchronous provider calls and library internals may use other threads; their results return to the owning dispatcher before changing agent state. A per-agent synchronization context or equivalent message pump preserves this ownership across awaits. Starting a dedicated thread or using `Task.Run` alone does not establish it.

At most one model or tool step advances a given session at a time. Independent agents can run concurrently. Ephemeral agents release their execution thread when their assignment ends. A consultation fork receives a temporary execution thread and dispatcher of its own so it can progress while the original session is active; it retains the original's identity and authority. These additional threads represent concurrent sessions, not new persistent agents.

Cooperative cancellation and tool deadlines are the normal stop mechanism. If inline code spins, blocks indefinitely, or ignores cancellation, the supervisor terminates the entire runtime process and restarts it from durable state. Modern .NET does not support safely force-aborting an arbitrary managed thread. The bootstrap accepts that one stuck tool can cause a restart affecting the whole team. The runtime reports invocation identifiers and deadlines to the supervisor before entering tool code, so a stuck agent thread cannot disable this recovery path.

Managers default to organizational tools: assign work, create or retire agents, revise objectives, allocate funds, request consultations, inspect evidence, and accept outcomes. Specialists default to the relevant implementation or review tools. Managers may change roles and assignments autonomously within the project's configured access. Expensive management attention remains visible in accounting, including time spent doing specialist work.

A provider adapter exposes model capabilities, streaming output, tool calls, cancellation behavior, usage reporting, and provider-specific request settings. A shared event format supports orchestration while preserving native message blocks and required provider metadata. Provider-specific reasoning artifacts, signatures, cache information, and tool-call identifiers must not be discarded merely to fit a text-only format.

Changing models does not change identity. Changing providers may require constructing a compatible context from durable records; it must not claim to transplant inaccessible reasoning or incompatible provider state. Consultation forks initially use the original session's model and compatible native context. Later model changes follow the same explicit conversion path as other sessions.

Model configuration records provider, model identifier, price information, context limits, supported tools, and optional settings. Rank does not hardcode a model. Managers can choose less expensive models when they are sufficient and stronger models when the task warrants them.

All five named providers are within the initial self-development milestone. Exercise real delegation across at least two providers early, before implementing the remaining adapters, to expose assumptions about tool semantics and context handling. Acceptance requires live verification of each configured provider; adapter code or mocked responses alone do not prove integration.

### Initial model catalog

The [initial model catalog](model-catalog.md) records the local FlatlineProxy configuration inspected on October 1, 2026. It includes GPT 6 and the configured GPT 5.6 family, Claude Fable 5, Opus 5 and 4.x, Sonnet 5 and Haiku 4.5, Grok 4.5, GLM 5.1 through 5.3, and DeepSeek V4 and V4.1 models. Listed models, request aliases, upstream identifiers, and enabled routes are separate facts; a catalog entry alone does not establish a working route.

Proposed initial assignments are `gpt-6` for the root manager, `claude-sonnet-5` for implementation, `gpt-5.6-sol` for independent verification, and `deepseek-v4-flash` for routine knowledge work. These are editable starting roles, not measured rankings or fixed organizational policy. Managers can use stronger or alternative models from the catalog as the work requires. Live route verification and usable cost information precede any automated choice based on price.

FlatlineProxy is a source for the initial model and route records; the design does not require embedding its Rust implementation or depending on Codex to run Ainur. Native provider adapters and an optional explicitly configured proxy transport can implement the same Ainur provider interface. Record logical model, requested alias, upstream model, effective route, authentication reference, capability provenance, and price schedule separately. Proxy-specific metadata and native provider capabilities must not be conflated.

The reference configuration contains subscription routes, disabled paid routes, inherited UI capability fields, and zero price placeholders. Import only explicit metadata with its provenance, and retain unknown values where validation is missing. If a proxy chooses among routes, Ainur needs the selected route and usable usage data to report its cost accurately; otherwise cash cost remains estimated or unknown and admission uses a configured conservative budget valuation. Subscription marginal cash cost, allocated subscription expense, and imputed quota charges remain distinguishable. Details and source references are in the catalog.

## Authoritative consultation forks

A consultation creates a new session for the consulted agent's existing identity. It is not a new subordinate or a replacement for the original session. It inherits that agent's project authority and tool access, subject to a bounded consultation budget and purpose. Its decisions and changes do not require routine ratification by the original session.

Each fork begins from an immutable checkpoint of the same application-visible context the original would receive at that boundary: its current conversation, identity revision, task state, relevant artifacts, provider context, and tool versions. It retains access to the same archived history without forcing all lifetime history into one model request. Hidden provider reasoning that was never exposed cannot be copied. Replacing the original's active context with a new summary alone is not a full-context fork. The implementation may share immutable storage and provider caches where supported; it still accounts for the actual cost of supplying context.

The fork has its own dispatcher, live-object registry, and any required PowerShell runspace. Context duplication does not imply that arbitrary mutable .NET objects can be deep-cloned. Forks reconstruct values from durable artifacts or explicit snapshot functions; immutable, thread-safe values may be shared deliberately. A handle owned by the original session is not silently treated as a mutable object owned by the fork.

An active model request has no stable mid-generation checkpoint. Fork creation uses the last committed boundary and records operations still in flight. If the requested consultation depends on an unfinished result, the scheduler waits for a safe boundary or pauses the original before taking the checkpoint. The caller receives the checkpoint identity and knows which state the consultation represents.

For code work, context includes the repository revision and relevant uncommitted changes. A fork starts in an isolated worktree materialized from that snapshot, not merely from the latest committed branch. Original and fork sessions never write concurrently into the same working directory.

A consultation records the requester, consulted identity, original and fork sessions, question or requested change, context checkpoint, related objectives, budget, causal ancestry, and completion condition. It permits a bounded exchange with the requester followed by one final result.

Fork authority does not eliminate concurrency conflicts. The runtime coordinates declared write scopes such as files, interface contracts, or shared records using expiring ownership claims and revision checks. Before changing overlapping work, a fork can issue a structured pause request specifying the original session, affected scope, reason, and expected release condition.

The scheduler enforces that request at an operation boundary. A requested pause becomes an acknowledged pause only after overlapping execution reaches a safe boundary. It stops dispatching overlapping work, records acknowledgement of quiescence, and returns the scope to the original once the fork has released it and outstanding operations are reconciled. Unrelated work can continue. Claims carry monotonically increasing generations; the integration path and managed shared-state writes reject results from a revoked generation. Expiration revokes the claim and starts recovery, rather than immediately authorizing another writer while an old operation may still be running. Cancellation of an in-flight external operation is not assumed to undo it; uncertain outcomes must be reconciled before either session relies on them.

Write claims coordinate known overlap but cannot prove the absence of semantic conflicts. Changes enter a serialized integration path, are checked against current revisions, and undergo the relevant verification. Stale or incompatible changes require reconciliation instead of silently overwriting newer work. Shared identity and knowledge writes use the same revision discipline without requiring manager approval.

After completion, the requester receives the answer or change result with evidence. The original session receives a concise record of decisions, changed artifacts, and required follow-up when relevant. Pure background information is added to its inbox without immediately waking a model. A change that invalidates ongoing work triggers a scheduler action or an actionable reconciliation event.

## Communication rules

Agent communication has explicit types: assignment, consultation, result, decision notice, pause request, resume notice, and escalation. Each type defines whether it wakes a session and whether a response is expected. There is no unrestricted shared chat channel in the initial runtime.

Consultations have limits on exchanges, delegation depth, cost, and lifetime. Notifications and final results do not invite acknowledgement conversations. Further work requires an unresolved question or an explicit new assignment.

Causal ancestry prevents a consultation from recursively recreating the same request through its own participants. The scheduler detects wait cycles among consultations and scope claims. An owning manager can resolve a genuine disagreement without replaying the original loop. Duplicate delivery must not create duplicate work.

The human conversation remains continuous while the team operates in the background. The root manager can surface completed outcomes, meaningful changes in direction, blockers, or exhausted funds. The user need not answer routine operational questions for work to continue.

## Shared project knowledge

Every Arda has a versioned knowledge store searchable and editable through a built-in knowledge service. A requesting agent states its question or intended edit; the scheduler starts an ephemeral agent using an inexpensive configured model to perform the work. This is a service invocation, not a full-context fork of an unrelated teammate. It receives the necessary request context and project access without inheriting the requester's entire conversation.

The initial store uses revisioned text documents and full-text search. Embeddings are optional later. Entries distinguish requirements, accepted decisions, proposals, observations, reference material, and superseded content. Each revision records its author, provenance, relevant objectives, and relationships to prior decisions.

Any agent can contribute edits. Changing an accepted decision creates a traceable replacement decision under the caller's existing authority; the inexpensive editor does not independently acquire the authority to decide project policy. Concurrent edits detect revision conflicts and reconcile them.

Retrieval returns a concise answer, immutable references containing document and revision identifiers, relevant passages, and any unresolved contradictions. The requester can inspect the referenced source directly without opening another conversational loop. Repository sources use commit references and stable paths, with line ranges where useful. Local uncommitted evidence is explicitly identified as a snapshot.

If retrieval or editing exceeds the cheap agent's capability, it returns uncertainty with supporting references or requests an escalation within the configured budget. Missing evidence must not become a fabricated consensus. The service charges its actual cost to the requesting work.

## Dollar accounting and autonomy

Every model request and billable compute operation produces a durable cost record attributed to an Arda, objective, agent identity, session, and causal request. Maintain two dollar views: cash expense, representing actual or estimated provider and compute charges, and effective budget charge, representing the conservative value of consumed resources. Paid API use normally charges its usage-priced amount; subscription use follows the valuation policy below. A consultation's incremental expense is charged to the requesting work while recording the consulted agent as the performer. The original session's historical costs are not duplicated when its context is forked.

Store provider-reported usage and the price schedule used to calculate cost, including input, output, cached input, and other billable categories where applicable. Distinguish estimates, usage-priced amounts, and invoice-reconciled amounts. Use fixed-precision monetary values. Do not label an estimate as a settled charge or assume provider billing reports are always immediately available.

Compute costs come from actual charges where available. Local hardware can use a configured amortized rate, explicitly labeled as estimated; if no defensible rate exists, report the compute amount as unknown rather than silently zero. Raw usage remains available for troubleshooting, but it is not an agent performance score.

Each cost event is recorded once. Views separately show direct expense, delegated expense, consultation and knowledge expense, and total expense. Organization totals aggregate underlying events without adding already-aggregated parent totals. Reorganization preserves who performed and sponsored historical work while supporting current organizational views.

Managers evaluate accepted outcomes against effective dollar charges, including failed attempts and rework, with cash expense and valuation provenance alongside them. Completion evidence establishes whether an outcome was achieved; it is not replaced by a token or time ranking. Attribution of downstream rework may be disputed or unknown and should retain that uncertainty. Avoid a universal numerical score for incomparable tasks.

The user configures each project's effective-dollar budget, available services, and any separate actual-cash ceiling. Managers autonomously allocate and reallocate within those envelopes. Creating a new agent does not create new money or quota. Before dispatch, the runtime reserves an upper bound where possible and enforces concurrency-aware limits; it settles the reservation when usage becomes known. Retries, knowledge workers, tool-finder calls, compaction, consultations, reviews, and provider fallbacks all consume the same allocated funds. Switching from an exhausted subscription to a paid API must fit both the effective budget and the cash ceiling.

Some providers or external operations cannot guarantee an exact spending ceiling. Their configuration must state the remaining exposure and use conservative admission limits. Runtime-mediated operations cannot increase the user's funding envelope through agent identity edits or organizational tools. Exhaustion checkpoints affected work and informs the manager; it does not trigger an unbounded retry or escalation loop.

The intended access scope is project workspaces and explicitly configured services. Organizational autonomy and self-upgrade authority apply within that scope. Secrets stay in host-managed configuration or a credential store and are supplied to the relevant provider adapter or tool invocation; identity files, knowledge entries, and ordinary event logs should not contain them. In-process tools are trusted code with the runtime's host privileges, so this scope is an operational convention rather than a security sandbox.

### Subscription quota valuation

Subscription usage consumes capacity even when it adds no immediate charge to the invoice. Every subscription request therefore incurs a positive effective dollar charge for nonzero consumption. This is an internal allocation and performance price, not a claim about the provider's actual operating costs. A configurable premium can deliberately price above the best estimate to account for uncertain quotas and possible subsidies.

Use a versioned nonzero API-equivalent rate schedule for the model, including relevant input, output, cache, and other usage categories. If no comparable API price is available, require an explicitly configured conservative fallback schedule. Unvalidated zero prices from FlatlineProxy cannot serve as the reference. Separately track applicable account-wide quota windows, including model-specific limits, their remaining capacity, reset cycles, observations, and uncertainty.

A proposed valuation for one request is `effective_charge = max(premium * reference_cost * scarcity_floor, max(window_fraction * window_value * window_scarcity))`. Here `reference_cost` is its API-equivalent or fallback dollar estimate; `premium` is a configured multiplier of at least one; `window_fraction` is its estimated fraction of the complete allowance consumed, including known model weighting; `window_value` is the configured dollar value of a complete window; and each scarcity multiplier is at least one. `scarcity_floor` is the largest applicable scarcity multiplier with usable telemetry, or one when none is known. The inner maximum ranges over windows with a defensible request-level consumption estimate.

Use the maximum across overlapping windows that constrain the same request, rather than adding the same service consumption multiple times. Still reserve capacity and enforce limits in every applicable window independently. When request-level quota consumption cannot be estimated, the positive reference floor remains usable; known remaining capacity can still increase its scarcity multiplier. A configurable table of remaining-capacity bands is sufficient initially. Apply safety headroom and telemetry freshness limits, and record uncertainty rather than implying precise remaining capacity.

Coordinate quota reservations across all Arda sharing an account. Key observations by account, provider limit identifier, and reset cycle. Compute usable capacity after existing reservations and headroom. A fresh provider snapshot already includes some completed work; reconcile its observation time with the journal so completed usage is not deducted twice. Other clients such as Codex can consume the same account limits. Changes in an aggregate quota percentage must not be attributed to whichever Ainur request finished last; use request-level evidence where available and conservative estimates otherwise.

Before dispatch, freeze the model, route, reference schedule, premium, scarcity multipliers, quota observations, and their timestamps in the price quote. Reserve effective dollars and applicable quota capacity atomically within Ainur. On completion, replace the reservation with one committed charge using actual request usage where available and the same quoted valuation parameters; release unused reservations. New telemetry improves future quotes instead of silently repricing past work. Unknown outcomes retain conservative estimated usage pending reconciliation, including after restart. Newly issued retries incur new charges, while duplicate callbacks update the original operation idempotently.

Each Ainur-controlled retry or fallback requires fresh admission, while unresolved earlier attempts retain their reservations. A proxy that chooses its own routes must offer either a fixed route or a bounded configured set of routes and attempts. In the latter case, freeze that envelope in the quote and reserve the cumulative worst-case cash, effective dollars, and account-quota exposure before dispatch; settle against the reported attempts without inventing attribution the proxy cannot provide. If that exposure cannot be bounded, disable automatic proxy fallback or use a route Ainur controls. A conservative effective-dollar estimate alone does not authorize an unknown paid fallback.

Record a subscription invoice once as cash expense. Optional allocations among projects must sum to that invoice and remain reporting allocations. Do not add the invoice, its allocations, and imputed request charges together as actual spending. The UI and manager reports distinguish actual or estimated cash, effective budget charge, consumed quota, uncertainty, and upcoming resets. Provider ceilings remain external constraints; internal dollar headroom cannot authorize requests beyond available quota.

## Built in tools and extensibility

One tool registry covers built-in operations, MCP tools, and agent-authored C# or PowerShell tools. Each registered version declares its name, purpose, model-facing input schema, output contract, implementation, timeout, cancellation behavior, intended access, and retry or idempotency behavior. Provider adapters expose compatible tool descriptions to each model. A shared .NET tool contract handles invocation context, cancellation, results, diagnostics, and artifact references.

Built-in tools cover organization management, objectives, identity and personal memory, consultations, knowledge, files, commands, Git workspaces, verification, cost inspection, and release operations. MCP is an adapter into the same invocation lifecycle, with explicitly configured server access and versioned tool descriptions. Servers need not support every metadata field; unknown retry behavior is treated as unknown, not assumed safe.

### Tool discovery and context cache

The complete registry is searchable without loading its full contents into every agent's context. At spawn, the runtime selects a small initial tool set from the agent's goals, role, and configured access. A consultation fork starts with the original's checkpointed tool set. Keep a minimal core available for tool discovery, context inspection and elision, and essential role-specific control; count that core against the same tool-context budget.

Provide a tool finder backed by a small, inexpensive model in an ephemeral service session. It searches the complete registry available to the requester, including built-ins, MCP tools, and agent-authored tools. Index names and descriptions first, then fetch detailed documentation and exact schemas for a bounded set of relevant candidates. The finder receives the question and necessary goal context, not the requester's full conversation. It returns selected tool IDs and versions, relevant documentation, exact input and output schemas where available, and any limitations. Schemas come from registered versions; the small model selects and explains tools but does not invent or rewrite their contracts.

Selecting a result loads its versioned schema into the requesting session's provider-facing tool set before the next model request. Documentation and schema become readily usable without another open-ended conversation. The context builder presents schemas once where the provider requires them and keeps the finder-result rendering concise, with exact references to the full returned record. Each model request persists its exact name-to-version bindings, pins them until the response and its accepted tool calls settle, and resolves returned calls against that snapshot. Discovery does not expand authority or enable a disabled service; availability and access are checked again at invocation. If no suitable tool exists, return that result explicitly so the agent can choose another approach or create a tool.

Maintain a per-session least-recently-used cache bounded primarily by rendered tool tokens, with an optional count limit. Documentation and schemas count toward that budget, including copies in discovery results. Explicit loading and committed invocation update recency; repeated background retrieval or a failed transport retry does not keep every tool permanently hot. At request boundaries, evict the least recently used unpinned entries. The core remains pinned for the session; other versions involved in an unfinished exchange or an accepted pipeline plan stay pinned until that work settles. A tool-count cap alone is insufficient because schema sizes vary widely.

Eviction removes future tool declarations and elides redundant historical documentation or schema bodies to versioned references. It does not erase invocation history, invalidate the result's provenance, unload the assembly, or dispose live pipeline objects. A later finder call can reload an evicted tool. Changing MCP definitions or registering a new tool invalidates affected cache entries without silently rebinding an already-dispatched call. If an external server cannot honor the recorded contract, report the version conflict instead of guessing arguments or invoking a replacement. Forks and resumed sessions retain the versions and any adapter-required historical definitions needed to replay their context validly.

Configure the tool-token budget as a small part of usable context, with headroom for discovery. If a single complete schema or the temporarily pinned set exceeds that budget, return an explicit capacity result and use a narrower registered wrapper or a deliberate bounded session-budget override. Never silently truncate required schema fields or let the full MCP registry spill into context. Automatic tool-result elision and LRU eviction cooperate so an evicted schema is not still repeated throughout the conversation.

### Inline execution and tool creation

Built-in and agent-authored tools run inline in the owning session's execution flow. C# tools can be ordinary projects compiled with the .NET SDK and loaded as versioned assemblies implementing the shared contract. Embed PowerShell through its hosting API when scripting is useful, with a session-owned runspace created on demand and never used by concurrent pipelines. Runspace invocation must respect its execution-thread configuration; preserve object ownership when PowerShell uses an internal execution thread. The bootstrap uses normal managed deployment with dynamic assembly loading; native ahead-of-time compilation is not a requirement.

Within a session, tools can pass live .NET objects directly through C# calls or a PowerShell pipeline. Objects retained across model turns live in a session-owned registry. The model sees typed handles, useful summaries, and artifact references; the registry resolves those handles to actual objects for subsequent tools. Intermediate values do not need a JSON round trip merely to reach the next inline tool. MCP and external programs use their own wire formats at their respective boundaries.

Handles record the owning session, runtime generation, expected type, and relevant tool version. They are released when no longer needed and cannot silently resolve to a different value after restart. Durable results and checkpoints use serializable records, artifact references, or explicit reconstruction recipes. Process restarts discard live object graphs and runspaces; recovery rebuilds supported objects or reports a stale handle and assigns reconstruction work. Reconstructing an object must not blindly repeat a side effect that may already have happened.

Agents can write a tool, supply its manifest and examples, compile or validate it, run relevant checks, and register it without waiting for user approval. New assemblies or scripts can normally be loaded while Ainur is running. Tool creation is expected to be occasional, so standard SDK builds and simple registration are sufficient initially; an embedded compiler service or extensive plugin marketplace is unnecessary.

Tool versions are immutable and identified by their content and dependency information. Existing sessions retain versions needed by their invocations and live objects; new work may select a newer version. Shared contract types must have consistent assembly identity. Reliable hot unloading is not required: retained references or background code can prevent unloading, and a supervised runtime restart can reclaim old assemblies or apply an incompatible tool update. Reverting a tool selects a known prior version.

Tools are trusted in-process code. Catchable failures become invocation results, while hangs or fatal failures may require whole-runtime recovery. Budget and access checks govern runtime-mediated operations; arbitrary inline code can bypass those paths. The first release does not claim security isolation between tools, agents, runtime state, or credentials. A future isolated backend can sit behind the tool contract if actual usage justifies it.

## Persistence and recovery

SQLite in WAL mode is the proposed local database. Maintain current-state tables and an append-only event journal in the same transactions. Store large immutable artifacts separately using content-addressed references. The database and artifact root live outside disposable release directories and agent worktrees.

The main entities are project, agent, identity revision, reporting relationship, objective, dependency, session, context checkpoint and view, compaction record, result artifact and elision state, consultation, notification, scope claim, knowledge revision, tool version, tool-cache state, tool invocation, model request, cost event, valuation schedule, quota observation, budget and quota reservation, release, and upgrade attempt. Stable identifiers must remain valid across model changes, reorganization, and upgrades.

Record an operation's intent before dispatch and its result before making dependent work runnable. Invocation states distinguish queued, running, succeeded, failed, canceled, and outcome unknown. A crash after an external side effect but before persistence cannot be solved by blindly retrying the request.

Tools that support idempotency receive durable invocation keys. Otherwise, recovery checks destination state where possible and creates a reconciliation task when the outcome is uncertain. Potentially billable model calls also retain uncertain usage until it can be reconciled or explicitly estimated. Exactly-once external execution is not a blanket guarantee.

On restart, the scheduler reconstructs ownership, pending work, reservations, consultations, and scope claims, then recreates the required agent threads and dispatchers. Live object handles from the prior runtime generation are invalid. It reconciles in-flight operations before resuming dependent tasks. Expired coordination claims cannot leave an agent paused indefinitely. The user sees a continuous project and event history across restarts.

A forced restart records the triggering invocation when it is known and leaves interrupted invocations unresolved until reconciled. The same hung invocation is not automatically retried on every boot. Quarantine the problematic tool version or assignment as appropriate and create repair work for its manager. If the runtime died before recording a final status, the supervisor's durable restart reason and pre-dispatch records provide the recovery evidence.

## Autonomous self upgrades

A small C# supervisor runs separately from the main agent runtime. It selects the active release, enforces one active scheduler, checks readiness, and restarts or rolls back failed releases. It also owns the last-resort process termination path for uncooperative inline tools. It contains mechanical release and recovery logic; organizational decisions remain in the runtime.

The autonomous release path is:

1. Assign a scoped Ainur improvement with completion conditions. Develop it in an isolated worktree and keep the active installation unchanged while implementation proceeds.
2. Use an independent verification agent to check the candidate, run relevant tests, and record evidence against those conditions.
3. Build an immutable candidate release. Validate startup, persistence compatibility, and a representative agent workflow against disposable state and a scratch project. Live state is not used for destructive candidate tests.
4. Record a durable upgrade attempt and the previous known release. Stop dispatching new work and drain active sessions to safe checkpoints within a configured deadline. Reconcile or deliberately postpone operations whose external outcomes are uncertain.
5. Have the supervisor stop the old runtime and start the candidate with exclusive ownership of the scheduler lock. The old and new runtimes must not dispatch work simultaneously.
6. Run startup, migration, readiness, and scheduler checks. Resume queued work once the candidate is ready, then monitor it through a bounded probation period.
7. Mark the release successful or automatically return to the previous compatible release. Persist the result and let the team continue without a user conversation being required.

The supervisor owns the drain deadline. If it expires, request supported cancellation, record unknown outcomes durably, and abort the upgrade rather than leaving the organization stopped indefinitely. Restore the previous runtime to service, retaining blocks on scopes whose effects remain uncertain while unrelated work resumes. If the runtime is unresponsive, the supervisor can stop it and recover the previous release under the same invocation-reconciliation rules. An aborted attempt cannot proceed to candidate activation in the background.

Require backward-compatible state changes during bootstrap; prefer additive migrations. Compatibility covers event payloads, checkpoints, tool invocation records, and artifacts as well as table schemas. Candidate validation must exercise the previous release reading state written by the candidate before the candidate is permitted to resume live work. Code rollback retains the durable history, including external operations completed by the candidate. Restoring an old database snapshot is not ordinary rollback: it can erase knowledge of completed operations and cause duplicate execution. Destructive schema cleanup happens only after the compatibility window closes and a tested recovery path exists.

After a failed upgrade, retain the failed release and diagnostics, suppress repeated activation of that same candidate, and assign repair work under the remaining budget. A process that starts successfully but cannot advance runnable work must fail health checks; an intentionally idle or blocked team must not be mistaken for a dead scheduler.

The supervisor can also evolve. Its replacement uses a separate staged handoff with one designated watchdog and a known fallback, rather than letting the active runtime overwrite its only recovery mechanism. Autonomous runtime upgrades are required for the first milestone; autonomous supervisor replacement is a later extension and is not required to call the first loop complete.

## Local web interface

The first UI uses React and TypeScript, with Vite as the proposed build tool and bundled assets served by ASP.NET Core. It contains a project list, manager conversation, org chart, nested objectives, cost views, and activity history. Selecting an agent shows its identity, manager, model, owned work, current state, and direct and delegated spending. Selecting an objective shows completion conditions, dependencies, evidence, and the responsible team.

An agent's context view shows its compaction mode, recent compactions, elided-result references, loaded tools, and the share of context occupied by tools. Cost views distinguish cash expense from effective budget charges and show subscription quota observations, uncertainty, valuation premiums, and reset times. These details make context and cost decisions inspectable without inserting them into the manager conversation on every turn.

Consultations appear as temporary branches of an agent's work, with their purpose, source checkpoint, changes, and outcome. They do not look like newly hired persistent agents. Paused work states why it is paused and what releases it.

The manager conversation is the only agent conversation the user can send messages into. Operational controls such as stop, resume, funding configuration, and release recovery act on the service directly and remain available if the manager or model provider is unavailable. These controls do not introduce direct conversations with subordinates.

Serve the UI and API from the local service with local authentication and browser origin checks. Bind to loopback by default. Use a versioned API and an event stream so a future remote or mobile client does not require replacing the scheduling model. Remote authentication, multi-host scheduling, and mobile applications are outside the initial milestone.

## Implementation sequence

### Durable local foundation

Create the .NET solution, ASP.NET Core API, React and TypeScript UI, database migrations, activity journal, project and agent records, and objective ownership. Implement recovery state before introducing side-effecting agent work. Add the supervisor's initial release selection, runtime lock, and restart path so the foundation can later upgrade without changing its process model.

Completion means a user can create an Arda, inspect its manager and objectives, restart the service, and retain the same project state. No claims of autonomous agent work are made at this stage.

### Delegation and real spending

Implement dedicated agent threads and serial dispatchers, the execution loop, first provider adapters, inline development tools, model usage accounting, separate cash and effective-dollar records, subscription valuation, and shared quota reservations. Seed model entries from the verified FlatlineProxy configuration. Establish manager, implementer, and verification roles while allowing managers to reorganize them. Add the remaining named providers through the same contract.

Completion means the user gives the manager a bounded repository task, the manager delegates implementation and verification across at least two providers, and the UI shows ownership, evidence, cash estimates or charges, and effective dollar costs. A subscription request with no incremental cash charge must still consume effective budget. Concurrent requests across projects must respect shared quota reservations without double-counting overlapping windows or invoices. All five configured providers must pass live integration checks before the overall bootstrap milestone is accepted.

### Persistent collaboration

Implement agent-owned identity and memory edits, checkpoints, full and rolling compaction, automatic and explicit result elision, authoritative consultation forks, coordination claims, bounded communication, and the knowledge service. Integrate code changes through the serialized verification path.

Completion means a fork can make an interface change, pause relevant original work, integrate the change, and let the original resume with a concise update. An independent work branch must remain able to progress. Exercise both compaction modes; rolling compaction must preserve the newer suffix verbatim. Automatic and explicit elision must bound large results while leaving raw evidence retrievable and provider tool exchanges valid. The knowledge service must return inspectable versioned evidence, and restart recovery must retain pending work and context policies correctly.

### Runtime tools and autonomous upgrades

Add MCP, goal-based initial tool selection, the cheap-model tool finder, token-budgeted LRU caching, versioned inline C# tools, embedded PowerShell, live-object pipelines, simple tool registration, candidate release validation, checkpoint draining, activation, probation, and rollback. Give the team Ainur's own repository and a scoped improvement objective.

Completion means the team discovers a previously unloaded tool, uses its exact schema, evicts and later reloads it, and keeps the rendered tool context within its configured budget with a large MCP inventory. It also creates or revises a tool during real work, composes tools using a live .NET object, completes a meaningful change to Ainur, verifies the resulting release, activates it autonomously, and resumes an already-existing project task. A deliberately failing candidate and a deliberately uncooperative inline tool must exercise automatic recovery without duplicating an external operation or losing the durable cost history. Stale object handles must be detected after restart, and the offending invocation must not create a restart loop.

## Decisions still open for implementation

The product direction is established; the following are implementation choices rather than reasons to require routine user approval:

- Choose a supported .NET release compatible with the selected embedded PowerShell SDK, and initial local packaging. C# for the core and React with TypeScript for the UI are settled. A runtime with bundled web assets and its small supervisor should not require several separately operated services.
- Verify the imported FlatlineProxy routes, fill in reliable cash and reference price schedules, choose quota-window valuations and conservative premiums, and set effective-dollar and cash budgets for live development. Existing model configuration is evidence for the starting catalog, not a new grant of credentials or a spending limit.
- Choose practical limits for consultation exchanges, concurrency, notification coalescing, compaction thresholds and rolling fractions, tool-result retention turns, result previews, and tool-cache tokens. Keep them configurable and validate them with real work rather than treating arbitrary initial numbers as product semantics.
- Specify the first self-developed feature and its acceptance conditions before running the autonomous bootstrap demonstration.
