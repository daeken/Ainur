using System.Text.Json;
using System.Text.Json.Nodes;
using Ainur.Core.Runtime;

namespace Ainur.Core.Tools;

/// <summary>One project-scoped native agent entry point, sharing exactly the API's durable workbook service.</summary>
public sealed class WorkbookTool : BuiltinTool {
	public override string Name => "workbook";
	public override string Description => "Create/list/open a project workbook, add/edit a PowerShell cell with revision compare-and-swap, explicitly run a saved revision, and read durable output. Runs use fresh runspaces inside the server, not OS sandboxes; side effects and cooperative cancellation apply. Never automatically rerun.";
	public override IReadOnlyList<string> Tags => ["workbook", "notebook", "powershell", "experiment", "cell"];
	public override TimeSpan Timeout => TimeSpan.FromMinutes(5);
	public override JsonObject InputSchema => Schema.Object(
		("action", Schema.String("list, create, open, add, edit, run, or runs"), true),
		("workbook_id", Schema.String("Workbook id for open/add/edit/run/runs"), false),
		("cell_id", Schema.String("Cell id for edit/run/runs"), false),
		("title", Schema.String("Title for create"), false),
		("source", Schema.String("PowerShell source for add/edit"), false),
		("revision", Schema.Integer("Explicit saved revision for run"), false),
		("expected_revision", Schema.Integer("Current revision for edit compare-and-swap"), false));
	public override async Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var w = new Workbooks(ctx.Runtime.Db) { Admission = id => ctx.Runtime.Maintenance.Admit("workbook", ctx.Session.Id, id) };
		var projectId = ctx.Project.Id;
		var action = Str(args, "action");
		string Json(object? value) => JsonSerializer.Serialize(value, Ainur.Core.JsonUtil.Options);
		try {
			object? result = action switch {
				"list" => w.List(projectId),
				"create" => w.Create(projectId, Str(args, "title")),
				"open" => w.Open(projectId, Str(args, "workbook_id")),
				"add" => w.Add(projectId, Str(args, "workbook_id"), "powershell", Str(args, "source")),
				"edit" => w.Edit(projectId, Str(args, "workbook_id"), Str(args, "cell_id"), OptInt(args, "expected_revision") ?? throw new ToolException("expected_revision is required"), Str(args, "source")),
				"runs" => w.Runs(projectId, Str(args, "workbook_id"), Str(args, "cell_id")),
				"run" => await w.ExecuteAsync(projectId, Str(args, "workbook_id"), Str(args, "cell_id"), OptInt(args, "revision") ?? throw new ToolException("revision is required"), ctx.Agent.Id, ctx.Project.WorkspacePath, ctx.CancellationToken),
				_ => throw new ToolException("Unknown workbook action."),
			};
			return result is null ? ToolResult.Error("Workbook or cell not found in this project.") : ToolResult.Ok(Json(result), result);
		} catch(Workbooks.ConflictException ex) { return ToolResult.Error(ex.Message); }
		catch(ArgumentException ex) { return ToolResult.Error(ex.Message); }
	}
}
