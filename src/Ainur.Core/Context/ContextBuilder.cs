using System.Text;
using Ainur.Core.Model;
using Ainur.Core.Providers;

namespace Ainur.Core.Context;

/// <summary>Configurable limits for one session's context. Numbers are starting points, not product semantics.</summary>
public sealed class ContextPolicy {
	public const string Version = "1";
	/// <summary>Elide eligible tool results after this many completed turns following first consumption.</summary>
	public int ElideAfterTurns { get; set; } = 6;
	public int MaxInlineResultChars { get; set; } = 24_000;
	public int AggregateResultChars { get; set; } = 64_000;
	public int PreviewChars { get; set; } = 3_000;
	/// <summary>Compact when the estimated request exceeds this fraction of the usable budget.</summary>
	public double TriggerFraction { get; set; } = 0.75;
	/// <summary>Target fraction of usable budget after compaction (desired headroom).</summary>
	public double TargetFraction { get; set; } = 0.5;
	/// <summary>Rolling compaction replaces this fraction of eligible conversation tokens, oldest first.</summary>
	public double RollingFraction { get; set; } = 0.5;
	public int MaxRollingPasses { get; set; } = 3;
	public int SummaryMaxTokens { get; set; } = 4_000;
	/// <summary>Overrides the model's window; useful to keep long-lived sessions cheap and to exercise compaction.</summary>
	public int? MaxContextTokens { get; set; }
	public int ReservedOutputTokens { get; set; } = 16_000;
	public int ToolTokenBudget { get; set; } = 14_000;
	public string CompactorModelId { get; set; } = "deepseek-v4.1-flash";

	public int UsableBudget(ModelInfo model) {
		var window = (int) ((model.ContextTokens ?? 128_000) * 0.95);
		if(MaxContextTokens is { } cap) window = Math.Min(window, cap);
		return Math.Max(1_000, window - ReservedOutputTokens);
	}

	public ContextPolicy Clone() => (ContextPolicy) MemberwiseClone();
}

public sealed class BuiltContext {
	public List<ChatMessage> Messages { get; init; } = [];
	public int EstimatedTokens { get; init; }
	public int ConversationTokens { get; init; }
	public long ThroughSeq { get; init; }
	public List<string> AutoElided { get; init; } = [];
	public int Revision { get; init; }
}

/// <summary>
/// Assembles the provider-facing request from durable records: authoritative instructions, the current summary,
/// and the verbatim suffix with tool-result elision applied. Tool call/result pairing is always preserved.
/// </summary>
public static class ContextBuilder {
	public static BuiltContext Build(string systemPrompt, ContextViewState view, IReadOnlyList<SessionItem> items, SessionItem? summary,
		int currentTurn, ContextPolicy policy, double tokenRatio, int revision) {
		var messages = new List<ChatMessage> { ChatMessage.System(systemPrompt) };
		var autoElided = new List<string>();
		if(summary is not null) {
			var s = JsonUtil.Deserialize<SummaryPayload>(summary.Payload)!;
			messages.Add(ChatMessage.User(RenderSummary(s)));
		}
		// Summary items are rendered only through the view's current summary; superseded ones never reappear.
		var visible = items.Where(i => i.Seq > view.CutoffSeq && i.Kind != ItemKinds.Summary).OrderBy(i => i.Seq).ToList();
		var lastUserSeq = visible.LastOrDefault(i => i.Kind is ItemKinds.User or ItemKinds.Notice)?.Seq ?? 0;

		for(var idx = 0; idx < visible.Count; idx++) {
			var item = visible[idx];
			switch(item.Kind) {
				case ItemKinds.User:
					messages.Add(ChatMessage.User(JsonUtil.Deserialize<UserPayload>(item.Payload)!.Text));
					break;
				case ItemKinds.Notice:
					messages.Add(ChatMessage.User("[Runtime notice] " + JsonUtil.Deserialize<NoticePayload>(item.Payload)!.Text));
					break;
				case ItemKinds.Assistant: {
					var a = JsonUtil.Deserialize<AssistantPayload>(item.Payload)!;
					// Reasoning is required by thinking models within the active tool loop; older reasoning is not replayed.
					var reasoning = item.Seq > lastUserSeq ? a.Reasoning : null;
					// Missing results (e.g. lost in a crash) are synthesized by FillMissingResults to keep the request valid.
					messages.Add(ChatMessage.Assistant(a.Content, reasoning, a.ToolCalls.Count > 0 ? a.ToolCalls : null));
					break;
				}
				case ItemKinds.ToolResult: {
					var batch = new List<SessionItem>();
					while(idx < visible.Count && visible[idx].Kind == ItemKinds.ToolResult) batch.Add(visible[idx++]);
					idx--;
					RenderResultBatch(batch, messages, view, currentTurn, policy, autoElided);
					break;
				}
			}
		}
		FillMissingResults(messages);

		var conversationChars = messages.Skip(1).Sum(MessageTokens);
		var total = (int) Math.Ceiling((MessageTokens(messages[0]) + conversationChars) * tokenRatio);
		return new BuiltContext {
			Messages = messages, EstimatedTokens = total, ConversationTokens = (int) Math.Ceiling(conversationChars * tokenRatio),
			ThroughSeq = visible.LastOrDefault()?.Seq ?? view.CutoffSeq, AutoElided = autoElided, Revision = revision,
		};
	}

	public static int MessageTokens(ChatMessage m) =>
		Tokens.Estimate(m.Content) + Tokens.Estimate(m.Reasoning) + (m.ToolCalls?.Sum(c => Tokens.Estimate(c.Name) + Tokens.Estimate(c.Arguments) + 6) ?? 0) + 4;

	public static string RenderSummary(SummaryPayload s) =>
		$"[Context summary — {s.Mode} compaction {s.CompactionId} covering transcript items #{s.CoversFromSeq}–#{s.CoversThroughSeq}. " +
		"This is a summary written by the runtime, not original statements; exact source items remain retrievable with read_history.]\n\n" + s.Text;

	/// <summary>True when the result's body should be replaced by an elision record in this request.</summary>
	public static bool IsElided(SessionItem item, ContextViewState view, int currentTurn, ContextPolicy policy) {
		if(view.Elided.Contains(item.Id)) return true;
		if(view.RetainedUntil.TryGetValue(item.Id, out var until) && currentTurn <= until) return false;
		// First consumed by the response that produced turn item.Turn + 1.
		var age = currentTurn - (item.Turn + 1);
		return age >= policy.ElideAfterTurns;
	}

	static void RenderResultBatch(List<SessionItem> batch, List<ChatMessage> messages, ContextViewState view, int currentTurn, ContextPolicy policy, List<string> autoElided) {
		var rendered = new List<(string CallId, string Text)>();
		var budgetPer = batch.Count == 0 ? policy.AggregateResultChars : Math.Max(policy.PreviewChars / 2, policy.AggregateResultChars / batch.Count);
		var batchFull = batch.Select(i => JsonUtil.Deserialize<ToolResultPayload>(i.Payload)!).Sum(p => Math.Min(p.Chars, policy.MaxInlineResultChars)) > policy.AggregateResultChars;
		foreach(var item in batch) {
			var r = JsonUtil.Deserialize<ToolResultPayload>(item.Payload)!;
			string text;
			if(IsElided(item, view, currentTurn, policy)) {
				if(!view.Elided.Contains(item.Id)) autoElided.Add(item.Id);
				text = ElisionRecord(r);
			} else {
				var limit = batchFull ? Math.Min(policy.MaxInlineResultChars, budgetPer) : policy.MaxInlineResultChars;
				text = r.Text.Length <= limit ? r.Text
					: TextUtil.Preview(r.Text, Math.Min(limit, Math.Max(policy.PreviewChars, limit))) + $"\n[Result is {r.Chars} chars; this is a bounded preview. Use read_result with invocation_id {r.InvocationId} to read a range.]";
			}
			if(r.IsError) text = "ERROR: " + text;
			if(r.ValueHandle is not null) text += $"\n[live object handle: {r.ValueHandle}]";
			rendered.Add((r.CallId, text));
		}
		foreach(var (callId, text) in rendered)
			messages.Add(ChatMessage.Tool(callId, text));
	}

	public static string ElisionRecord(ToolResultPayload r) =>
		$"[elided result: invocation {r.InvocationId}, tool {r.ToolVersion}, {(r.IsError ? "error" : "ok")}, {r.Chars} chars" +
		(r.Description is null ? "" : $", {r.Description}") + $". Use read_result with invocation_id {r.InvocationId} to retrieve an excerpt.]";

	/// <summary>Ensures every assistant tool call is answered before the next non-tool message.</summary>
	static void FillMissingResults(List<ChatMessage> messages) {
		for(var i = 0; i < messages.Count; i++) {
			if(messages[i] is not { Role: "assistant", ToolCalls: { Count: > 0 } calls }) continue;
			var j = i + 1;
			var seen = new HashSet<string>();
			while(j < messages.Count && messages[j].Role == "tool") seen.Add(messages[j++].ToolCallId!);
			var missing = calls.Where(c => !seen.Contains(c.Id)).ToList();
			// Drop results that do not belong to this assistant message (cannot be sent without a matching call).
			for(var k = j - 1; k > i; k--)
				if(!calls.Any(c => c.Id == messages[k].ToolCallId)) {
					messages.RemoveAt(k);
					j--;
				}
			foreach(var call in missing)
				messages.Insert(j++, ChatMessage.Tool(call.Id, "[no result was recorded for this call; its outcome is unknown]"));
		}
	}

	/// <summary>Renders transcript items as plain text for a compactor or history reader.</summary>
	public static string RenderTranscript(IEnumerable<SessionItem> items, int maxResultChars = 2_000) {
		var sb = new StringBuilder();
		foreach(var item in items) {
			sb.Append($"#{item.Seq} ");
			switch(item.Kind) {
				case ItemKinds.User: sb.Append("[user/inbox] ").Append(JsonUtil.Deserialize<UserPayload>(item.Payload)!.Text); break;
				case ItemKinds.Notice: sb.Append("[runtime notice] ").Append(JsonUtil.Deserialize<NoticePayload>(item.Payload)!.Text); break;
				case ItemKinds.Summary: sb.Append("[earlier summary] ").Append(JsonUtil.Deserialize<SummaryPayload>(item.Payload)!.Text); break;
				case ItemKinds.Assistant: {
					var a = JsonUtil.Deserialize<AssistantPayload>(item.Payload)!;
					sb.Append("[assistant] ").Append(a.Content);
					foreach(var c in a.ToolCalls) sb.Append($"\n  -> call {c.Name}({TextUtil.Truncate(c.Arguments, 600)}) id={c.Id}");
					break;
				}
				case ItemKinds.ToolResult: {
					var r = JsonUtil.Deserialize<ToolResultPayload>(item.Payload)!;
					sb.Append($"[tool result {r.ToolName} invocation={r.InvocationId}{(r.IsError ? " ERROR" : "")}] ").Append(TextUtil.Preview(r.Text, maxResultChars));
					break;
				}
			}
			sb.Append("\n\n");
		}
		return sb.ToString();
	}
}
