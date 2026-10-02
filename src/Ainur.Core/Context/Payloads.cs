using Ainur.Core.Providers;
using Ainur.Core.Tools;

namespace Ainur.Core.Context;

public sealed class UserPayload {
	public List<ToolImage> Images { get; set; } = [];
	public string Text { get; set; } = "";
	public List<string> NotificationIds { get; set; } = [];
}

public sealed class AssistantPayload {
	public string? Content { get; set; }
	public string? Reasoning { get; set; }
	public List<ToolCall> ToolCalls { get; set; } = [];
	public string? FinishReason { get; set; }
	public string? ModelRequestId { get; set; }
	public string? ModelId { get; set; }
}

public sealed class ToolResultPayload {
	public string CallId { get; set; } = "";
	public string ToolName { get; set; } = "";
	public string ToolVersion { get; set; } = "";
	public string InvocationId { get; set; } = "";
	public bool IsError { get; set; }
	/// <summary>Full model-facing text, always also stored as an artifact.</summary>
	public string Text { get; set; } = "";
	public string? Artifact { get; set; }
	public int Chars { get; set; }
	public string? Description { get; set; }
	public string? ValueHandle { get; set; }
	/// <summary>Typed, authorized image artifact references; no binary or base64 is written into the transcript.</summary>
	public List<ToolImage> Images { get; set; } = [];
}

public sealed class SummaryPayload {
	public string Text { get; set; } = "";
	public string Mode { get; set; } = "";
	public long CoversFromSeq { get; set; }
	public long CoversThroughSeq { get; set; }
	public string CompactionId { get; set; } = "";
	public string? PreviousSummaryItemId { get; set; }
}

public sealed class NoticePayload {
	public string Text { get; set; } = "";
}

/// <summary>Durable description of what the next request sees. The transcript itself is never modified.</summary>
public sealed class ContextViewState {
	/// <summary>Summary item standing in for every item with seq ≤ <see cref="CutoffSeq"/>.</summary>
	public string? SummaryItemId { get; set; }
	public long CutoffSeq { get; set; }
	/// <summary>Tool results the agent explicitly elided.</summary>
	public HashSet<string> Elided { get; set; } = [];
	/// <summary>Tool results the agent asked to keep visible, mapped to the turn through which they stay retained.</summary>
	public Dictionary<string, int> RetainedUntil { get; set; } = [];
	public string PolicyVersion { get; set; } = ContextPolicy.Version;
}
