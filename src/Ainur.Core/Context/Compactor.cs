using Ainur.Core.Model;
using Ainur.Core.Persistence;
using Ainur.Core.Providers;
using Ainur.Core.Runtime;

namespace Ainur.Core.Context;

public sealed record CompactionOutcome(string CompactionId, string Mode, long ThroughSeq, int SourceTokens, int SummaryTokens, ContextViewState View);

/// <summary>
/// Full and rolling compaction. Runs only at committed boundaries, persists the new view atomically with its
/// provenance, and leaves the previous view in place if summarization fails.
/// </summary>
public sealed class Compactor(Store store, ModelGateway gateway) {
	public const string Prompt = """
		You are the context compactor for a long-running agent in the Ainur orchestrator. You receive part of the agent's
		transcript (and possibly an earlier summary that already covers an older prefix). Write a single replacement summary
		that will stand in for all of it in the agent's future context.

		Requirements:
		- Preserve current goals, requirements, commitments, accepted decisions, unresolved questions, blockers, and the
		  state of work in progress, including what the agent was about to do next.
		- Preserve exact evidence references: transcript item numbers (#N), tool invocation ids (inv_...), file paths,
		  commit hashes, objective ids, agent ids, and identifiers needed to retrieve sources later.
		- Never change an accepted decision and never drop an unresolved constraint from the user or a manager.
		- Clearly attribute statements (who said or decided what). Do not invent facts.
		- Fold the earlier summary into the new one rather than nesting it; the result must cover the whole range.
		- Be concise and structured (headings and bullets). Stay under the stated length limit.
		""";

	public async Task<CompactionOutcome> CompactAsync(Session session, Agent agent, ContextViewState view, string mode, ContextPolicy policy, CancellationToken ct) {
		var items = store.Items(session.Id);
		var visible = items.Where(i => i.Seq > view.CutoffSeq && i.Kind != ItemKinds.Summary).OrderBy(i => i.Seq).ToList();
		if(visible.Count == 0) throw new InvalidOperationException("Nothing to compact");
		var previous = view.SummaryItemId is null ? null : items.FirstOrDefault(i => i.Id == view.SummaryItemId);
		var previousSummary = previous is null ? null : Json.Deserialize<SummaryPayload>(previous.Payload);

		int cut; // index of the last item to fold into the summary
		if(mode == CompactionModes.Full)
			cut = visible.Count - 1;
		else {
			var total = visible.Sum(i => i.TokenEstimate);
			var target = total * policy.RollingFraction;
			cut = -1;
			var acc = 0.0;
			for(var i = 0; i < visible.Count - 1; i++) {
				acc += visible[i].TokenEstimate;
				if(acc >= target && IsBoundary(visible, i)) {
					cut = i;
					break;
				}
			}
			if(cut < 0) {
				// No valid boundary that preserves a verbatim suffix; fall back explicitly to full compaction.
				mode = CompactionModes.Full;
				cut = visible.Count - 1;
			}
		}
		var folded = visible.Take(cut + 1).ToList();
		var through = folded[^1].Seq;
		var from = previousSummary?.CoversFromSeq ?? folded[0].Seq;
		var sourceTokens = folded.Sum(i => i.TokenEstimate) + (previousSummary is null ? 0 : Tokens.Estimate(previousSummary.Text));

		var compactionId = Ids.New("cmp");
		store.Db.Write(u => {
			u.Execute("""
				INSERT INTO compactions(id,session_id,mode,from_seq,through_seq,source_tokens,state,policy,created_at)
				VALUES(@compactionId,@sid,@mode,@from,@through,@sourceTokens,'running',@policy,@now)
				""", new { compactionId, sid = session.Id, mode, from, through, sourceTokens, policy = Json.Serialize(policy), now = Clock.Now });
			u.Journal("compaction.started", session.ProjectId, "session", session.Id, agent.Id, new { compactionId, mode, from, through, sourceTokens });
		});

		try {
			var model = store.GetModel(policy.CompactorModelId) ?? store.GetModel(session.ModelId)!;
			var text = await SummarizeAsync(session, agent, model, previousSummary?.Text, folded, policy, ct);
			var payload = new SummaryPayload {
				Text = text, Mode = mode, CoversFromSeq = from, CoversThroughSeq = through, CompactionId = compactionId, PreviousSummaryItemId = previous?.Id,
			};
			var newView = new ContextViewState {
				CutoffSeq = through, Elided = view.Elided, RetainedUntil = view.RetainedUntil, PolicyVersion = ContextPolicy.Version,
			};
			var summaryTokens = Tokens.Estimate(text);
			store.Db.Write(u => {
				var item = store.AppendItem(u, session.Id, ItemKinds.Summary, payload, summaryTokens);
				newView.SummaryItemId = item.Id;
				store.CommitView(u, session.Id, Json.Serialize(newView), $"{mode} compaction {compactionId}");
				u.Execute("UPDATE compactions SET state='succeeded', summary_item_id=@id, summary_tokens=@summaryTokens, finished_at=@now WHERE id=@compactionId",
					new { id = item.Id, summaryTokens, now = Clock.Now, compactionId });
				u.Journal("compaction.succeeded", session.ProjectId, "session", session.Id, agent.Id, new { compactionId, mode, from, through, sourceTokens, summaryTokens });
			});
			return new(compactionId, mode, through, sourceTokens, summaryTokens, newView);
		} catch(Exception e) {
			store.Db.Write(u => {
				u.Execute("UPDATE compactions SET state='failed', error=@error, finished_at=@now WHERE id=@compactionId", new { error = e.Message, now = Clock.Now, compactionId });
				u.Journal("compaction.failed", session.ProjectId, "session", session.Id, agent.Id, new { compactionId, error = e.Message });
			});
			throw;
		}
	}

	/// <summary>A cut after index i is valid when it does not separate an assistant tool call from its results.</summary>
	public static bool IsBoundary(List<SessionItem> visible, int i) =>
		i + 1 >= visible.Count || visible[i + 1].Kind != ItemKinds.ToolResult;

	async Task<string> SummarizeAsync(Session session, Agent agent, ModelInfo model, string? previous, List<SessionItem> folded, ContextPolicy policy, CancellationToken ct) {
		// The source must fit the compactor's own window: summarize bounded segments with provenance first if needed.
		var window = (int) ((model.ContextTokens ?? 128_000) * 0.8) - policy.SummaryMaxTokens * 2;
		var segments = new List<List<SessionItem>> { new() };
		var acc = previous is null ? 0 : Tokens.Estimate(previous);
		foreach(var item in folded) {
			if(acc + item.TokenEstimate > window && segments[^1].Count > 0) {
				segments.Add([]);
				acc = 0;
			}
			segments[^1].Add(item);
			acc += item.TokenEstimate;
		}
		var carried = previous;
		foreach(var segment in segments) {
			var source = ContextBuilder.RenderTranscript(segment);
			var user = $"""
				Agent: {agent.Name} ({agent.Role}, {agent.Title}). Session {session.Id}.
				Length limit: about {policy.SummaryMaxTokens} tokens.

				{(carried is null ? "There is no earlier summary." : $"EARLIER SUMMARY (covers transcript before #{segment[0].Seq}):\n{carried}")}

				TRANSCRIPT ITEMS #{segment[0].Seq}–#{segment[^1].Seq}:
				{source}

				Write the replacement summary now.
				""";
			var result = await gateway.CallAsync(new ModelCall {
				ProjectId = session.ProjectId, AgentId = agent.Id, SessionId = session.Id, Purpose = "compaction", Category = "compaction",
				Model = model, Messages = [ChatMessage.System(Prompt), ChatMessage.User(user)],
				MaxOutputTokens = policy.SummaryMaxTokens * 2, ReasoningEffort = "none",
			}, ct);
			carried = result.Response.Content?.Trim();
			if(string.IsNullOrWhiteSpace(carried)) throw new InvalidOperationException("Compactor returned an empty summary");
		}
		return carried!;
	}
}
