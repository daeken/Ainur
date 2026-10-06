using System.Text.Json;
using System.Text.Json.Nodes;
using Ainur.Core.Persistence;
using Ainur.Core.Runtime;

namespace Ainur.Core.Tools;

/// <summary>Agent and local-human requests share one ownership/review/integration implementation.</summary>
public sealed class GitWorkspaceTool : BuiltinTool {
	public override string Name => "git_workspace";
	public override string Description => "Opt-in native Git: register this project's clean repository/integration branch, create an objective-owned author worktree, inspect exact base/head/path/test evidence, submit to an independent reviewer, accept exact review, explicitly integrate clean FF-only, reconcile unknown operations, and report retention/publication obligations. No automatic push or deletion. Use returned author path explicitly for shell/edit tools; never edit the integration checkout.";
	public override IReadOnlyList<string> Tags => ["git", "worktree", "workspace", "review", "integrate", "publish", "repository"];
	public override JsonObject InputSchema => Schema.Object(
		("action", Schema.String("Lifecycle action", "status", "register", "create", "inspect", "submit_review", "accept_review", "integrate", "reconcile", "reconcile_registration", "abandon", "publish", "publication_readback"), true),
		("repository_id", Schema.String("Registered repository for create"), false),
		("workspace_id", Schema.String("Owned workspace"), false),
		("objective_id", Schema.String("Assigned objective for create"), false),
		("operation_id", Schema.String("Caller-chosen unique id required for register/create/integrate/publish/reconcile/reconcile_registration; never replay unknown"), false),
		("path", Schema.String("Exact project workspace for registration"), false),
		("integration_branch", Schema.String("Clean checked-out local integration branch"), false),
		("owner_id", Schema.String("Project agent responsible for integration/publication"), false),
		("remote", Schema.String("Optional existing named publication remote; no automatic pushes"), false),
		("remote_branch", Schema.String("Optional publication branch; requires remote"), false),
		("head", Schema.String("Exact reviewed source head"), false),
		("base", Schema.String("Exact review base for independent acceptance"), false),
		("reviewer_id", Schema.String("Independent project agent for review dispatch"), false),
		("evidence", Schema.String("Bounded exact test commands/results/artifact references, never secrets"), false));
	public static object Execute(NativeGit service, string project, string actor, JsonObject args) => Str(args, "action") switch {
		"status" => service.Status(project, actor),
		"register" => service.Register(project, actor, Str(args, "path"), Str(args, "integration_branch"), Str(args, "owner_id"), OptStr(args, "remote"), OptStr(args, "remote_branch"), Str(args, "operation_id")),
		"create" => service.Create(project, actor, Str(args, "repository_id"), Str(args, "objective_id"), Str(args, "operation_id")),
		"inspect" => service.Inspect(project, actor, Str(args, "workspace_id")),
		"submit_review" => service.Submit(project, actor, Str(args, "workspace_id"), Str(args, "head"), Str(args, "evidence"), Str(args, "reviewer_id")),
		"accept_review" => service.Accept(project, actor, Str(args, "workspace_id"), Str(args, "head"), Str(args, "base")),
		"integrate" => service.Integrate(project, actor, Str(args, "workspace_id"), Str(args, "operation_id")),
		"reconcile" => service.Reconcile(project, actor, Str(args, "operation_id")),
		"reconcile_registration" => service.ReconcileRegistration(project, actor, Str(args, "operation_id")),
		"abandon" => service.Abandon(project, actor, Str(args, "workspace_id")),
		"publish" => service.Publish(project, actor, Str(args, "workspace_id"), Str(args, "operation_id")),
		"publication_readback" => service.PublicationReadback(project, actor, Str(args, "operation_id")),
		_ => throw new ToolException("Unknown native Git action.")
	};
	public override Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		try {
			var value = Execute(new NativeGit(ctx.Runtime, ctx.CancellationToken), ctx.Project.Id, ctx.Agent.Id, args);
			return Task.FromResult(ToolResult.Ok(JsonSerializer.Serialize(value, JsonUtil.Options), value));
		} catch(DomainException ex) { return Task.FromResult(ToolResult.Error(ex.Message)); }
	}
}
