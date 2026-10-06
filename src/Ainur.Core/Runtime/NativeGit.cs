using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Ainur.Core.Model;
using Ainur.Core.Persistence;
using Ainur.Core.Tools;
using Dapper;

namespace Ainur.Core.Runtime;

public sealed class GitRepository {
	public string Id { get; set; } = "";
	public string ProjectId { get; set; } = "";
	public string Path { get; set; } = "";
	public string CommonDir { get; set; } = "";
	public string IntegrationBranch { get; set; } = "";
	public string OwnerId { get; set; } = "";
	public string? Remote { get; set; }
	public string? RemoteBranch { get; set; }
	public string? FetchEndpointHash { get; set; }
	public string? PushEndpointHash { get; set; }
}

public sealed class GitWorkspace {
	public string Id { get; set; } = "";
	public string RepositoryId { get; set; } = "";
	public string ProjectId { get; set; } = "";
	public string ObjectiveId { get; set; } = "";
	public string OwnerId { get; set; } = "";
	public string Path { get; set; } = "";
	public string Branch { get; set; } = "";
	public string BaseSha { get; set; } = "";
	public string State { get; set; } = "";
	public string? ReviewHead { get; set; }
	public string? Evidence { get; set; }
	public string? ReviewerId { get; set; }
	public string? AcceptanceHead { get; set; }
	public string? AcceptanceBase { get; set; }
	public string? AcceptanceActor { get; set; }
	public string? IntegratedHead { get; set; }
	public string PublicationState { get; set; } = "";
	public string Retention { get; set; } = "";
}

/// <summary>Opt-in Git ownership and exact review receipts. No scheduler, automatic merge/push, reset or dirty cleanup.</summary>
public sealed class NativeGit(AinurRuntime rt, CancellationToken cancellationToken = default) {
	static readonly AsyncLocal<CallScope?> Current = new();
	internal Func<ProcessStartInfo, ProcessStartInfo>? ProcessStartForTests { get; init; }
	internal TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(90);
	internal TimeSpan ProcessTimeout { get; init; } = TimeSpan.FromSeconds(30);
	sealed class CallScope : IDisposable {
		readonly CallScope? prior = Current.Value;
		internal readonly CancellationTokenSource Deadline;
		internal IUnknownMutationLease? Lease;
		internal string? InterruptedDiagnostic;
		internal bool ProcessAttempted;
		internal readonly TimeSpan ProcessTimeout;
		internal readonly Func<ProcessStartInfo, ProcessStartInfo>? ProcessStartForTests;
		internal CallScope(CancellationToken token, TimeSpan timeout, TimeSpan processTimeout, Func<ProcessStartInfo, ProcessStartInfo>? processStartForTests) {
			ProcessStartForTests = processStartForTests; ProcessTimeout = processTimeout; Lease = prior?.Lease;
			Deadline = CancellationTokenSource.CreateLinkedTokenSource(token, prior?.Deadline.Token ?? default);
			Deadline.CancelAfter(timeout); Current.Value = this;
		}
		internal void ProcessAttempt() { ProcessAttempted = true; prior?.ProcessAttempt(); }
		public void Dispose() { Current.Value = prior; Deadline.Dispose(); }
	}
	CallScope BeginCall() => new(cancellationToken, CallTimeout, ProcessTimeout, ProcessStartForTests);
	IUnknownMutationLease Admit(string kind, string projectId) {
		CheckCancellation();
		var lease = rt.AdmitMutation(kind, projectId); Current.Value!.Lease = lease; return lease;
	}
	static void CheckCancellation() {
		if(Current.Value is { } scope && scope.Deadline.IsCancellationRequested) {
			if(scope.ProcessAttempted) scope.Lease?.MarkUnknown();
			throw new DomainException("Native Git call deadline/cancellation before next phase; inspect admitted effects, no subprocess replay.");
		}
	}
	public IReadOnlyList<GitRepository> Repositories(string projectId) => rt.Db.Read(c => c.Query<GitRepository>("SELECT * FROM git_repositories WHERE project_id=@projectId", new { projectId }).AsList());
	public IReadOnlyList<GitWorkspace> Workspaces(string projectId) => rt.Db.Read(c => c.Query<GitWorkspace>("SELECT * FROM git_workspaces WHERE project_id=@projectId ORDER BY created_at", new { projectId }).AsList());
	GitRepository Repository(string projectId, string id) => rt.Db.Read(c => c.QuerySingleOrDefault<GitRepository>("SELECT * FROM git_repositories WHERE project_id=@projectId AND id=@id", new { projectId, id })) ?? throw new DomainException("Registered repository not found in this project.");
	GitWorkspace Workspace(string projectId, string id) => rt.Db.Read(c => c.QuerySingleOrDefault<GitWorkspace>("SELECT * FROM git_workspaces WHERE project_id=@projectId AND id=@id", new { projectId, id })) ?? throw new DomainException("Owned workspace not found in this project.");

	void Actor(string projectId, string actor) {
		if(actor == "human") return; // Only the locally authorized HTTP surface supplies this principal.
		var agent = rt.Store.GetAgent(actor);
		if(agent?.ProjectId != projectId || !AgentStates.IsLive(agent.State) || agent.State == AgentStates.Paused || agent.PrimarySessionId is { } sid && rt.IsPaused(sid))
			throw new DomainException("Actor unavailable, paused, or outside this project.");
	}
	void Author(GitWorkspace w, string actor) {
		Actor(w.ProjectId, actor);
		if(actor != "human" && actor != w.OwnerId) throw new DomainException("Only the workspace owner may author, submit or abandon it.");
	}
	void Integrator(GitRepository r, string actor) {
		Actor(r.ProjectId, actor);
		if(actor != "human" && actor != r.OwnerId) throw new DomainException("Only the registered repository integration owner may integrate, reconcile or publish.");
	}
	static string Canonical(string path) {
		CheckCancellation();
		var full = System.IO.Path.GetFullPath(path);
		var root = System.IO.Path.GetPathRoot(full)!;
		var current = root;
		foreach(var segment in full[root.Length..].Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)) {
			CheckCancellation();
			current = System.IO.Path.Combine(current, segment);
			if(Directory.Exists(current) && new DirectoryInfo(current).ResolveLinkTarget(true) is { } link) current = Canonical(link.FullName);
		}
		return current;
	}
	static bool Contains(string root, string child) {
		var p = System.IO.Path.GetRelativePath(root, child);
		return p == "." || !System.IO.Path.IsPathRooted(p) && p != ".." && !p.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal);
	}

	public GitRepository Register(string projectId, string actor, string path, string branch, string owner, string? remote = null, string? remoteBranch = null, string? operationId = null) {
		using var call = BeginCall(); CheckCancellation();
		using var admission = Admit("git-register", projectId);
		Actor(projectId, actor);
		var project = rt.Store.GetProject(projectId) ?? throw new DomainException("Project not found.");
		if(actor != "human" && actor != project.RootAgentId) throw new DomainException("Repository registration requires the project lead or human.");
		if(owner == "human") throw new DomainException("Name a project agent as the durable integration/publication obligation owner; humans may explicitly act through the local API.");
		Actor(projectId, owner);
		if(string.IsNullOrWhiteSpace(operationId) || !Regex.IsMatch(operationId, "^[a-zA-Z0-9][a-zA-Z0-9._:-]{0,127}$")) throw new DomainException("Registration requires a caller-chosen operation_id (1-128 safe characters); never replay UNKNOWN.");
		var intentId = operationId;
		lock(rt.AdmissionGate) {
			rt.RequireResolvedGitRegistrations(projectId);
			rt.Db.Write(u => {
				rt.RequireResolvedGitRegistrations(projectId, u);
				if(u.Scalar<long>("SELECT COUNT(*) FROM git_registration_intents WHERE id=@intentId", new { intentId }) != 0)
					throw new DomainException("Registration operation_id already exists; inspect/reconcile_registration, never replay.");
				u.Execute("INSERT INTO git_registration_intents(id,project_id,actor_id,requested_path,integration_branch,owner_id,remote,remote_branch,state,created_at,updated_at) VALUES(@intentId,@projectId,@actor,@path,@branch,@owner,@remote,@remoteBranch,'running',@now,@now)", new { intentId, projectId, actor, path, branch, owner, remote, remoteBranch, now = Clock.Now });
				u.Journal("git.registration.intent", projectId, "git_registration", intentId, actor == "human" ? null : actor, new { operationId = intentId });
			});
		}
		try {
		path = Canonical(path);
		if(project.WorkspacePath is null || Canonical(project.WorkspacePath) != path) throw new DomainException("Repository must be this project's exact configured workspace.");
		if(rt.Store.ListProjects().Any(p => p.Id != projectId && p.WorkspacePath is { } other && (Contains(Canonical(other), path) || Contains(path, Canonical(other)))))
			throw new DomainException("Repository overlaps another project.");
		if(branch.StartsWith('-') || !Regex.IsMatch(branch, "^[a-zA-Z0-9][a-zA-Z0-9/._-]*$") || !Git(path, "check-ref-format", "--branch", branch).Ok)
			throw new DomainException("Invalid integration branch.");
		if((remote is null) != (remoteBranch is null)) throw new DomainException("Publication requires both remote name and remote branch, or neither.");
		if(remote is not null && (!Regex.IsMatch(remote, "^[a-zA-Z0-9][a-zA-Z0-9._-]*$") || !Git(path, "remote").Text.Split('\n').Contains(remote) || remoteBranch!.StartsWith('-') || !Git(path, "check-ref-format", "--branch", remoteBranch).Ok))
			throw new DomainException("Publication must use an existing named remote and valid branch; no credentials are provisioned.");
		var common = Canonical(Require(path, "rev-parse", "--path-format=absolute", "--git-common-dir"));
		using var baton = Baton(common);
		CleanTarget(path, branch);
		if(Canonical(Require(path, "rev-parse", "--show-toplevel")) != path || Require(path, "rev-parse", "--is-bare-repository") != "false") throw new DomainException("Register a normal exact repository checkout, not a bare/nested checkout.");
		var authority = remote is null ? null : CaptureAuthority(path, remote);
		var prior = Repositories(projectId).SingleOrDefault(r => r.CommonDir == common);
		if(prior is not null) {
			if(prior.Path != path || prior.IntegrationBranch != branch || prior.OwnerId != owner || prior.Remote != remote || prior.RemoteBranch != remoteBranch || prior.FetchEndpointHash != authority?.FetchHash || prior.PushEndpointHash != authority?.PushHash)
				throw new DomainException("Repository is already registered with different authority or policy.");
			CompleteRegistration(intentId, prior);
			return prior;
		}
		var r = new GitRepository { Id = Ids.New("repo"), ProjectId = projectId, Path = path, CommonDir = common, IntegrationBranch = branch, OwnerId = owner, Remote = remote, RemoteBranch = remoteBranch, FetchEndpointHash = authority?.FetchHash, PushEndpointHash = authority?.PushHash };
		BindRegistration(intentId, r);
		rt.Db.Write(u => {
			u.Execute("INSERT INTO git_repositories(id,project_id,path,common_dir,integration_branch,owner_id,remote,remote_branch,fetch_endpoint_hash,push_endpoint_hash,created_at) VALUES(@Id,@ProjectId,@Path,@CommonDir,@IntegrationBranch,@OwnerId,@Remote,@RemoteBranch,@FetchEndpointHash,@PushEndpointHash,@now)", new { r.Id, r.ProjectId, r.Path, r.CommonDir, r.IntegrationBranch, r.OwnerId, r.Remote, r.RemoteBranch, r.FetchEndpointHash, r.PushEndpointHash, now = Clock.Now });
			u.Journal("git.repository.registered", projectId, "repository", r.Id, actor == "human" ? null : actor, new { r.IntegrationBranch, r.OwnerId, publication = remote is not null });
		});
		CompleteRegistration(intentId, r);
		return r;
		} catch {
			admission.MarkUnknown();
			rt.Db.Write(u => u.Execute("UPDATE git_registration_intents SET state='unknown',updated_at=@now WHERE id=@intentId AND state='running'", new { intentId, now = Clock.Now }));
			throw;
		}
	}

	void BindRegistration(string id, GitRepository r) => rt.Db.Write(u => u.Execute("UPDATE git_registration_intents SET repository_id=@Id,canonical_path=@Path,common_dir=@CommonDir,fetch_endpoint_hash=@FetchEndpointHash,push_endpoint_hash=@PushEndpointHash,updated_at=@now WHERE id=@intent", new { r.Id, r.Path, r.CommonDir, r.FetchEndpointHash, r.PushEndpointHash, now = Clock.Now, intent = id }));
	void CompleteRegistration(string id, GitRepository r) {
		BindRegistration(id, r);
		rt.Db.Write(u => u.Execute("UPDATE git_registration_intents SET state='completed',updated_at=@now WHERE id=@id", new { id, now = Clock.Now }));
	}
	// Readback only: works with a retained unknown maintenance lease, never releases it or retries Git.
	public object ReconcileRegistration(string projectId, string actor, string operationId) {
		using var call = BeginCall(); CheckCancellation(); Actor(projectId, actor);
		var project = rt.Store.GetProject(projectId) ?? throw new DomainException("Project not found.");
		if(actor != "human" && actor != project.RootAgentId) throw new DomainException("Registration readback requires the project lead or human.");
		return rt.Db.Read(c => {
			var intent = c.QuerySingleOrDefault<GitRegistrationIntent>("SELECT * FROM git_registration_intents WHERE id=@operationId AND project_id=@projectId", new { operationId, projectId });
			if(intent is null) return (object)new { operation_id = operationId, state = "unknown", reason = "No native registration intent; historical invocation remains UNKNOWN. Absence is not evidence of non-execution." };
			var r = intent.RepositoryId is null ? null : c.QuerySingleOrDefault<GitRepository>("SELECT * FROM git_repositories WHERE id=@id AND project_id=@projectId", new { id = intent.RepositoryId, projectId });
			var exact = r is not null && r.Path == intent.CanonicalPath && r.CommonDir == intent.CommonDir && r.IntegrationBranch == intent.IntegrationBranch && r.OwnerId == intent.OwnerId && r.Remote == intent.Remote && r.RemoteBranch == intent.RemoteBranch && r.FetchEndpointHash == intent.FetchEndpointHash && r.PushEndpointHash == intent.PushEndpointHash;
			return new { operation_id = operationId, state = exact ? "completed_readback" : "unknown", intent, repository = exact ? r : null, limitation = "Read-only durable destination proof; no filesystem/Git replay or maintenance UNKNOWN release. Does not prove unrelated historical invocations." };
		});
	}
	public GitWorkspace Create(string projectId, string actor, string repoId, string objectiveId, string operationId) {
		using var call = BeginCall(); CheckCancellation();
		using var admission = Admit("git-create", projectId);
		var r = Repository(projectId, repoId);
		Actor(projectId, actor);
		var o = rt.Store.GetObjective(objectiveId) ?? throw new DomainException("Objective not found.");
		if(o.ProjectId != projectId || o.State is "complete" or "canceled" || actor != "human" && o.OwnerId != actor) throw new DomainException("Workspace requires the caller's active/planned assigned objective.");
		using var baton = Baton(r.CommonDir);
		var duplicate = Operation(operationId);
		if(duplicate is not null) {
			Match(duplicate, r, "create", actor, objectiveId);
			if(duplicate.State == "succeeded") return Workspace(projectId, duplicate.WorkspaceId!);
			throw new DomainException("Create outcome unknown; reconcile explicitly, never replay.");
		}
		NoPending(r);
		ValidateRepository(r); CleanTarget(r.Path, r.IntegrationBranch);
		var id = Ids.New("gws");
		var path = System.IO.Path.Combine(Canonical(rt.Options.Home), "git-workspaces", id);
		var branch = "ainur/work/" + id;
		if(Canonical(path) != path || !Contains(Canonical(rt.Options.Home), path)) throw new DomainException("Workspace storage symlink drift; no changes made.");
		var baseSha = Head(r.Path);
		var owner = actor == "human" ? "human" : actor;
		rt.Db.Write(u => {
			u.Execute("INSERT INTO git_workspaces(id,repository_id,project_id,objective_id,owner_id,path,branch,base_sha,state,publication_state,created_at,updated_at) VALUES(@id,@repoId,@projectId,@objectiveId,@owner,@path,@branch,@baseSha,'creating',@publication,@now,@now)", new { id, repoId, projectId, objectiveId, owner, path, branch, baseSha, publication = r.Remote is null ? "not_configured" : "not_due", now = Clock.Now });
			Intent(u, operationId, r, id, "create", actor, objectiveId, baseSha, baseSha);
		});
		try {
			Require(r.Path, "worktree", "add", "-b", branch, "--", path, baseSha);
			Finish(operationId, "succeeded", "Workspace materialized; retained.", id, "open");
		} catch { Finish(operationId, "unknown", "Git create outcome requires explicit reconciliation."); throw new DomainException("Workspace creation outcome unknown; do not retry. Reconcile. " + Current.Value?.InterruptedDiagnostic); }
		return Workspace(projectId, id);
	}

	void VerifyOwned(GitWorkspace w, GitRepository r, bool clean = true) {
		if(w.Retention == "removed" || !Directory.Exists(w.Path) || Canonical(w.Path) != w.Path ||
			Canonical(Require(w.Path, "rev-parse", "--path-format=absolute", "--git-common-dir")) != r.CommonDir || Require(w.Path, "symbolic-ref", "--short", "HEAD") != w.Branch ||
			!Git(w.Path, "merge-base", "--is-ancestor", w.BaseSha, Head(w.Path)).Ok)
			throw new DomainException("Workspace ownership/ref/base drift; no changes made.");
		if(clean) CleanTarget(w.Path, w.Branch);
	}
	public object Inspect(string projectId, string actor, string workspaceId) {
		using var call = BeginCall(); CheckCancellation();
		Actor(projectId, actor);
		var w = Workspace(projectId, workspaceId); var r = Repository(projectId, w.RepositoryId);
		using var baton = Baton(r.CommonDir);
		ValidateRepository(r); VerifyOwned(w, r, false);
		var head = Head(w.Path);
		return new { workspace = w, head, target = Head(r.Path), dirty = Require(w.Path, "status", "--porcelain=v1", "--untracked-files=all").Length != 0,
			paths = Require(w.Path, "diff", "--name-only", w.BaseSha, head, "--").Split('\n', StringSplitOptions.RemoveEmptyEntries),
			stat = Require(w.Path, "diff", "--stat", w.BaseSha, head, "--"), acceptance_valid = head == w.AcceptanceHead && w.AcceptanceBase == w.BaseSha,
			operations = rt.Db.Read(c => c.Query<GitOperation>("SELECT * FROM git_operations WHERE workspace_id=@workspaceId ORDER BY created_at", new { workspaceId }).AsList()) };
	}

	public GitWorkspace Submit(string projectId, string actor, string workspaceId, string head, string evidence, string reviewerId) {
		using var call = BeginCall(); CheckCancellation();
		using var admission = Admit("git-submit", projectId);
		var w = Workspace(projectId, workspaceId); Author(w, actor); var r = Repository(projectId, w.RepositoryId);
		using var baton = Baton(r.CommonDir); w = Workspace(projectId, workspaceId); NoPending(r); VerifyOwned(w, r);
		if(w.State is not ("open" or "review_pending" or "integration_owed")) throw new DomainException("Workspace is not authoring/reviewable.");
		if(head != Head(w.Path) || head == w.BaseSha || evidence.Length is 0 or > 16000) throw new DomainException("Submit exact changed head with bounded test/evidence references.");
		var reviewer = rt.Store.GetAgent(reviewerId);
		if(reviewer?.ProjectId != projectId || reviewerId == w.OwnerId || !AgentStates.IsLive(reviewer.State)) throw new DomainException("Review requires a distinct live agent in this project.");
		var created = rt.Db.Write(u => {
			var objective = rt.Store.GetObjective(u, w.ObjectiveId);
			if(objective?.ProjectId != projectId || objective.State == ObjectiveStates.Canceled) throw new DomainException("Review objective missing/canceled or outside this project.");
			if(NativeGitReviewPurpose.Prior(u, w, head, reviewerId) is { } prior) {
				if(prior.Status is not ("active" or "accepted") || !NativeGitReviewPurpose.Current(u, prior))
					throw new DomainException("Review action canceled/superseded; never revive old exact-purpose identity.");
				return false; // Exact duplicate: no wake, no hold release, no acceptance reset.
			}
			if(NativeGitReviewPurpose.UnknownHistory(u, w.Id)) throw new DomainException("Earlier review side effects UNKNOWN; cancellation/supersession cannot authorize fresh source identity.");
			var currentAction = u.Single<NativeGitReviewPurpose.Receipt>("SELECT action_notification_id,status FROM git_review_receipts WHERE workspace_id=@id AND status IN ('active','accepted')", new { id = w.Id });
			if(currentAction is not null && NativeGitReviewPurpose.Held(u, currentAction) || currentAction is null && NativeGitReviewPurpose.LegacyHeld(u, w)) throw new DomainException("Existing review deliberately held/UNKNOWN; a new source identity cannot bypass explicit reconciliation.");
			u.Execute("UPDATE git_workspaces SET state='review_pending',review_head=@head,evidence=@evidence,reviewer_id=@reviewerId,acceptance_head=NULL,acceptance_base=NULL,acceptance_actor=NULL,accepted_at=NULL,updated_at=@now WHERE id=@id", new { head, evidence, reviewerId, now = Clock.Now, id = w.Id });
			var action = NativeGitReviewPurpose.Create(rt, u, w, actor, head, reviewerId, evidence);
			u.Journal("git.review.requested", projectId, "workspace", w.Id, actor == "human" ? null : actor, new { head, w.BaseSha, reviewerId, action_notification_id = action.Id });
			return true;
		});
		if(created && rt.Generation > 0 && reviewer.State != AgentStates.Paused && (reviewer.PrimarySessionId is not { } sid || !rt.IsPaused(sid))) rt.Wake(reviewerId); // Existing pause-aware continuity dispatch; never ResumeAgent.
		return Workspace(projectId, w.Id);
	}

	public GitWorkspace Accept(string projectId, string actor, string workspaceId, string head, string baseSha) {
		using var call = BeginCall(); CheckCancellation();
		using var admission = Admit("git-accept", projectId);
		var w = Workspace(projectId, workspaceId); Actor(projectId, actor); var r = Repository(projectId, w.RepositoryId);
		using var baton = Baton(r.CommonDir); w = Workspace(projectId, workspaceId); NoPending(r); VerifyOwned(w, r);
		if(actor == w.OwnerId || actor != "human" && actor != w.ReviewerId) throw new DomainException("Only the named independent reviewer (or independent human) can accept.");
		var receipt = rt.Db.Read(c => c.QuerySingleOrDefault<NativeGitReviewPurpose.Receipt>("SELECT action_notification_id,status FROM git_review_receipts WHERE workspace_id=@id AND base_sha=@baseSha AND head_sha=@head AND reviewer_id=@reviewer", new { id = w.Id, baseSha, head, reviewer = w.ReviewerId }));
		if(receipt is null || !rt.Db.Write(u => NativeGitReviewPurpose.Current(u, receipt) && !NativeGitReviewPurpose.UnknownHistory(u, w.Id))) throw new DomainException("Exact current structured review purpose without historical UNKNOWN required; canceled/superseded/legacy action cannot be accepted.");
		if(w.State == "integration_owed" && receipt.Status == "accepted" && w.AcceptanceHead == head && w.AcceptanceBase == baseSha && w.AcceptanceActor == actor && Head(w.Path) == head) return w;
		if(receipt.Status != "active" || w.State != "review_pending" || head != w.ReviewHead || head != Head(w.Path) || baseSha != w.BaseSha) throw new DomainException("Stale review: exact current submitted head/base required.");
		if(rt.Db.Write(u => NativeGitReviewPurpose.Held(u, receipt))) throw new DomainException("Review action deliberately held or side effects UNKNOWN; explicitly reconcile/disposition before acceptance.");
		rt.Db.Write(u => {
			if(!NativeGitReviewPurpose.Current(u, receipt) || NativeGitReviewPurpose.Held(u, receipt) || NativeGitReviewPurpose.UnknownHistory(u, w.Id)) throw new DomainException("Review purpose changed/held or historical UNKNOWN; exact action no longer acceptable.");
			u.Execute("UPDATE git_review_receipts SET status='accepted',updated_at=@now WHERE action_notification_id=@id", new { id = receipt.ActionNotificationId, now = Clock.Now });
			u.Execute("UPDATE git_workspaces SET state='integration_owed',acceptance_head=@head,acceptance_base=@baseSha,acceptance_actor=@actor,accepted_at=@now,updated_at=@now WHERE id=@id", new { head, baseSha, actor, now = Clock.Now, id = w.Id });
			rt.Store.InsertNotification(u, new Notification { Id = Ids.New("ntf"), ProjectId = projectId, Type = NotificationTypes.Result,
				FromAgentId = actor == "human" ? null : actor, ToAgentId = r.OwnerId, ObjectiveId = w.ObjectiveId, Wakes = true, State = "pending", DedupeKey = $"git-accept:{w.Id}:{head}:{actor}",
				Body = $"Exact independent review accepted {w.Id} base {baseSha} head {head}. Local integration owed to {r.OwnerId}; use guarded integrate. Publication remains separate.", CreatedAt = Clock.Now });
			u.Journal("git.review.accepted", projectId, "workspace", w.Id, actor == "human" ? null : actor, new { head, baseSha, integration_owner = r.OwnerId });
		});
		if(rt.Generation > 0 && rt.Store.GetAgent(r.OwnerId) is { State: not AgentStates.Paused } owner && (owner.PrimarySessionId is not { } sid || !rt.IsPaused(sid))) rt.Wake(r.OwnerId);
		return Workspace(projectId, w.Id);
	}

	public GitWorkspace Integrate(string projectId, string actor, string workspaceId, string operationId) {
		using var call = BeginCall(); CheckCancellation();
		using var admission = Admit("git-integrate", projectId);
		var w = Workspace(projectId, workspaceId); var r = Repository(projectId, w.RepositoryId); Integrator(r, actor);
		using var baton = Baton(r.CommonDir);
		w = Workspace(projectId, workspaceId);
		var prior = Operation(operationId);
		if(prior is not null) {
			Match(prior, r, "integrate", actor, workspaceId);
			if(prior.State == "succeeded") return w;
			throw new DomainException("Integration outcome unknown; reconcile actual refs before further changes.");
		}
		NoPending(r); VerifyOwned(w, r); ValidateRepository(r); CleanTarget(r.Path, r.IntegrationBranch);
		var head = Head(w.Path); var target = Head(r.Path);
		if(w.State != "integration_owed" || head != w.AcceptanceHead || w.BaseSha != w.AcceptanceBase || w.AcceptanceActor == w.OwnerId) throw new DomainException("Independent exact acceptance missing/stale; request fresh review.");
		// Conservative drift: only exact reviewed base or already-that-head; no interleaving unreviewed target work.
		if(target != w.BaseSha && target != head) throw new DomainException("Integration target moved; reconcile/rebase and obtain fresh exact review. No merge attempted.");
		if(!Git(r.Path, "merge-base", "--is-ancestor", target, head).Ok) throw new DomainException("Not a fast-forward; no merge attempted.");
		rt.Db.Write(u => {
			// Atomic with the durable NEW operation intent. An already admitted/applied operation
			// is handled above/by readback; cancellation is not a filesystem rollback guarantee.
			NativeGitReviewPurpose.RequireAccepted(u, w, head);
			Intent(u, operationId, r, w.Id, "integrate", actor, w.Id, target, head);
		});
		try {
			if(target != head) Require(r.Path, "merge", "--ff-only", "--no-edit", "--no-overwrite-ignore", head);
			if(Head(r.Path) != head) throw new DomainException("Target changed during integration.");
			Integrated(w, r, operationId);
		} catch { Finish(operationId, "unknown", "Integration disposition requires actual-ref reconciliation."); throw new DomainException("Integration outcome unknown; no automatic replay. " + Current.Value?.InterruptedDiagnostic); }
		return Workspace(projectId, w.Id);
	}
	void Integrated(GitWorkspace w, GitRepository r, string op) => rt.Db.Write(u => {
		u.Execute("UPDATE git_workspaces SET state='integrated',integrated_head=@head,publication_state=@publication,updated_at=@now WHERE id=@id", new { head = w.AcceptanceHead, publication = r.Remote is null ? "not_configured" : "publication_owed", now = Clock.Now, id = w.Id });
		u.Execute("UPDATE git_operations SET state='succeeded',detail='Exact local fast-forward observed; retained author workspace.',updated_at=@now WHERE id=@op", new { now = Clock.Now, op });
		u.Journal("git.integration.completed", r.ProjectId, "workspace", w.Id, payload: new { head = w.AcceptanceHead, publication_owed = r.Remote is not null });
	});

	public GitWorkspace Reconcile(string projectId, string actor, string operationId) {
		using var call = BeginCall(); CheckCancellation();
		using var admission = Admit("git-reconcile", projectId);
		var op = Operation(operationId) ?? throw new DomainException("Operation not found.");
		var r = Repository(projectId, op.RepositoryId); Integrator(r, actor);
		using var baton = Baton(r.CommonDir);
		var w = Workspace(projectId, op.WorkspaceId!);
		op = Operation(operationId)!;
		if(op.State == "succeeded" || op.State == "not_applied") return w;
		ValidateRepository(r); CleanTarget(r.Path, r.IntegrationBranch);
		if(op.Action == "create") {
			if(!Directory.Exists(w.Path) && !Git(r.Path, "show-ref", "--verify", "refs/heads/" + w.Branch).Ok) {
				Finish(op.Id, "not_applied", "No worktree/ref exists; old intent retained. Explicit new request may create another workspace.", w.Id, "abandoned");
			} else {
				VerifyOwned(w, r); if(Head(w.Path) != w.BaseSha) throw new DomainException("Create state drift; manual disposition required.");
				Finish(op.Id, "succeeded", "Owned clean worktree and original base observed.", w.Id, "open");
			}
		} else if(op.Action == "integrate") {
			VerifyOwned(w, r);
			if(Head(r.Path) == op.IntendedSha && Head(w.Path) == op.IntendedSha && w.AcceptanceHead == op.IntendedSha) Integrated(w, r, op.Id);
			else if(Head(r.Path) == op.BeforeSha) Finish(op.Id, "not_applied", "Original clean target observed; no replay. New operation requires explicit request.");
			else throw new DomainException("Unknown refs changed; retain operation/workspace and seek human reconciliation.");
		} else throw new DomainException("Operation requires explicit publication readback or retained cleanup disposition; no replay.");
		return Workspace(projectId, w.Id);
	}

	public GitWorkspace Publish(string projectId, string actor, string workspaceId, string operationId) {
		using var call = BeginCall(); CheckCancellation();
		using var admission = Admit("git-publish", projectId);
		var w = Workspace(projectId, workspaceId); var r = Repository(projectId, w.RepositoryId); Integrator(r, actor);
		using var baton = Baton(r.CommonDir); w = Workspace(projectId, workspaceId);
		var prior = Operation(operationId);
		if(prior is not null) {
			Match(prior, r, "publish", actor, w.Id);
			BoundAuthority(r, prior);
			if(prior.State == "succeeded") return w;
			throw new DomainException("Publication outcome unknown; use explicit publication_readback, no automatic retry.");
		}
		NoPending(r); ValidateRepository(r); CleanTarget(r.Path, r.IntegrationBranch);
		if(r.Remote is null || r.RemoteBranch is null || w.State != "integrated" || w.IntegratedHead != Head(r.Path) || w.PublicationState != "publication_owed")
			throw new DomainException("Publication not configured/owed, or integration target changed. No push attempted.");
		BoundAuthority(r);
		rt.Db.Write(u => Intent(u, operationId, r, w.Id, "publish", actor, w.Id, Head(r.Path), w.IntegratedHead));
		try {
			var endpoint = BoundAuthority(r, Operation(operationId)!); // Point-of-use check; never resolve a mutable alias for the push.
			Require(r.Path, "push", "--no-verify", "--", endpoint.Push, w.IntegratedHead + ":refs/heads/" + r.RemoteBranch);
			PublicationObserved(r, w, operationId);
		} catch { Finish(operationId, "unknown", "Publication outcome unknown; explicit remote readback required."); throw new DomainException("Publication outcome unknown; no automatic push retry. " + Current.Value?.InterruptedDiagnostic); }
		return Workspace(projectId, w.Id);
	}
	public GitWorkspace PublicationReadback(string projectId, string actor, string operationId) {
		using var call = BeginCall(); CheckCancellation();
		using var admission = Admit("git-publicationreadback", projectId);
		var op = Operation(operationId) ?? throw new DomainException("Publication operation not found.");
		var r = Repository(projectId, op.RepositoryId); Integrator(r, actor);
		using var baton = Baton(r.CommonDir); op = Operation(operationId)!;
		ValidateRepository(r);
		if(op.Action != "publish") throw new DomainException("Not a publication operation.");
		var w = Workspace(projectId, op.WorkspaceId!);
		BoundAuthority(r, op);
		if(op.State != "succeeded") PublicationObserved(r, w, op.Id);
		return Workspace(projectId, w.Id);
	}
	void PublicationObserved(GitRepository r, GitWorkspace w, string op) {
		var operation = Operation(op)!;
		var intended = operation.IntendedSha;
		ValidateRepository(r);
		var endpoint = BoundAuthority(r, operation);
		var actual = Require(r.Path, "ls-remote", "--refs", "--", endpoint.Push, "refs/heads/" + operation.PublicationBranch).Split('\t')[0];
		BoundAuthority(r, operation); // Drift during the remote command is unknown, not accepted publication.
		if(actual != intended) throw new DomainException("Remote exact SHA not observed; publication remains owed/unknown, no retry.");
		rt.Db.Write(u => {
			u.Execute("UPDATE git_workspaces SET publication_state='published',updated_at=@now WHERE id=@id", new { now = Clock.Now, id = w.Id });
			u.Execute("UPDATE git_operations SET state='succeeded',detail='Exact configured remote SHA readback observed.',updated_at=@now WHERE id=@op", new { now = Clock.Now, op });
			u.Journal("git.publication.completed", r.ProjectId, "workspace", w.Id, payload: new { head = intended });
		});
	}

	public GitWorkspace Abandon(string projectId, string actor, string workspaceId) {
		using var call = BeginCall(); CheckCancellation();
		using var admission = Admit("git-abandon", projectId);
		var w = Workspace(projectId, workspaceId); Author(w, actor); var r = Repository(projectId, w.RepositoryId);
		using var baton = Baton(r.CommonDir); NoPending(r);
		if(w.State == "integrated") throw new DomainException("Integrated workspace remains retained; abandon is not history rollback.");
		rt.Db.Write(u => { NativeGitReviewPurpose.Close(u, w.Id, "canceled"); u.Execute("UPDATE git_workspaces SET state='abandoned',retention='retained',updated_at=@now WHERE id=@id", new { now = Clock.Now, id = w.Id }); u.Journal("git.workspace.abandoned", projectId, "workspace", w.Id); });
		return Workspace(projectId, w.Id); // No deletion, even if clean. Owned branches/workspaces intentionally retained in this slice.
	}

	public object Status(string projectId, string actor) {
		using var call = BeginCall(); CheckCancellation(); Actor(projectId, actor); return new { repositories = Repositories(projectId), registration_intents = rt.Db.Read(c => c.Query<GitRegistrationIntent>("SELECT * FROM git_registration_intents WHERE project_id=@projectId ORDER BY created_at", new { projectId }).AsList()), workspaces = Workspaces(projectId),
		operations = rt.Db.Read(c => c.Query<GitOperation>("SELECT o.* FROM git_operations o JOIN git_repositories r ON r.id=o.repository_id WHERE r.project_id=@projectId ORDER BY o.created_at", new { projectId }).AsList()) }; }

	sealed record PublicationAuthority(string Push, string FetchHash, string PushHash);
	static PublicationAuthority CaptureAuthority(string path, string remote) {
		// Explicit endpoints must not be re-resolved through mutable insteadOf/pushInsteadOf at command time.
		if(Git(path, "config", "--get-regexp", "^url\\..*\\.(insteadof|pushinsteadof)$").Ok)
			throw new DomainException("URL rewrite publication configurations are unsupported; manual authority resolution required.");
		static string Single(string output) {
			var lines = output.Split('\n');
			if(lines.Length != 1 || lines[0].Length is 0 or > 8192 || lines[0].Any(char.IsControl) || lines[0].StartsWith('-'))
				throw new DomainException("Publication requires one unambiguous safe fetch endpoint and one push endpoint.");
			return lines[0];
		}
		string SafeEndpoint(string endpoint) {
			if(System.IO.Path.IsPathRooted(endpoint)) return Canonical(endpoint);
			if(Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "ssh" or "file") {
				if(uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.Scheme == "file" && (uri.Host.Length != 0 && uri.Host != "localhost" || uri.UserInfo.Length != 0))
					throw new DomainException("Unsupported publication endpoint syntax; no endpoint values recorded.");
				return uri.Scheme == "file" ? Canonical(uri.LocalPath) : endpoint;
			}
			if(Regex.IsMatch(endpoint, "^[a-zA-Z0-9._-]+@[a-zA-Z0-9._-]+:[a-zA-Z0-9/._-]+$")) return endpoint;
			throw new DomainException("Unsupported publication endpoint syntax; use an absolute local path, file, HTTPS or SSH endpoint.");
		}
		var fetch = SafeEndpoint(Single(Require(path, "remote", "get-url", "--all", remote)));
		var push = SafeEndpoint(Single(Require(path, "remote", "get-url", "--push", "--all", remote)));
		static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
		return new(push, Hash(fetch), Hash(push)); // Credentials, if present, participate in exact authority but only hashes persist/return.
	}
	static PublicationAuthority BoundAuthority(GitRepository r, GitOperation? op = null) {
		if(r.Remote is null) throw new DomainException("Publication is not configured.");
		var endpoint = CaptureAuthority(r.Path, r.Remote);
		if(endpoint.FetchHash != r.FetchEndpointHash || endpoint.PushHash != r.PushEndpointHash || op is not null &&
			(op.FetchEndpointHash != endpoint.FetchHash || op.PushEndpointHash != endpoint.PushHash || op.PublicationBranch != r.RemoteBranch))
			throw new DomainException("Publication authority drift or missing exact operation binding; retain obligation and resolve manually. No push/readback replay to a new endpoint.");
		return endpoint;
	}

	sealed record GitResult(bool Ok, string Text);
	static GitResult Git(string directory, params string[] args) {
		CheckCancellation();
		var scope = Current.Value ?? throw new InvalidOperationException("Native Git call scope missing.");
		var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
		foreach(var arg in new[] { "-c", "core.hooksPath=/dev/null", "-c", "core.fsmonitor=false" }.Concat(args)) start.ArgumentList.Add(arg);
		foreach(var name in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_NAMESPACE", "GIT_CONFIG", "GIT_CONFIG_COUNT", "GIT_CONFIG_PARAMETERS" }) start.Environment.Remove(name);
		start.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
		start.Environment["GIT_TERMINAL_PROMPT"] = "0"; start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
		try {
			scope.ProcessAttempt();
			var result = NativeGitProcess.Run(scope.ProcessStartForTests?.Invoke(start) ?? start, args[0], scope.Deadline.Token, scope.ProcessTimeout);
			return new(result.Ok, result.Text);
		} catch(NativeGitProcess.Interrupted ex) {
			// Even observed root exit/closed pipes cannot certify admitted mutation effects.
			scope.InterruptedDiagnostic = ex.Message; scope.Lease?.MarkUnknown(); throw new DomainException(ex.Message);
		} catch(OperationCanceledException) { scope.Lease?.MarkUnknown(); throw new DomainException("Native Git cancelled at process-start boundary; inspect admitted effects, no replay."); }
	}
	static string Require(string path, params string[] args) { var result = Git(path, args); return result.Ok ? result.Text : throw new DomainException("Git precondition/command failed; no raw command output recorded."); }
	static string Head(string path) => Require(path, "rev-parse", "--verify", "HEAD");
	static void Clean(string path) { if(Require(path, "status", "--porcelain=v1", "--untracked-files=all").Length != 0) throw new DomainException("Dirty tracked/untracked files; preserve them and resolve manually."); }
	static void CleanTarget(string path, string branch) {
		if(Canonical(Require(path, "rev-parse", "--show-toplevel")) != path || Require(path, "symbolic-ref", "--short", "HEAD") != branch) throw new DomainException("Integration checkout moved/detached; no changes made.");
		foreach(var marker in new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "rebase-merge", "rebase-apply" }) {
			var location = Require(path, "rev-parse", "--path-format=absolute", "--git-path", marker);
			if(File.Exists(location) || Directory.Exists(location)) throw new DomainException("Checkout has an in-progress Git operation; retain and reconcile manually.");
		}
		Clean(path);
	}
	void ValidateRepository(GitRepository r) {
		if(Canonical(r.Path) != r.Path || rt.Store.GetProject(r.ProjectId)?.WorkspacePath is not { } configured || Canonical(configured) != r.Path ||
			Canonical(Require(r.Path, "rev-parse", "--path-format=absolute", "--git-common-dir")) != r.CommonDir)
			throw new DomainException("Registered checkout/common-directory/project-workspace drift; no changes made.");
	}
	static FileStream Baton(string commonDir) {
		try { return new FileStream(System.IO.Path.Combine(commonDir, "ainur-integration.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
		catch(IOException) { throw new DomainException("Repository integration baton busy; no operation started."); }
	}
	public sealed class GitOperation {
		public string Id { get; set; } = "";
		public string RepositoryId { get; set; } = "";
		public string? WorkspaceId { get; set; }
		public string Action { get; set; } = "";
		public string Fingerprint { get; set; } = "";
		public string Actor { get; set; } = "";
		public string? BeforeSha { get; set; }
		public string? IntendedSha { get; set; }
		public string? FetchEndpointHash { get; set; }
		public string? PushEndpointHash { get; set; }
		public string? PublicationBranch { get; set; }
		public string State { get; set; } = "";
		public string Detail { get; set; } = "";
	}
	GitOperation? Operation(string id) => rt.Db.Read(c => c.QuerySingleOrDefault<GitOperation>("SELECT * FROM git_operations WHERE id=@id", new { id }));
	static void Match(GitOperation op, GitRepository r, string action, string actor, string fingerprint) {
		if(op.RepositoryId != r.Id || op.Action != action || op.Actor != actor || op.Fingerprint != fingerprint) throw new DomainException("Operation id reused for different intent.");
	}
	void NoPending(GitRepository r) {
		if(rt.Db.Read(c => c.ExecuteScalar<long>("SELECT COUNT(*) FROM git_operations WHERE repository_id=@id AND state IN ('intent','unknown')", new { id = r.Id })) > 0)
			throw new DomainException("Repository has an unknown operation; explicit reconciliation required before mutations.");
	}
	static void Intent(Ainur.Core.Persistence.Db.Unit u, string id, GitRepository r, string workspace, string action, string actor, string fingerprint, string? before, string? intended) {
		if(!Regex.IsMatch(id, "^[a-zA-Z0-9_-]{1,120}$")) throw new DomainException("Explicit bounded operation_id required.");
		u.Execute("INSERT INTO git_operations(id,repository_id,workspace_id,action,fingerprint,actor,before_sha,intended_sha,fetch_endpoint_hash,push_endpoint_hash,publication_branch,state,created_at,updated_at) VALUES(@id,@repo,@workspace,@action,@fingerprint,@actor,@before,@intended,@fetchHash,@pushHash,@publicationBranch,'intent',@now,@now)", new { id, repo = r.Id, workspace, action, fingerprint, actor, before, intended, fetchHash = action == "publish" ? r.FetchEndpointHash : null, pushHash = action == "publish" ? r.PushEndpointHash : null, publicationBranch = action == "publish" ? r.RemoteBranch : null, now = Clock.Now });
		u.Journal("git.operation.intent", r.ProjectId, "operation", id, actor == "human" ? null : actor, new { workspace, action, before, intended });
	}
	void Finish(string id, string state, string detail, string? workspace = null, string? workspaceState = null) => rt.Db.Write(u => {
		u.Execute("UPDATE git_operations SET state=@state,detail=@detail,updated_at=@now WHERE id=@id", new { state, detail, now = Clock.Now, id });
		if(workspace is not null) u.Execute("UPDATE git_workspaces SET state=@workspaceState,updated_at=@now WHERE id=@workspace", new { workspaceState, now = Clock.Now, workspace });
		u.Journal("git.operation.disposition", null, "operation", id, payload: new { state, detail });
	});
}
