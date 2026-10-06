using System.Diagnostics;
using Dapper;
using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Ainur.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;
using Ainur.Core;
using Ainur.Core.Model;
using Ainur.Core.Persistence;
using Ainur.Core.Runtime;
using Ainur.Core.Tools;
using Xunit;

namespace Ainur.Tests;

public class NativeGitTests {
	sealed class Fixture : IDisposable {
		public readonly TempHome Home = new();
		public AinurRuntime Runtime;
		public NativeGit Git => new(Runtime);
		public Project Project;
		public Agent Author;
		public Agent Reviewer;
		public Objective Objective;
		public GitRepository Repository;
		public Fixture(bool remote = false) {
			Run(Home.Workspace, "init", "-b", "main");
			Run(Home.Workspace, "config", "user.name", "Offline fixture");
			Run(Home.Workspace, "config", "user.email", "offline@example.invalid");
			Commit(Home.Workspace, "initial.txt", "base");
			if(remote) {
				var bare = Path.Combine(Home.Path, "remote.git"); Directory.CreateDirectory(bare);
				Run(bare, "init", "--bare"); Run(Home.Workspace, "remote", "add", "fixture", bare);
			}
			Runtime = Home.Runtime(new FakeProvider((_, _) => throw new Exception("No model allowed")), start: false);
			Project = Runtime.CreateProject("Native Git fixture", "offline", Home.Workspace, 10m);
			Author = Runtime.CreateAgent(Project.Id, new NewAgent { Name = "Author", Title = "implementer", ManagerId = Project.RootAgentId }, Project.RootAgentId);
			Reviewer = Runtime.CreateAgent(Project.Id, new NewAgent { Name = "Reviewer", Title = "reviewer", ManagerId = Project.RootAgentId }, Project.RootAgentId);
			Objective = AddObjective(Runtime, Project.Id, Author.Id);
			Repository = Git.Register(Project.Id, Project.RootAgentId!, Home.Workspace, "main", Project.RootAgentId!, remote ? "fixture" : null, remote ? "main" : null, operationId: Guid.NewGuid().ToString("N"));
		}
		public GitWorkspace Workspace(string operation = "create-one", string? author = null, string? objective = null) => Git.Create(Project.Id, author ?? Author.Id, Repository.Id, objective ?? Objective.Id, operation);
		public GitWorkspace Review(GitWorkspace w) {
			Commit(w.Path, "change.txt", "pixel-independent ordinary change");
			var head = Run(w.Path, "rev-parse", "HEAD");
			Git.Submit(Project.Id, w.OwnerId, w.Id, head, "fixture: git diff --check; offline tests passed", Reviewer.Id);
			return Git.Accept(Project.Id, Reviewer.Id, w.Id, head, w.BaseSha);
		}
		public void Restart() { Runtime.Dispose(); Runtime = Home.Runtime(new FakeProvider((_, _) => throw new Exception("No model allowed")), start: false); }
		public void Dispose() { Runtime.Dispose(); Home.Dispose(); }
	}
	static Objective AddObjective(AinurRuntime rt, string project, string owner) {
		var o = new Objective { Id = Ids.New("obj"), ProjectId = project, OwnerId = owner, Title = "Small assigned change", State = "active", CreatedAt = Clock.Now, UpdatedAt = Clock.Now };
		rt.Db.Write(u => rt.Store.InsertObjective(u, o, owner)); return o;
	}
	static string Run(string path, params string[] args) {
		var start = new ProcessStartInfo("git") { WorkingDirectory = path, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
		foreach(var arg in new[] { "-c", "core.hooksPath=/dev/null" }.Concat(args)) start.ArgumentList.Add(arg);
		var result = NativeGitProcess.Run(start, "fixture", default, TimeSpan.FromSeconds(10));
		Assert.True(result.Ok, "Fixture Git command failed; no replay."); return result.Text.Trim();
	}
	static void Commit(string path, string name, string text) { File.WriteAllText(Path.Combine(path, name), text); Run(path, "add", "--", name); Run(path, "commit", "-m", "fixture change"); }

	static ProcessStartInfo Shell(string script) {
		var start = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
		start.ArgumentList.Add("-c"); start.ArgumentList.Add(script); return start;
	}

	[Fact]
	public void Hanging_process_and_descendant_held_pipes_are_bounded_and_do_not_return_secret_diagnostics() {
		var watch = Stopwatch.StartNew();
		var hang = Assert.Throws<NativeGitProcess.Interrupted>(() => NativeGitProcess.Run(
			Shell("echo secret-diagnostic >&2; exec sleep 10"), "rev-parse", default, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100)));
		Assert.Contains("phase rev-parse", hang.Message); Assert.DoesNotContain("secret", hang.Message); Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
		watch.Restart();
		var pipe = Assert.Throws<NativeGitProcess.Interrupted>(() => NativeGitProcess.Run(
			Shell("sleep 1 & exit 0"), "status", default, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100)));
		Assert.Contains("root_exit_confirmed=True", pipe.Message); Assert.Contains("pipes_closed=False", pipe.Message);
		Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task Registration_cancellation_retains_intent_and_unknown_lease_after_dispose_and_restart() {
		using var f = new Fixture(); using var cancel = new CancellationTokenSource();
		var calls = 0;
		var native = new NativeGit(f.Runtime, cancel.Token) { ProcessStartForTests = _ => { calls++; cancel.CancelAfter(50); return Shell("exec sleep 10"); } };
		Assert.Throws<DomainException>(() => native.Register(f.Project.Id, f.Project.RootAgentId!, f.Home.Workspace, "main", f.Project.RootAgentId!, operationId: "cancel-intent"));
		Assert.Equal(1, calls);
		Assert.Equal("unknown", f.Runtime.Db.Read(c => c.ExecuteScalar<string>("SELECT state FROM git_registration_intents WHERE id='cancel-intent'")));
		Assert.Equal(1, f.Runtime.Db.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM maintenance_activities WHERE state='unknown'")));
		Assert.Throws<DomainException>(() => f.Git.Create(f.Project.Id, f.Author.Id, f.Repository.Id, f.Objective.Id, "blocked-after-cancel"));
		Assert.Contains("unknown", JsonSerializer.Serialize(f.Git.ReconcileRegistration(f.Project.Id, f.Project.RootAgentId!, "cancel-intent")));
		Assert.False((await f.Runtime.DrainWithStatusAsync(TimeSpan.Zero)).Drained);
		f.Restart();
		Assert.False((await f.Runtime.DrainWithStatusAsync(TimeSpan.Zero)).Drained);
		var status = f.Runtime.Maintenance.Begin("inspect unknown", TimeSpan.FromMilliseconds(1));
		Assert.Contains(status.Blockers, b => b.Kind == "git_mutation" && b.Id == "git-register" && b.State == "unknown");
		Assert.False(status.VerifiedQuiescent);
		Assert.Throws<DomainException>(() => f.Runtime.Maintenance.PrepareHandoff(status.Operation!.Id));
		Assert.Equal("unknown", f.Runtime.Db.Read(c => c.ExecuteScalar<string>("SELECT state FROM git_registration_intents WHERE id='cancel-intent'")));
	}

	[Fact]
	public void Successful_process_drains_both_streams_and_caps_retained_output() {
		var result = NativeGitProcess.Run(Shell("head -c 50000 /dev/zero | tr '\\000' x; head -c 50000 /dev/zero >&2"), "fixture", default, TimeSpan.FromSeconds(3));
		Assert.True(result.Ok); Assert.Equal(24000, result.Text.Length);
	}

	[Fact]
	public void Restart_retains_running_registration_as_unknown_without_destination_or_execution() {
		using var f = new Fixture();
		f.Runtime.Db.Write(u => u.Execute("INSERT INTO git_registration_intents(id,project_id,actor_id,requested_path,integration_branch,owner_id,state,created_at,updated_at) VALUES('crashed-before-binding',@project,@actor,@path,'main',@actor,'running',1,1)",
			new { project = f.Project.Id, actor = f.Project.RootAgentId!, path = f.Home.Workspace }));
		f.Restart(); f.Runtime.Start("native-registration-recovery-test");
		Assert.Equal("unknown", f.Runtime.Db.Read(c => c.ExecuteScalar<string>("SELECT state FROM git_registration_intents WHERE id='crashed-before-binding'")));
		var calls = 0;
		var native = new NativeGit(f.Runtime) { ProcessStartForTests = _ => { calls++; return Shell("exit 1"); } };
		Assert.Contains("unknown", JsonSerializer.Serialize(native.ReconcileRegistration(f.Project.Id, f.Project.RootAgentId!, "crashed-before-binding")));
		Assert.Equal(0, calls);
		Assert.Throws<DomainException>(() => native.Register(f.Project.Id, f.Project.RootAgentId!, f.Home.Workspace, "main", f.Project.RootAgentId!, operationId: "crashed-before-binding"));
		Assert.Equal(0, calls);
	}

	[Theory]
	[InlineData("running", "absent")]
	[InlineData("running", "candidate")]
	[InlineData("running", "mismatch")]
	[InlineData("running", "destination")]
	[InlineData("unknown", "absent")]
	[InlineData("unknown", "candidate")]
	[InlineData("unknown", "mismatch")]
	[InlineData("unknown", "destination")]
	public async Task Orphan_registration_intent_fences_fresh_ids_all_path_spellings_and_mutations_without_a_lease(string state, string binding) {
		using var f = new Fixture(); var w = f.Review(f.Workspace());
		var repositoryId = binding == "destination" ? f.Repository.Id : binding == "absent" ? null : "repo-orphan-candidate";
		var canonical = binding == "absent" ? null : binding == "mismatch" ? f.Home.Workspace + "-mismatch" : f.Repository.Path;
		f.Runtime.Db.Write(u => u.Execute("""
			INSERT INTO git_registration_intents(id,project_id,actor_id,requested_path,integration_branch,owner_id,state,repository_id,canonical_path,common_dir,created_at,updated_at)
			VALUES('orphan',@project,@actor,@path,'main',@actor,@state,@repositoryId,@canonical,@common,1,1)
			""", new { project = f.Project.Id, actor = f.Project.RootAgentId!, path = f.Home.Workspace, state, repositoryId, canonical,
				common = binding == "absent" ? null : f.Repository.CommonDir }));
		Assert.Equal(0, f.Runtime.Db.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM maintenance_activities WHERE kind='git_mutation' AND state IN ('running','unknown')")));
		var calls = 0;
		NativeGit Spy() => new(f.Runtime) { ProcessStartForTests = _ => { calls++; return Shell("exit 1"); } };
		void RejectFreshRequests() {
			var before = f.Runtime.Db.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM git_registration_intents"));
			foreach(var path in new[] { f.Home.Workspace, Path.Combine(f.Home.Workspace, "."), Path.Combine(f.Home.Workspace, "..", Path.GetFileName(f.Home.Workspace)) }) {
				var error = Assert.Throws<DomainException>(() => Spy().Register(f.Project.Id, f.Project.RootAgentId!, path, "main", f.Project.RootAgentId!, operationId: Guid.NewGuid().ToString("N")));
				Assert.Contains("Unresolved native registration intent", error.Message);
			}
			Assert.Throws<DomainException>(() => Spy().Create(f.Project.Id, f.Author.Id, f.Repository.Id, f.Objective.Id, "orphan-fresh-create"));
			Assert.Throws<DomainException>(() => Spy().Integrate(f.Project.Id, f.Project.RootAgentId!, w.Id, "orphan-fresh-integrate"));
			Assert.Throws<DomainException>(() => Spy().Submit(f.Project.Id, f.Author.Id, w.Id, w.AcceptanceHead!, "tests", f.Reviewer.Id));
			Assert.Throws<DomainException>(() => Spy().Accept(f.Project.Id, f.Reviewer.Id, w.Id, w.AcceptanceHead!, w.BaseSha));
			Assert.Throws<DomainException>(() => Spy().Abandon(f.Project.Id, f.Author.Id, w.Id));
			Assert.Throws<DomainException>(() => Spy().Publish(f.Project.Id, f.Project.RootAgentId!, w.Id, "orphan-fresh-publish"));
			Assert.Throws<DomainException>(() => Spy().Reconcile(f.Project.Id, f.Project.RootAgentId!, "orphan-fresh-integrate"));
			Assert.Throws<DomainException>(() => Spy().PublicationReadback(f.Project.Id, f.Project.RootAgentId!, "orphan-fresh-publish"));
			Assert.Equal(0, calls); Assert.Equal(before, f.Runtime.Db.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM git_registration_intents")));
			Assert.Equal(0, f.Runtime.Db.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM maintenance_activities WHERE kind='git_mutation' AND state IN ('running','unknown')")));
		}
		RejectFreshRequests(); // running uncertainty fences admission even before startup converts it.
		f.Restart(); f.Runtime.Start("orphan-native-registration-recovery"); RejectFreshRequests();
		Assert.Equal("unknown", f.Runtime.Db.Read(c => c.ExecuteScalar<string>("SELECT state FROM git_registration_intents WHERE id='orphan'")));
		var readback = JsonSerializer.Serialize(Spy().ReconcileRegistration(f.Project.Id, f.Project.RootAgentId!, "orphan"));
		Assert.Equal(binding == "destination", readback.Contains("completed_readback"));
		RejectFreshRequests(); // Exact destination proof is not an admission-fence disposition.
		var drain = await f.Runtime.DrainWithStatusAsync(TimeSpan.Zero);
		Assert.False(drain.Drained); Assert.Contains("git-registration:orphan", drain.Running);
		var maintenance = f.Runtime.Maintenance.Begin("orphan intent audit", TimeSpan.FromMilliseconds(1));
		Assert.Contains(maintenance.Blockers, b => b.Kind == "git_registration_intent" && b.Id == "orphan" && b.State == "unknown");
		Assert.False(maintenance.VerifiedQuiescent);
		Assert.Throws<DomainException>(() => f.Runtime.Maintenance.PrepareHandoff(maintenance.Operation!.Id));
	}

	[Fact]
	public void Post_binding_completion_exception_retains_intent_and_lease_and_blocks_new_ids_even_after_exact_readback() {
		using var f = new Fixture();
		f.Runtime.Db.Write(u => u.Execute("""
			CREATE TRIGGER fail_registration_completion BEFORE UPDATE OF state ON git_registration_intents
			WHEN OLD.id='postbind' AND NEW.state='completed' BEGIN SELECT RAISE(ABORT,'synthetic post-binding completion failure'); END;
			"""));
		var destination = Path.Combine(f.Home.Workspace, "second-native-repository"); Directory.CreateDirectory(destination);
		Run(destination, "init", "-b", "main"); Run(destination, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "--allow-empty", "-m", "fixture");
		f.Runtime.Db.Write(u => u.Execute("UPDATE projects SET workspace_path=@destination WHERE id=@project", new { destination, project = f.Project.Id }));
		Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => f.Git.Register(f.Project.Id, f.Project.RootAgentId!, destination, "main", f.Project.RootAgentId!, operationId: "postbind"));
		Assert.Equal("unknown", f.Runtime.Db.Read(c => c.ExecuteScalar<string>("SELECT state FROM git_registration_intents WHERE id='postbind'")));
		var destinationId = f.Runtime.Db.Read(c => c.ExecuteScalar<string>("SELECT repository_id FROM git_registration_intents WHERE id='postbind'"));
		Assert.NotEqual(f.Repository.Id, destinationId);
		Assert.Equal(destination, f.Runtime.Db.Read(c => c.ExecuteScalar<string>("SELECT path FROM git_repositories WHERE id=@destinationId", new { destinationId })));
		Assert.Equal(1, f.Runtime.Db.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM maintenance_activities WHERE state='unknown'")));
		Assert.Contains("completed_readback", JsonSerializer.Serialize(f.Git.ReconcileRegistration(f.Project.Id, f.Project.RootAgentId!, "postbind")));
		var calls = 0; var spy = new NativeGit(f.Runtime) { ProcessStartForTests = _ => { calls++; return Shell("exit 1"); } };
		Assert.Throws<DomainException>(() => spy.Register(f.Project.Id, f.Project.RootAgentId!, destination, "main", f.Project.RootAgentId!, operationId: "fresh-after-postbind"));
		Assert.Equal(0, calls); Assert.Equal(0, f.Runtime.Db.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM git_registration_intents WHERE id='fresh-after-postbind'")));
	}

	[Fact]
	public void Validation_failure_after_intent_is_conservatively_unresolved_not_inferred_safe_from_absent_binding() {
		using var f = new Fixture(); var calls = 0;
		var spy = new NativeGit(f.Runtime) { ProcessStartForTests = _ => { calls++; return Shell("exit 1"); } };
		Assert.Throws<DomainException>(() => spy.Register(f.Project.Id, f.Project.RootAgentId!, f.Home.Path, "main", f.Project.RootAgentId!, operationId: "validation-no-binding"));
		Assert.Equal(0, calls); Assert.Equal("unknown", f.Runtime.Db.Read(c => c.ExecuteScalar<string>("SELECT state FROM git_registration_intents WHERE id='validation-no-binding'")));
		Assert.Null(f.Runtime.Db.Read(c => c.ExecuteScalar<string?>("SELECT repository_id FROM git_registration_intents WHERE id='validation-no-binding'")));
		Assert.Equal(1, f.Runtime.Db.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM maintenance_activities WHERE state='unknown'")));
		Assert.Throws<DomainException>(() => spy.Register(f.Project.Id, f.Project.RootAgentId!, f.Home.Workspace, "main", f.Project.RootAgentId!, operationId: "fresh-after-validation"));
		Assert.Equal(0, calls); Assert.Equal(0, f.Runtime.Db.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM git_registration_intents WHERE id='fresh-after-validation'")));
	}

	[Fact]
	public void Admission_rechecks_orphan_intents_after_transient_lease_before_inserting_new_intent() {
		using var f = new Fixture(); var calls = 0;
		// Synthetic atomic insertion after initial admission check, before registration insertion recheck.
		f.Runtime.Db.Write(u => u.Execute($"""
			CREATE TRIGGER inject_orphan_after_lease AFTER INSERT ON maintenance_activities
			WHEN NEW.kind='git_mutation' AND NEW.reference_id='git-register' AND NEW.state='running'
			BEGIN
				INSERT INTO git_registration_intents(id,project_id,actor_id,requested_path,integration_branch,owner_id,state,created_at,updated_at)
				SELECT 'concurrent-orphan',id,root_agent_id,workspace_path,'main',root_agent_id,'unknown',1,1 FROM projects WHERE id='{f.Project.Id}';
			END;
			"""));
		var spy = new NativeGit(f.Runtime) { ProcessStartForTests = _ => { calls++; return Shell("exit 1"); } };
		Assert.Throws<DomainException>(() => spy.Register(f.Project.Id, f.Project.RootAgentId!, f.Home.Workspace, "main", f.Project.RootAgentId!, operationId: "fresh-racing-orphan"));
		Assert.Equal(0, calls);
		Assert.Equal(0, f.Runtime.Db.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM git_registration_intents WHERE id='fresh-racing-orphan'")));
		Assert.Equal("unknown", f.Runtime.Db.Read(c => c.ExecuteScalar<string>("SELECT state FROM git_registration_intents WHERE id='concurrent-orphan'")));
	}

	[Fact]
	public void Completed_registration_and_rejection_before_intent_do_not_create_an_uncertainty_fence() {
		using var f = new Fixture();
		Assert.Throws<DomainException>(() => f.Git.Register(f.Project.Id, f.Project.RootAgentId!, f.Home.Workspace, "main", f.Project.RootAgentId!, operationId: "bad operation identity"));
		Assert.Equal(0, f.Runtime.Db.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM git_registration_intents WHERE state IN ('running','unknown')")));
		var result = f.Git.Register(f.Project.Id, f.Project.RootAgentId!, Path.Combine(f.Home.Workspace, "."), "main", f.Project.RootAgentId!, operationId: "fresh-after-completed");
		Assert.Equal(f.Repository.Id, result.Id);
		Assert.Equal("completed", f.Runtime.Db.Read(c => c.ExecuteScalar<string>("SELECT state FROM git_registration_intents WHERE id='fresh-after-completed'")));
		Assert.Equal("open", f.Workspace("create-after-completed-register").State);
	}

	[Fact]
	public void Intent_only_fence_is_project_scoped_and_read_only_observation_retains_authority_and_state() {
		using var f = new Fixture();
		var destination = Path.Combine(f.Home.Path, "other-project-native"); Directory.CreateDirectory(destination);
		var other = f.Runtime.CreateProject("other native scope", "fixture", destination);
		f.Runtime.Db.Write(u => u.Execute("INSERT INTO git_registration_intents(id,project_id,actor_id,requested_path,integration_branch,owner_id,state,created_at,updated_at) VALUES('scope-orphan',@project,@actor,@path,'main',@actor,'unknown',1,1)",
			new { project = f.Project.Id, actor = f.Project.RootAgentId!, path = f.Home.Workspace }));
		string IntentSnapshot() => JsonSerializer.Serialize(f.Runtime.Db.Read(c => c.Query<GitRegistrationIntent>("SELECT * FROM git_registration_intents ORDER BY id").AsList()));
		var before = IntentSnapshot();
		Assert.Contains("scope-orphan", JsonSerializer.Serialize(f.Git.Status(f.Project.Id, f.Project.RootAgentId!)));
		Assert.Contains("scope-orphan", JsonSerializer.Serialize(f.Git.Status(f.Project.Id, f.Author.Id)));
		Assert.Contains("unknown", JsonSerializer.Serialize(f.Git.ReconcileRegistration(f.Project.Id, f.Project.RootAgentId!, "scope-orphan")));
		Assert.DoesNotContain("scope-orphan", JsonSerializer.Serialize(f.Git.Status(other.Id, other.RootAgentId!)));
		Assert.Throws<DomainException>(() => f.Git.Status(f.Project.Id, other.RootAgentId!));
		Assert.Throws<DomainException>(() => f.Git.ReconcileRegistration(f.Project.Id, other.RootAgentId!, "scope-orphan"));
		Assert.Throws<DomainException>(() => f.Git.ReconcileRegistration(f.Project.Id, f.Author.Id, "scope-orphan"));
		Assert.Equal(before, IntentSnapshot());
		Run(destination, "init", "-b", "main"); Run(destination, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "--allow-empty", "-m", "fixture");
		var registered = f.Git.Register(other.Id, other.RootAgentId!, destination, "main", other.RootAgentId!, operationId: "other-project-register");
		Assert.Equal(other.Id, registered.ProjectId);
		Assert.Equal("completed", f.Runtime.Db.Read(c => c.ExecuteScalar<string>("SELECT state FROM git_registration_intents WHERE id='other-project-register'")));
		Assert.Equal("unknown", f.Runtime.Db.Read(c => c.ExecuteScalar<string>("SELECT state FROM git_registration_intents WHERE id='scope-orphan'")));
		Assert.Throws<DomainException>(() => f.Git.Register(f.Project.Id, f.Project.RootAgentId!, f.Home.Workspace, "main", f.Project.RootAgentId!, operationId: "scope-bypass"));
		Assert.Equal(0, f.Runtime.Db.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM git_registration_intents WHERE id='scope-bypass'")));
	}

	[Fact]
	public void Total_call_budget_covers_multiple_subprocesses_without_replay() {
		using var f = new Fixture(); var calls = 0;
		var native = new NativeGit(f.Runtime) { CallTimeout = TimeSpan.FromMilliseconds(180), ProcessTimeout = TimeSpan.FromSeconds(5), ProcessStartForTests = original => {
			calls++; var delayed = Shell("sleep .12; exec git \"$@\""); delayed.WorkingDirectory = original.WorkingDirectory;
			delayed.ArgumentList.Add("native-test"); foreach(var arg in original.ArgumentList) delayed.ArgumentList.Add(arg); return delayed;
		} };
		Assert.Throws<DomainException>(() => native.Register(f.Project.Id, f.Project.RootAgentId!, f.Home.Workspace, "main", f.Project.RootAgentId!, operationId: "overall-intent"));
		Assert.InRange(calls, 1, 2);
		Assert.Equal("unknown", f.Runtime.Db.Read(c => c.ExecuteScalar<string>("SELECT state FROM git_registration_intents WHERE id='overall-intent'")));
	}

	[Fact]
	public void Registration_readback_requires_exact_destination_and_never_terminalizes_historical_absence() {
		using var f = new Fixture();
		var registered = f.Git.Register(f.Project.Id, f.Project.RootAgentId!, f.Home.Workspace, "main", f.Project.RootAgentId!, operationId: "register-readback");
		Assert.Equal(f.Repository.Id, registered.Id);
		// Simulate lost acknowledgement after durable destination bind, not a re-execution.
		f.Runtime.Db.Write(u => u.Execute("UPDATE git_registration_intents SET state='unknown' WHERE id='register-readback'"));
		f.Restart();
		Assert.Contains("completed_readback", JsonSerializer.Serialize(f.Git.ReconcileRegistration(f.Project.Id, f.Project.RootAgentId!, "register-readback")));
		Assert.Equal("unknown", f.Runtime.Db.Read(c => c.ExecuteScalar<string>("SELECT state FROM git_registration_intents WHERE id='register-readback'")));
		Assert.Throws<DomainException>(() => f.Git.Register(f.Project.Id, f.Project.RootAgentId!, f.Home.Workspace, "main", f.Project.RootAgentId!, operationId: "register-readback"));
		f.Runtime.Db.Write(u => u.Execute("UPDATE git_registration_intents SET owner_id='mismatch' WHERE id='register-readback'"));
		Assert.DoesNotContain("completed_readback", JsonSerializer.Serialize(f.Git.ReconcileRegistration(f.Project.Id, f.Project.RootAgentId!, "register-readback")));
		Assert.Contains("historical invocation remains UNKNOWN", JsonSerializer.Serialize(f.Git.ReconcileRegistration(f.Project.Id, f.Project.RootAgentId!, "inv_historical_fixture")));
		Assert.Throws<DomainException>(() => f.Git.ReconcileRegistration(f.Project.Id, f.Author.Id, "register-readback"));
	}

	[Fact]
	public void Missing_identity_and_pre_cancelled_call_have_no_registration_effects() {
		using var f = new Fixture(); var before = f.Runtime.Db.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM git_registration_intents"));
		Assert.Throws<DomainException>(() => f.Git.Register(f.Project.Id, f.Project.RootAgentId!, f.Home.Workspace, "main", f.Project.RootAgentId!));
		using var cancel = new CancellationTokenSource(); cancel.Cancel(); var calls = 0;
		var native = new NativeGit(f.Runtime, cancel.Token) { ProcessStartForTests = _ => { calls++; return Shell("exit 1"); } };
		Assert.Throws<DomainException>(() => native.Register(f.Project.Id, f.Project.RootAgentId!, f.Home.Workspace, "main", f.Project.RootAgentId!, operationId: "pre-cancelled"));
		Assert.Equal(0, calls); Assert.Equal(before, f.Runtime.Db.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM git_registration_intents")));
	}

	[Fact]
	public async Task Tool_cancellation_is_forwarded_and_registration_readback_is_available_during_maintenance() {
		using var f = new Fixture(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
		var ctx = new ToolContext { InvocationId = "native-pre-cancel", CancellationToken = cancel.Token, Host = null!, Runtime = f.Runtime,
			Project = f.Project, Agent = f.Runtime.Store.GetAgent(f.Project.RootAgentId!)!, Session = new Session { Id = "fixture_session", ProjectId = f.Project.Id, AgentId = f.Project.RootAgentId! } };
		var payload = new JsonObject { ["action"] = "register", ["path"] = f.Home.Workspace, ["integration_branch"] = "main", ["owner_id"] = f.Project.RootAgentId, ["operation_id"] = "tool-pre-cancel" };
		var result = await new GitWorkspaceTool().InvokeAsync(ctx, payload);
		Assert.True(result.IsError); Assert.Contains("deadline/cancellation", result.Text);
		Assert.Equal(0, f.Runtime.Db.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM git_registration_intents WHERE id='tool-pre-cancel'")));
		var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
		builder.Services.AddSingleton(f.Runtime); builder.Services.AddSingleton(new ServerOptions()); builder.Services.AddSingleton<EventHub>();
		await using var app = builder.Build(); Api.Map(app); await app.StartAsync();
		using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) }; client.DefaultRequestHeaders.Add("X-Ainur", "1");
		f.Runtime.Maintenance.Begin("read-only registration proof", TimeSpan.FromMilliseconds(1));
		var response = await client.PostAsJsonAsync($"/api/v1/projects/{f.Project.Id}/git", new JsonObject { ["action"] = "reconcile_registration", ["operation_id"] = "historical-no-intent" });
		Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Contains("unknown", await response.Content.ReadAsStringAsync());
	}

	[Fact]
	public void Ordinary_assignment_review_and_local_FF_are_durable_without_remote_or_models() {
		using var f = new Fixture();
		var w = f.Workspace(); Assert.Equal(w.BaseSha, Run(w.Path, "rev-parse", "HEAD"));
		w = f.Review(w);
		Assert.Equal("integration_owed", w.State);
		Assert.Contains(f.Runtime.Store.PendingNotifications(f.Reviewer.Id), n => n.Body.Contains(w.ReviewHead!));
		Assert.Contains(f.Runtime.Store.PendingNotifications(f.Project.RootAgentId!), n => n.Body.Contains("Local integration owed"));
		f.Restart();
		var integrated = f.Git.Integrate(f.Project.Id, f.Project.RootAgentId!, w.Id, "integrate-one");
		Assert.Equal("integrated", integrated.State); Assert.Equal("not_configured", integrated.PublicationState);
		Assert.Equal(w.AcceptanceHead, Run(f.Home.Workspace, "rev-parse", "HEAD"));
		Assert.Equal("retained", integrated.Retention); Assert.True(Directory.Exists(w.Path));
		Assert.Equal(integrated.Id, f.Git.Integrate(f.Project.Id, f.Project.RootAgentId!, w.Id, "integrate-one").Id);
		Assert.Equal(w.Id, f.Git.Create(f.Project.Id, f.Author.Id, f.Repository.Id, f.Objective.Id, "create-one").Id);
		Assert.Throws<DomainException>(() => f.Git.Create(f.Project.Id, f.Author.Id, f.Repository.Id, f.Objective.Id, "integrate-one"));
	}

	[Fact]
	public void Self_acceptance_unassigned_authors_and_non_owner_integration_are_rejected() {
		using var f = new Fixture(); var w = f.Workspace();
		Assert.Throws<DomainException>(() => f.Git.Create(f.Project.Id, f.Reviewer.Id, f.Repository.Id, f.Objective.Id, "outsider"));
		Commit(w.Path, "new", "text"); var head = Run(w.Path, "rev-parse", "HEAD");
		Assert.Throws<DomainException>(() => f.Git.Submit(f.Project.Id, f.Author.Id, w.Id, head, "tests", f.Author.Id));
		f.Git.Submit(f.Project.Id, f.Author.Id, w.Id, head, "tests", f.Reviewer.Id);
		Assert.Throws<DomainException>(() => f.Git.Accept(f.Project.Id, f.Author.Id, w.Id, head, w.BaseSha));
		f.Git.Accept(f.Project.Id, f.Reviewer.Id, w.Id, head, w.BaseSha);
		Assert.Throws<DomainException>(() => f.Git.Integrate(f.Project.Id, f.Author.Id, w.Id, "bad-integrator"));
	}

	[Fact]
	public void Source_change_invalidates_exact_receipt_and_old_review_base_is_rejected() {
		using var f = new Fixture(); var w = f.Review(f.Workspace());
		Commit(w.Path, "after-review", "changed");
		Assert.Throws<DomainException>(() => f.Git.Integrate(f.Project.Id, f.Project.RootAgentId!, w.Id, "stale"));
		Assert.Throws<DomainException>(() => f.Git.Accept(f.Project.Id, f.Reviewer.Id, w.Id, w.AcceptanceHead!, w.BaseSha));
		var head = Run(w.Path, "rev-parse", "HEAD"); f.Git.Submit(f.Project.Id, f.Author.Id, w.Id, head, "fresh tests", f.Reviewer.Id);
		Assert.Throws<DomainException>(() => f.Git.Accept(f.Project.Id, f.Reviewer.Id, w.Id, head, w.AcceptanceHead!));
		f.Git.Accept(f.Project.Id, f.Reviewer.Id, w.Id, head, w.BaseSha);
		Assert.Equal("integrated", f.Git.Integrate(f.Project.Id, f.Project.RootAgentId!, w.Id, "fresh").State);
	}

	[Fact]
	public void Moved_target_and_conflicting_parallel_authors_never_merge_or_overwrite() {
		using var f = new Fixture(); var first = f.Review(f.Workspace());
		var other = f.Runtime.CreateAgent(f.Project.Id, new NewAgent { Name = "Other author", Title = "implementer", ManagerId = f.Project.RootAgentId }, f.Project.RootAgentId);
		var objective = AddObjective(f.Runtime, f.Project.Id, other.Id);
		var second = f.Workspace("create-two", other.Id, objective.Id);
		Commit(second.Path, "change.txt", "conflicting independent change"); var head = Run(second.Path, "rev-parse", "HEAD");
		f.Git.Submit(f.Project.Id, other.Id, second.Id, head, "parallel tests", f.Reviewer.Id);
		f.Git.Accept(f.Project.Id, f.Reviewer.Id, second.Id, head, second.BaseSha);
		f.Git.Integrate(f.Project.Id, f.Project.RootAgentId!, first.Id, "first");
		Assert.Throws<DomainException>(() => f.Git.Integrate(f.Project.Id, f.Project.RootAgentId!, second.Id, "second"));
		Assert.Equal(first.AcceptanceHead, Run(f.Home.Workspace, "rev-parse", "HEAD"));
		Assert.Equal("conflicting independent change", File.ReadAllText(Path.Combine(second.Path, "change.txt")));
		Assert.Equal(head, Run(second.Path, "rev-parse", "HEAD"));
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void Dirty_tracked_or_untracked_target_is_preserved(bool tracked) {
		using var f = new Fixture(); var w = f.Review(f.Workspace());
		var path = Path.Combine(f.Home.Workspace, tracked ? "initial.txt" : "untracked-private"); File.WriteAllText(path, "user-owned dirty data");
		Assert.Throws<DomainException>(() => f.Git.Integrate(f.Project.Id, f.Project.RootAgentId!, w.Id, "dirty"));
		Assert.Equal("user-owned dirty data", File.ReadAllText(path)); Assert.Equal(w.BaseSha, Run(f.Home.Workspace, "rev-parse", "HEAD"));
	}

	[Fact]
	public void Retention_abandon_preserves_even_dirty_owned_worktree_and_ref() {
		using var f = new Fixture(); var w = f.Workspace(); File.WriteAllText(Path.Combine(w.Path, "untracked"), "keep");
		var abandoned = f.Git.Abandon(f.Project.Id, f.Author.Id, w.Id);
		Assert.Equal("abandoned", abandoned.State); Assert.Equal("retained", abandoned.Retention);
		Assert.Equal("keep", File.ReadAllText(Path.Combine(w.Path, "untracked"))); Assert.Equal(w.BaseSha, Run(w.Path, "rev-parse", "HEAD"));
		Assert.Throws<DomainException>(() => f.Git.Submit(f.Project.Id, f.Author.Id, w.Id, w.BaseSha, "tests", f.Reviewer.Id));
	}

	[Fact]
	public void Unknown_integration_restart_requires_reconcile_never_duplicate_replay() {
		using var f = new Fixture(); var w = f.Review(f.Workspace());
		f.Runtime.Db.Write(u => u.Execute("INSERT INTO git_operations(id,repository_id,workspace_id,action,fingerprint,actor,before_sha,intended_sha,state,created_at,updated_at) VALUES('crashed',@repo,@id,'integrate',@id,@actor,@before,@head,'intent',1,1)", new { repo = w.RepositoryId, id = w.Id, actor = f.Project.RootAgentId, before = w.BaseSha, head = w.AcceptanceHead }));
		Run(f.Home.Workspace, "merge", "--ff-only", w.AcceptanceHead!); // Simulated crash after Git, before durable completion.
		f.Restart();
		Assert.Throws<DomainException>(() => f.Git.Integrate(f.Project.Id, f.Project.RootAgentId!, w.Id, "crashed"));
		Assert.Throws<DomainException>(() => f.Git.Integrate(f.Project.Id, f.Project.RootAgentId!, w.Id, "different"));
		Assert.Equal("integrated", f.Git.Reconcile(f.Project.Id, f.Project.RootAgentId!, "crashed").State);
		Assert.Equal("integrated", f.Git.Integrate(f.Project.Id, f.Project.RootAgentId!, w.Id, "crashed").State);
	}

	[Fact]
	public void Unknown_unapplied_intent_can_be_observed_not_applied_without_replay() {
		using var f = new Fixture(); var w = f.Review(f.Workspace());
		f.Runtime.Db.Write(u => u.Execute("INSERT INTO git_operations(id,repository_id,workspace_id,action,fingerprint,actor,before_sha,intended_sha,state,created_at,updated_at) VALUES('unapplied',@repo,@id,'integrate',@id,@actor,@before,@head,'unknown',1,1)", new { repo = w.RepositoryId, id = w.Id, actor = f.Project.RootAgentId, before = w.BaseSha, head = w.AcceptanceHead }));
		Assert.Equal("integration_owed", f.Git.Reconcile(f.Project.Id, f.Project.RootAgentId!, "unapplied").State);
		Assert.Equal(w.BaseSha, Run(f.Home.Workspace, "rev-parse", "HEAD"));
		Assert.Throws<DomainException>(() => f.Git.Integrate(f.Project.Id, f.Project.RootAgentId!, w.Id, "unapplied"));
		Assert.Equal("integrated", f.Git.Integrate(f.Project.Id, f.Project.RootAgentId!, w.Id, "new-intent").State);
	}

	[Fact]
	public void Repository_and_worktree_outside_project_ownership_or_ref_drift_are_rejected() {
		using var f = new Fixture(); var w = f.Review(f.Workspace());
		Assert.Throws<DomainException>(() => f.Git.Register(f.Project.Id, f.Project.RootAgentId!, w.Path, "main", f.Project.RootAgentId!, operationId: Guid.NewGuid().ToString("N")));
		Assert.Throws<DomainException>(() => f.Git.Register(f.Project.Id, f.Author.Id, f.Home.Workspace, "main", f.Author.Id, operationId: Guid.NewGuid().ToString("N")));
		Run(w.Path, "checkout", "--detach");
		Assert.Throws<DomainException>(() => f.Git.Integrate(f.Project.Id, f.Project.RootAgentId!, w.Id, "detached"));
		Assert.Equal(w.BaseSha, Run(f.Home.Workspace, "rev-parse", "HEAD"));
	}

	[Fact]
	public void Publication_is_separate_explicit_authorized_and_remote_exact_readback() {
		using var f = new Fixture(remote: true); var w = f.Review(f.Workspace());
		w = f.Git.Integrate(f.Project.Id, f.Project.RootAgentId!, w.Id, "local"); Assert.Equal("publication_owed", w.PublicationState);
		Assert.Equal("", Run(f.Home.Workspace, "ls-remote", "fixture", "refs/heads/main"));
		Assert.Throws<DomainException>(() => f.Git.Publish(f.Project.Id, f.Author.Id, w.Id, "unauthorized"));
		w = f.Git.Publish(f.Project.Id, f.Project.RootAgentId!, w.Id, "publish"); Assert.Equal("published", w.PublicationState);
		Assert.StartsWith(w.IntegratedHead!, Run(f.Home.Workspace, "ls-remote", "fixture", "refs/heads/main"));
		f.Restart(); Assert.Equal("published", f.Git.PublicationReadback(f.Project.Id, f.Project.RootAgentId!, "publish").PublicationState);
	}

	[Fact]
	public void Repository_baton_blocks_parallel_integration_and_paused_actor_cannot_mutate() {
		using var f = new Fixture(); var w = f.Review(f.Workspace());
		using(var baton = new FileStream(Path.Combine(f.Repository.CommonDir, "ainur-integration.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
			Assert.Throws<DomainException>(() => f.Git.Integrate(f.Project.Id, f.Project.RootAgentId!, w.Id, "busy"));
		f.Runtime.Db.Write(u => u.Execute("UPDATE agents SET state='paused' WHERE id=@id", new { id = f.Project.RootAgentId }));
		Assert.Throws<DomainException>(() => f.Git.Integrate(f.Project.Id, f.Project.RootAgentId!, w.Id, "paused"));
	}

	[Fact]
	public void Create_crash_before_durable_result_reconciles_owned_worktree_without_recreating() {
		using var f = new Fixture(); var w = f.Workspace();
		f.Runtime.Db.Write(u => {
			u.Execute("UPDATE git_operations SET state='intent' WHERE id='create-one'");
			u.Execute("UPDATE git_workspaces SET state='creating' WHERE id=@id", new { id = w.Id });
		});
		f.Restart();
		Assert.Throws<DomainException>(() => f.Workspace());
		Assert.Equal("open", f.Git.Reconcile(f.Project.Id, f.Project.RootAgentId!, "create-one").State);
		Assert.Equal(w.Id, f.Workspace().Id);
	}

	[Fact]
	public void Dirty_source_and_in_progress_target_operation_are_preserved() {
		using var f = new Fixture(); var w = f.Review(f.Workspace());
		File.WriteAllText(Path.Combine(w.Path, "private-untracked"), "keep");
		Assert.Throws<DomainException>(() => f.Git.Integrate(f.Project.Id, f.Project.RootAgentId!, w.Id, "dirty-author"));
		Assert.Equal("keep", File.ReadAllText(Path.Combine(w.Path, "private-untracked"))); File.Delete(Path.Combine(w.Path, "private-untracked"));
		var marker = Path.Combine(f.Repository.CommonDir, "MERGE_HEAD"); File.WriteAllText(marker, w.AcceptanceHead);
		Assert.Throws<DomainException>(() => f.Git.Integrate(f.Project.Id, f.Project.RootAgentId!, w.Id, "pending-merge"));
		Assert.True(File.Exists(marker)); Assert.Equal(w.BaseSha, Run(f.Home.Workspace, "rev-parse", "HEAD"));
	}

	[Fact]
	public void Drifted_checkout_directory_and_outside_project_actor_cannot_mutate() {
		using var f = new Fixture(); var w = f.Review(f.Workspace());
		using var other = new TempHome();
		var moved = f.Home.Workspace + "-moved"; Directory.Move(f.Home.Workspace, moved);
		Directory.CreateDirectory(f.Home.Workspace);
		Run(f.Home.Workspace, "init", "-b", "main"); Run(f.Home.Workspace, "config", "user.name", "Other"); Run(f.Home.Workspace, "config", "user.email", "other@example.invalid"); Commit(f.Home.Workspace, "unrelated", "do not touch");
		try { Assert.Throws<DomainException>(() => f.Git.Integrate(f.Project.Id, f.Project.RootAgentId!, w.Id, "replaced-repo")); Assert.Equal("do not touch", File.ReadAllText(Path.Combine(f.Home.Workspace, "unrelated"))); }
		finally { Directory.Delete(f.Home.Workspace, true); Directory.Move(moved, f.Home.Workspace); }
		Assert.Throws<DomainException>(() => f.Git.Inspect("prj_other", f.Author.Id, w.Id));
	}

	[Fact]
	public void Ignored_user_file_is_not_overwritten_by_fast_forward() {
		using var f = new Fixture(); var w = f.Workspace();
		Run(f.Home.Workspace, "config", "core.excludesfile", Path.Combine(f.Home.Path, "ignore")); File.WriteAllText(Path.Combine(f.Home.Path, "ignore"), "protected\n");
		File.WriteAllText(Path.Combine(w.Path, "protected"), "authored"); Run(w.Path, "add", "-f", "protected"); Run(w.Path, "commit", "-m", "fixture ignored overlap");
		var head = Run(w.Path, "rev-parse", "HEAD"); f.Git.Submit(f.Project.Id, f.Author.Id, w.Id, head, "tests", f.Reviewer.Id); f.Git.Accept(f.Project.Id, f.Reviewer.Id, w.Id, head, w.BaseSha);
		File.WriteAllText(Path.Combine(f.Home.Workspace, "protected"), "ignored user data");
		Assert.Throws<DomainException>(() => f.Git.Integrate(f.Project.Id, f.Project.RootAgentId!, w.Id, "ignored"));
		Assert.Equal("ignored user data", File.ReadAllText(Path.Combine(f.Home.Workspace, "protected"))); Assert.Equal(w.BaseSha, Run(f.Home.Workspace, "rev-parse", "HEAD"));
	}

	[Fact]
	public void Publication_alias_rebind_blocks_forward_push_and_unknown_readback_without_touching_either_endpoint() {
		using var f = new Fixture(remote: true); var w = f.Review(f.Workspace());
		w = f.Git.Integrate(f.Project.Id, f.Project.RootAgentId!, w.Id, "local");
		var bareA = Path.Combine(f.Home.Path, "remote.git"); var bareB = Path.Combine(f.Home.Path, "other.git");
		Directory.CreateDirectory(bareB); Run(bareB, "init", "--bare");
		Run(f.Home.Workspace, "remote", "set-url", "fixture", bareB);
		Assert.Throws<DomainException>(() => f.Git.Publish(f.Project.Id, f.Project.RootAgentId!, w.Id, "rebound-forward"));
		Assert.Equal("", Run(f.Home.Workspace, "ls-remote", bareA, "refs/heads/main"));
		Assert.Equal("", Run(f.Home.Workspace, "ls-remote", bareB, "refs/heads/main"));
		Run(f.Home.Workspace, "remote", "set-url", "fixture", bareA);
		f.Git.Publish(f.Project.Id, f.Project.RootAgentId!, w.Id, "original-publish");
		f.Runtime.Db.Write(u => { u.Execute("UPDATE git_operations SET state='unknown' WHERE id='original-publish'"); u.Execute("UPDATE git_workspaces SET publication_state='publication_owed' WHERE id=@id", new { id = w.Id }); });
		// B deliberately contains the same SHA: matching content must not substitute publication authority.
		Run(f.Home.Workspace, "push", bareB, w.IntegratedHead + ":refs/heads/main");
		Run(f.Home.Workspace, "remote", "set-url", "fixture", bareB); f.Restart();
		Assert.Throws<DomainException>(() => f.Git.PublicationReadback(f.Project.Id, f.Project.RootAgentId!, "original-publish"));
		Assert.Equal("publication_owed", f.Git.Workspaces(f.Project.Id).Single().PublicationState);
		Assert.Contains("unknown", JsonSerializer.Serialize(f.Git.Status(f.Project.Id, "human")));
		Run(f.Home.Workspace, "remote", "set-url", "fixture", bareA);
		Assert.Equal("published", f.Git.PublicationReadback(f.Project.Id, f.Project.RootAgentId!, "original-publish").PublicationState);
	}

	[Fact]
	public void Distinct_fetch_and_push_endpoint_are_bound_and_only_push_endpoint_is_read_back() {
		using var f = new Fixture(); var fetch = Path.Combine(f.Home.Path, "fetch.git"); var push = Path.Combine(f.Home.Path, "push.git");
		foreach(var p in new[] { fetch, push }) { Directory.CreateDirectory(p); Run(p, "init", "--bare"); }
		Run(f.Home.Workspace, "remote", "add", "separate", fetch); Run(f.Home.Workspace, "remote", "set-url", "--push", "separate", push);
		// Fresh fixture registered without publication cannot silently change policy; use a separate project fixture helper.
		f.Runtime.Db.Write(u => u.Execute("DELETE FROM git_repositories WHERE id=@id", new { id = f.Repository.Id }));
		f.Repository = f.Git.Register(f.Project.Id, f.Project.RootAgentId!, f.Home.Workspace, "main", f.Project.RootAgentId!, "separate", "main", operationId: Guid.NewGuid().ToString("N"));
		Assert.NotEqual(f.Repository.FetchEndpointHash, f.Repository.PushEndpointHash);
		var w = f.Review(f.Workspace()); f.Git.Integrate(f.Project.Id, f.Project.RootAgentId!, w.Id, "local");
		Assert.Equal("published", f.Git.Publish(f.Project.Id, f.Project.RootAgentId!, w.Id, "push-distinct").PublicationState);
		Assert.Equal("", Run(f.Home.Workspace, "ls-remote", fetch, "refs/heads/main"));
		Assert.StartsWith(w.AcceptanceHead!, Run(f.Home.Workspace, "ls-remote", push, "refs/heads/main"));
		var operations = JsonSerializer.Serialize(f.Git.Status(f.Project.Id, "human")); Assert.DoesNotContain(push, operations); Assert.DoesNotContain(fetch, operations);
	}

	[Theory]
	[InlineData("multiple_fetch")]
	[InlineData("multiple_push")]
	[InlineData("rewrite")]
	[InlineData("uri_injection")]
	[InlineData("refspec_injection")]
	public void Ambiguous_or_unsafe_publication_configuration_fails_sanitized(string mode) {
		using var f = new Fixture(); const string privateCanary = "PRIVATE_CREDENTIAL_CANARY";
		var endpoint = "https://user:" + privateCanary + "@example.invalid/repo.git";
		Run(f.Home.Workspace, "remote", "add", "unsafe", endpoint);
		if(mode == "multiple_fetch") Run(f.Home.Workspace, "remote", "set-url", "--add", "unsafe", "https://example.invalid/second.git");
		if(mode == "multiple_push") { Run(f.Home.Workspace, "remote", "set-url", "--add", "--push", "unsafe", endpoint); Run(f.Home.Workspace, "remote", "set-url", "--add", "--push", "unsafe", "https://example.invalid/second.git"); }
		if(mode == "rewrite") Run(f.Home.Workspace, "config", "url.https://example.invalid/.insteadOf", "https://");
		if(mode == "uri_injection") Run(f.Home.Workspace, "remote", "set-url", "unsafe", endpoint + "?credential=" + privateCanary);
		var error = Assert.Throws<DomainException>(() => f.Git.Register(f.Project.Id, f.Project.RootAgentId!, f.Home.Workspace, "main", f.Project.RootAgentId!, "unsafe", mode == "refspec_injection" ? "main:refs/heads/injected" : "main", operationId: Guid.NewGuid().ToString("N")));
		Assert.DoesNotContain(privateCanary, error.Message); Assert.DoesNotContain(endpoint, error.Message);
		Assert.DoesNotContain(privateCanary, JsonSerializer.Serialize(f.Git.Status(f.Project.Id, "human")));
	}

	[Fact]
	public void Credential_endpoint_binding_persists_hash_only_and_duplicate_registration_rejects_drift() {
		using var f = new Fixture(); const string privateCanary = "PRIVATE_CREDENTIAL_CANARY";
		Run(f.Home.Workspace, "remote", "add", "credential", "https://user:" + privateCanary + "@example.invalid/repo.git");
		f.Runtime.Db.Write(u => u.Execute("DELETE FROM git_repositories WHERE id=@id", new { id = f.Repository.Id }));
		var r = f.Git.Register(f.Project.Id, f.Project.RootAgentId!, f.Home.Workspace, "main", f.Project.RootAgentId!, "credential", "main", operationId: Guid.NewGuid().ToString("N"));
		Assert.Equal(64, r.PushEndpointHash!.Length); Assert.DoesNotContain(privateCanary, JsonSerializer.Serialize(f.Git.Status(f.Project.Id, "human")));
		Assert.Equal(r.Id, f.Git.Register(f.Project.Id, f.Project.RootAgentId!, f.Home.Workspace, "main", f.Project.RootAgentId!, "credential", "main", operationId: Guid.NewGuid().ToString("N")).Id);
		Run(f.Home.Workspace, "remote", "set-url", "credential", "https://user:OTHER@different.invalid/repo.git");
		Assert.Throws<DomainException>(() => f.Git.Register(f.Project.Id, f.Project.RootAgentId!, f.Home.Workspace, "main", f.Project.RootAgentId!, "credential", "main", operationId: Guid.NewGuid().ToString("N")));
	}

	[Fact]
	public async Task Drain_rejects_all_agent_and_human_mutations_without_intents_notifications_or_Git_writes() {
		using var f = new Fixture(); var w = f.Review(f.Workspace());
		var before = JsonSerializer.Serialize(f.Git.Status(f.Project.Id, "human"));
		var worktrees = Run(f.Home.Workspace, "worktree", "list", "--porcelain");
		var notifications = f.Runtime.Store.PendingNotifications(f.Project.RootAgentId!).Count;
		await f.Runtime.DrainAsync(TimeSpan.Zero);
		var actions = new[] { "register", "create", "submit_review", "accept_review", "integrate", "publish", "abandon", "reconcile", "publication_readback" };
		var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
		builder.Services.AddSingleton(f.Runtime); builder.Services.AddSingleton(new ServerOptions()); builder.Services.AddSingleton<EventHub>();
		await using var app = builder.Build(); Api.Map(app); await app.StartAsync();
		using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) }; client.DefaultRequestHeaders.Add("X-Ainur", "1");
		var tool = new GitWorkspaceTool();
		var ctx = new ToolContext { InvocationId = "fixture_invocation", CancellationToken = default, Host = null!, Runtime = f.Runtime, Project = f.Project, Agent = f.Author, Session = new Session { Id = "fixture_session", ProjectId = f.Project.Id, AgentId = f.Author.Id } };
		foreach(var action in actions) {
			var payload = new JsonObject { ["action"] = action, ["repository_id"] = f.Repository.Id, ["workspace_id"] = w.Id,
				["objective_id"] = f.Objective.Id, ["operation_id"] = "drain-" + action, ["path"] = f.Home.Workspace, ["integration_branch"] = "main",
				["owner_id"] = f.Project.RootAgentId, ["head"] = w.AcceptanceHead, ["base"] = w.BaseSha, ["reviewer_id"] = f.Reviewer.Id, ["evidence"] = "tests" };
			var result = await tool.InvokeAsync(ctx, payload); Assert.True(result.IsError); Assert.Contains("not queued", result.Text);
			var http = await client.PostAsJsonAsync($"/api/v1/projects/{f.Project.Id}/git", payload);
			Assert.Equal(HttpStatusCode.Conflict, http.StatusCode); Assert.Contains("not queued", await http.Content.ReadAsStringAsync());
		}
		Assert.Equal(before, JsonSerializer.Serialize(f.Git.Status(f.Project.Id, "human")));
		Assert.Equal(worktrees, Run(f.Home.Workspace, "worktree", "list", "--porcelain"));
		Assert.Equal(notifications, f.Runtime.Store.PendingNotifications(f.Project.RootAgentId!).Count);
		Assert.Equal(w.BaseSha, Run(f.Home.Workspace, "rev-parse", "HEAD"));
		Assert.NotNull(f.Git.Inspect(f.Project.Id, "human", w.Id));
		Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/projects/{f.Project.Id}/git")).StatusCode);
	}

	[Fact]
	public async Task Already_admitted_mutation_blocks_checkpoint_until_settled_without_cancellation_or_replay() {
		using var f = new Fixture();
		// Invoke the production generic admission hook directly, not a substitute implementation.
		var method = typeof(AinurRuntime).GetMethod("AdmitMutation", BindingFlags.Instance | BindingFlags.NonPublic)!;
		using var lease = (IDisposable)method.Invoke(f.Runtime, ["git-fixture-already-admitted", f.Project.Id])!;
		var timedOut = await f.Runtime.DrainWithStatusAsync(TimeSpan.Zero);
		Assert.False(timedOut.Drained); Assert.Contains(timedOut.Running, id => id.StartsWith("git-fixture-already-admitted:"));
		Assert.False(f.Runtime.Draining); // Existing timeout rollback semantics are unchanged, not a queued retry.
		var draining = f.Runtime.DrainWithStatusAsync(TimeSpan.FromSeconds(5));
		Assert.True(f.Runtime.Draining); Assert.False(draining.IsCompleted);
		Assert.Throws<DomainException>(() => f.Workspace("after-drain"));
		lease.Dispose(); lease.Dispose(); // Settled exactly once; drain never canceled/replayed the admitted action.
		var snapshot = await draining; Assert.True(snapshot.Drained); Assert.Empty(snapshot.Running);
		f.Runtime.Undrain(); Assert.Equal("open", f.Workspace("after-drain").State);
	}

	[Fact]
	public async Task Maintenance_rejects_all_nine_mutations_for_tool_and_HTTP_but_retains_input_and_read_only_status() {
		using var f = new Fixture(); var w = f.Review(f.Workspace());
		f.Runtime.PauseAgent(f.Project.RootAgentId!, null, "intentional root pause");
		var op = f.Runtime.Maintenance.Begin("combined fence", TimeSpan.FromSeconds(1));
		var image = f.Runtime.UploadConversationImage(f.Project.Id, ConversationImageTests.Png(), "image/png");
		var entry = f.Runtime.PostUserMessage(f.Project.Id, "retained input", [image.Id], "git-held-input");
		Assert.Equal(entry.Id, f.Runtime.PostUserMessage(f.Project.Id, "retained input", [image.Id], "git-held-input").Id);
		var before = JsonSerializer.Serialize(f.Git.Status(f.Project.Id, "human"));
		var notifications = f.Runtime.Store.ListNotifications(f.Project.Id).Count;
		var worktrees = Run(f.Home.Workspace, "worktree", "list", "--porcelain");
		var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
		builder.Services.AddSingleton(f.Runtime); builder.Services.AddSingleton(new ServerOptions()); builder.Services.AddSingleton<EventHub>();
		await using var app = builder.Build(); Api.Map(app); await app.StartAsync();
		using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
		var tool = new GitWorkspaceTool();
		var ctx = new ToolContext { InvocationId = "fixture_invocation", CancellationToken = default, Host = null!, Runtime = f.Runtime, Project = f.Project, Agent = f.Author, Session = new Session { Id = f.Author.PrimarySessionId!, ProjectId = f.Project.Id, AgentId = f.Author.Id } };
		foreach(var action in new[] { "register", "create", "submit_review", "accept_review", "integrate", "publish", "abandon", "reconcile", "publication_readback" }) {
			var payload = new JsonObject { ["action"] = action, ["repository_id"] = f.Repository.Id, ["workspace_id"] = w.Id, ["objective_id"] = f.Objective.Id, ["operation_id"] = "maintenance-" + action, ["path"] = f.Home.Workspace, ["integration_branch"] = "main", ["owner_id"] = f.Project.RootAgentId, ["head"] = w.AcceptanceHead, ["base"] = w.BaseSha, ["reviewer_id"] = f.Reviewer.Id, ["evidence"] = "tests" };
			Assert.True((await tool.InvokeAsync(ctx, payload)).IsError);
			Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/v1/projects/{f.Project.Id}/git", payload)).StatusCode);
		}
		Assert.Equal(before, JsonSerializer.Serialize(f.Git.Status(f.Project.Id, "human")));
		Assert.Equal(notifications, f.Runtime.Store.ListNotifications(f.Project.Id).Count);
		Assert.Equal(worktrees, Run(f.Home.Workspace, "worktree", "list", "--porcelain"));
		Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/projects/{f.Project.Id}/git")).StatusCode);
		Assert.NotNull(f.Git.Inspect(f.Project.Id, "human", w.Id));
		Assert.Null(f.Runtime.GetHost(f.Author.PrimarySessionId!));
		f.Runtime.Maintenance.Release(op.Operation!.Id, true);
		Assert.Equal(AgentStates.Paused, f.Runtime.Store.GetAgent(f.Project.RootAgentId!)!.State);
		Assert.Single(f.Runtime.Store.Conversation(f.Project.Id), c => c.Author == "user");
		Assert.Equal(image.Id, Assert.Single(entry.Attachments).Id);
		await app.StopAsync();
	}

	[Fact]
	public async Task Combined_admission_holds_accumulate_and_settle_once_before_maintenance_or_drain_success() {
		using var f = new Fixture();
		var method = typeof(AinurRuntime).GetMethod("AdmitMutation", BindingFlags.Instance | BindingFlags.NonPublic)!;
		using var first = (IDisposable)method.Invoke(f.Runtime, ["git-staggered-one", f.Project.Id])!;
		using var second = (IDisposable)method.Invoke(f.Runtime, ["git-staggered-two", f.Project.Id])!;
		var timedOut = await f.Runtime.DrainWithStatusAsync(TimeSpan.Zero);
		Assert.False(timedOut.Drained); Assert.Equal(2, timedOut.Running.Count(id => id.StartsWith("git-staggered")));
		var op = f.Runtime.Maintenance.Begin("staggered Git", TimeSpan.FromSeconds(1));
		Assert.Equal(2, op.Blockers.Count(b => b.Kind == "git_mutation"));
		first.Dispose(); first.Dispose();
		Assert.False(f.Runtime.Maintenance.Status().VerifiedQuiescent);
		Assert.Single(f.Runtime.Maintenance.Status().Blockers, b => b.Kind == "git_mutation");
		Assert.Throws<MaintenanceAdmissionException>(() => f.Workspace("blocked"));
		await Assert.ThrowsAsync<DomainException>(() => f.Runtime.DrainWithStatusAsync(TimeSpan.Zero));
		second.Dispose(); second.Dispose();
		Assert.True(f.Runtime.Maintenance.Status().VerifiedQuiescent);
		Assert.Equal(2, f.Runtime.Db.Read(c => Dapper.SqlMapper.ExecuteScalar<int>(c, "SELECT COUNT(*) FROM maintenance_activities WHERE kind='git_mutation' AND reference_id LIKE 'git-staggered%' AND state='completed'")));
		f.Runtime.Maintenance.Release(op.Operation!.Id, false);
		Assert.True((await f.Runtime.DrainWithStatusAsync(TimeSpan.Zero)).Drained);
		Assert.Throws<DomainException>(() => f.Workspace("drain-blocked"));
		f.Runtime.Undrain(); Assert.Equal("open", f.Workspace("after-release").State);
		Assert.Empty(f.Runtime.Maintenance.Status().Blockers);
	}

	[Fact]
	public void Real_create_settles_both_leases_when_fence_arrives_after_its_durable_intent_and_on_exception() {
		using var f = new Fixture(); string? operation = null; bool fenced = false;
		f.Runtime.Db.Committed += _ => {
			if(fenced || !f.Runtime.Db.Read(c => Dapper.SqlMapper.ExecuteScalar<int>(c, "SELECT COUNT(*) FROM git_operations WHERE id='in-flight-create' AND state='intent'" ) != 0)) return;
			fenced = true;
			var status = f.Runtime.Maintenance.Begin("admitted create", TimeSpan.FromSeconds(1)); operation = status.Operation!.Id;
			Assert.Contains(status.Blockers, b => b.Kind == "git_mutation" && b.Id == "git-create" && b.State == "running");
			Assert.False(status.VerifiedQuiescent);
		};
		var w = f.Workspace("in-flight-create");
		Assert.NotNull(operation); Assert.Equal("open", w.State);
		Assert.True(f.Runtime.Maintenance.Status().VerifiedQuiescent);
		f.Runtime.Maintenance.Release(operation!, true);
		Assert.Throws<DomainException>(() => f.Git.Create(f.Project.Id, f.Author.Id, "missing", f.Objective.Id, "failing-create"));
		var check = f.Runtime.Maintenance.Begin("exception settled", TimeSpan.FromSeconds(1));
		Assert.True(check.VerifiedQuiescent);
		Assert.DoesNotContain(check.Blockers, b => b.Kind == "git_mutation");
		f.Runtime.Maintenance.Release(check.Operation!.Id, true);
		Assert.Equal(w.Id, f.Workspace("in-flight-create").Id);
	}

	[Fact]
	public void Restart_unknown_git_maintenance_lease_preserves_fence_then_manual_hold_after_abort_without_replay() {
		using var f = new Fixture();
		f.Runtime.Options.AutoStartHosts = false;
		f.Runtime.Start();
		var method = typeof(AinurRuntime).GetMethod("AdmitMutation", BindingFlags.Instance | BindingFlags.NonPublic)!;
		_ = (IDisposable)method.Invoke(f.Runtime, ["git-interrupted-owner", f.Project.Id])!;
		var operation = f.Runtime.Maintenance.Begin("crash", TimeSpan.FromSeconds(1)).Operation!.Id;
		var before = JsonSerializer.Serialize(f.Git.Status(f.Project.Id, "human"));
		var worktrees = Run(f.Home.Workspace, "worktree", "list", "--porcelain");
		f.Restart(); f.Runtime.Options.AutoStartHosts = false; f.Runtime.Start();
		Assert.True(f.Runtime.Maintenance.Fenced);
		Assert.Contains(f.Runtime.Maintenance.Status().Blockers, b => b.Kind == "git_mutation" && b.State == "unknown");
		Assert.False(f.Runtime.Maintenance.Status().VerifiedQuiescent);
		f.Runtime.Maintenance.Release(operation, true);
		Assert.Throws<DomainException>(() => f.Workspace("after-unknown-abort"));
		Assert.Equal(before, JsonSerializer.Serialize(f.Git.Status(f.Project.Id, "human")));
		Assert.Equal(worktrees, Run(f.Home.Workspace, "worktree", "list", "--porcelain"));
		Assert.Equal(1, f.Runtime.Db.Read(c => Dapper.SqlMapper.ExecuteScalar<int>(c, "SELECT COUNT(*) FROM maintenance_activities WHERE kind='git_mutation' AND state='unknown'")));
		Assert.NotNull(f.Git.Status(f.Project.Id, "human"));
	}

	[Fact]
	public void Actual_maintenance_schema11_reopen_to12_preserves_holds_input_continuity_and_workbook_then_ordinary_Git() {
		using var f = new Fixture();
		var books = new Workbooks(f.Runtime.Db); var book = books.Create(f.Project.Id, "preserved workbook");
		var cell = books.Add(f.Project.Id, book.Id, "powershell", "'retained source'")!;
		var notification = AinurRuntime.NewNotification(f.Project.Id, NotificationTypes.Assignment, f.Project.RootAgentId!, f.Author.Id, "durable assignment", f.Objective.Id);
		f.Runtime.Db.Write(u => f.Runtime.Store.InsertNotification(u, notification));
		f.Runtime.PauseAgent(f.Project.RootAgentId!, null, "intentional migration pause");
		var op = f.Runtime.Maintenance.Begin("persist maintenance11", TimeSpan.FromSeconds(1));
		var input = f.Runtime.PostUserMessage(f.Project.Id, "retained migration input", [], "migration-input");
		f.Runtime.Db.Write(u => u.Execute("DROP TABLE git_review_receipts; DROP TABLE git_registration_intents; DROP TABLE git_operations; DROP TABLE git_workspaces; DROP TABLE git_repositories; PRAGMA user_version=11;"));
		Assert.Equal(11, f.Runtime.Db.SchemaVersion);
		f.Restart(); f.Runtime.Options.AutoStartHosts = false; f.Runtime.Start(); Assert.Equal(14, f.Runtime.Db.SchemaVersion);
		Assert.True(f.Runtime.Maintenance.Fenced);
		Assert.Equal(op.Operation!.Id, f.Runtime.Maintenance.Status().Operation!.Id);
		Assert.True(f.Runtime.Maintenance.Status().VerifiedQuiescent);
		Assert.Equal(AgentStates.Paused, f.Runtime.Store.GetAgent(f.Project.RootAgentId!)!.State);
		Assert.Equal(input.Id, Assert.Single(f.Runtime.Store.Conversation(f.Project.Id), c => c.Author == "user").Id);
		Assert.Equal(notification!.Id, f.Runtime.Db.Read(c => Dapper.SqlMapper.ExecuteScalar<string>(c, "SELECT action_notification_id FROM work_obligations WHERE id=@id", new { id = notification.Id })));
		Assert.Equal("'retained source'", Assert.Single(new Workbooks(f.Runtime.Db).Open(f.Project.Id, book.Id)!.Cells).Cell.Source);
		Assert.Equal(cell.Id, Assert.Single(new Workbooks(f.Runtime.Db).Open(f.Project.Id, book.Id)!.Cells).Cell.Id);
		Assert.Equal(5, f.Runtime.Db.Read(c => Dapper.SqlMapper.ExecuteScalar<int>(c, "SELECT (SELECT COUNT(*) FROM pragma_table_info('git_repositories') WHERE name IN ('fetch_endpoint_hash','push_endpoint_hash')) + (SELECT COUNT(*) FROM pragma_table_info('git_operations') WHERE name IN ('fetch_endpoint_hash','push_endpoint_hash','publication_branch'))")));
		f.Runtime.Maintenance.Release(op.Operation.Id, true);
		Assert.Equal(AgentStates.Paused, f.Runtime.Store.GetAgent(f.Project.RootAgentId!)!.State);
		f.Runtime.PauseAgent(f.Reviewer.Id, null, "offline reviewer activation deliberately held");
		f.Repository = f.Git.Register(f.Project.Id, "human", f.Home.Workspace, "main", f.Author.Id, operationId: Guid.NewGuid().ToString("N"));
		var w = f.Workspace("post-upgrade");
		Commit(w.Path, "change.txt", "ordinary post-upgrade Git");
		var head = Run(w.Path, "rev-parse", "HEAD").Trim();
		f.Git.Submit(f.Project.Id, f.Author.Id, w.Id, head, "offline review", f.Reviewer.Id);
		f.Git.Accept(f.Project.Id, "human", w.Id, head, w.BaseSha);
		Assert.Equal("integrated", f.Git.Integrate(f.Project.Id, "human", w.Id, "post-upgrade-integrate").State);
		f.Restart(); Assert.Equal("integrated", f.Git.Workspaces(f.Project.Id).Single(x => x.Id == w.Id).State);
		Assert.Single(new Workbooks(f.Runtime.Db).List(f.Project.Id));
	}

	[Fact]
	public void Human_and_agent_tool_surface_share_workflow_and_bounded_inspection() {
		using var f = new Fixture();
		var result = GitWorkspaceTool.Execute(f.Git, f.Project.Id, "human", new JsonObject { ["action"] = "create", ["repository_id"] = f.Repository.Id, ["objective_id"] = f.Objective.Id, ["operation_id"] = "human-create" });
		var w = Assert.IsType<GitWorkspace>(result); Assert.Equal("human", w.OwnerId);
		Commit(w.Path, "human.txt", "local human author"); var head = Run(w.Path, "rev-parse", "HEAD");
		f.Git.Submit(f.Project.Id, "human", w.Id, head, "git diff --check: passed", f.Reviewer.Id);
		Assert.Throws<DomainException>(() => f.Git.Accept(f.Project.Id, "human", w.Id, head, w.BaseSha));
		f.Git.Accept(f.Project.Id, f.Reviewer.Id, w.Id, head, w.BaseSha);
		var inspection = JsonSerializer.Serialize(f.Git.Inspect(f.Project.Id, "human", w.Id)); Assert.Contains("human.txt", inspection);
		Assert.Equal("integrated", f.Git.Integrate(f.Project.Id, "human", w.Id, "human-integrate").State);
	}
}
