namespace Ainur.Core.Model;

public static class Roles {
	public const string Manager = "manager";
	public const string Specialist = "specialist";
}

public static class Lifetimes {
	public const string Persistent = "persistent";
	public const string Ephemeral = "ephemeral";
}

public static class AgentStates {
	public const string Active = "active";
	public const string Sleeping = "sleeping";
	public const string Working = "working";
	public const string Paused = "paused";
	public const string Retired = "retired";
	public const string Terminated = "terminated";
	public static bool IsLive(string s) => s is not (Retired or Terminated);
}

public static class CompactionModes {
	public const string Rolling = "rolling";
	public const string Full = "full";
}

public static class ObjectiveStates {
	public const string Planned = "planned";
	public const string Ready = "ready";
	public const string Active = "active";
	public const string Blocked = "blocked";
	public const string Verifying = "verifying";
	public const string Complete = "complete";
	public const string Canceled = "canceled";
	public static readonly string[] All = [Planned, Ready, Active, Blocked, Verifying, Complete, Canceled];
	public static bool IsOpen(string s) => s is not (Complete or Canceled);
}

public static class NotificationTypes {
	public const string Assignment = "assignment";
	public const string Consultation = "consultation";
	public const string Result = "result";
	public const string Decision = "decision";
	public const string Pause = "pause";
	public const string Resume = "resume";
	public const string Escalation = "escalation";
	public const string UserMessage = "user_message";
	public const string System = "system";
	public static readonly string[] AgentSendable = [Assignment, Result, Decision, Escalation];

	/// <summary>Whether a notification of this type wakes its recipient. Decision notices are background information.</summary>
	public static bool Wakes(string type) => type is not Decision;
}

public static class InvocationStates {
	public const string Queued = "queued";
	public const string Running = "running";
	public const string Succeeded = "succeeded";
	public const string Failed = "failed";
	public const string Canceled = "canceled";
	public const string Unknown = "unknown";
}

public sealed class Project {
	public string Id { get; set; } = "";
	public string Name { get; set; } = "";
	public string Description { get; set; } = "";
	public string? WorkspacePath { get; set; }
	public string? RootAgentId { get; set; }
	public string? RootObjectiveId { get; set; }
	public string State { get; set; } = "active";
	public long EffectiveBudgetNanos { get; set; }
	public long? CashCeilingNanos { get; set; }
	public long CreatedAt { get; set; }
	public long UpdatedAt { get; set; }
}

public sealed class Agent {
	public string Id { get; set; } = "";
	public string ProjectId { get; set; } = "";
	public string Name { get; set; } = "";
	public string Title { get; set; } = "";
	public string Role { get; set; } = Roles.Specialist;
	public string Lifetime { get; set; } = Lifetimes.Persistent;
	public string? ManagerId { get; set; }
	public string ModelId { get; set; } = "";
	public string? ReasoningEffort { get; set; }
	public string State { get; set; } = AgentStates.Sleeping;
	public string CompactionMode { get; set; } = CompactionModes.Rolling;
	public string Instructions { get; set; } = "";
	public string? TerminationCondition { get; set; }
	public string? PrimarySessionId { get; set; }
	public string? CreatedBy { get; set; }
	public long CreatedAt { get; set; }
	public long UpdatedAt { get; set; }
	public long? RetiredAt { get; set; }
}

public sealed class IdentityRevision {
	public string Id { get; set; } = "";
	public string AgentId { get; set; } = "";
	public int Revision { get; set; }
	public string Content { get; set; } = "";
	public string? AuthorAgentId { get; set; }
	public long CreatedAt { get; set; }
}

public sealed class Objective {
	public string Id { get; set; } = "";
	public string ProjectId { get; set; } = "";
	public string? ParentId { get; set; }
	public string? OwnerId { get; set; }
	public string? DelegatedById { get; set; }
	public string Title { get; set; } = "";
	public string Description { get; set; } = "";
	public string CompletionConditions { get; set; } = "";
	public string State { get; set; } = ObjectiveStates.Planned;
	public bool Required { get; set; } = true;
	public string Evidence { get; set; } = "[]";
	public int SortOrder { get; set; }
	public long CreatedAt { get; set; }
	public long UpdatedAt { get; set; }
}

public sealed class Session {
	public string Id { get; set; } = "";
	public string ProjectId { get; set; } = "";
	public string AgentId { get; set; } = "";
	public string Kind { get; set; } = "primary";
	public string State { get; set; } = "idle";
	public string ModelId { get; set; } = "";
	public string CompactionMode { get; set; } = CompactionModes.Rolling;
	public string? ParentSessionId { get; set; }
	public long? CheckpointSeq { get; set; }
	public int TurnCount { get; set; }
	public long NextSeq { get; set; } = 1;
	public int ContextRevision { get; set; }
	public double TokenRatio { get; set; } = 1.0;
	public string? Purpose { get; set; }
	public string? Result { get; set; }
	public long CreatedAt { get; set; }
	public long UpdatedAt { get; set; }
}

public static class ItemKinds {
	public const string User = "user";
	public const string Assistant = "assistant";
	public const string ToolResult = "tool_result";
	public const string Summary = "summary";
	public const string Notice = "notice";
}

public sealed class SessionItem {
	public string Id { get; set; } = "";
	public string SessionId { get; set; } = "";
	public long Seq { get; set; }
	public string Kind { get; set; } = "";
	public int Turn { get; set; }
	public string Payload { get; set; } = "{}";
	public int TokenEstimate { get; set; }
	public long CreatedAt { get; set; }
}

public sealed class Notification {
	public string Id { get; set; } = "";
	public string ProjectId { get; set; } = "";
	public string Type { get; set; } = "";
	public string? FromAgentId { get; set; }
	public string ToAgentId { get; set; } = "";
	public string? ObjectiveId { get; set; }
	public string Body { get; set; } = "";
	public bool Wakes { get; set; }
	public string State { get; set; } = "pending";
	public string? CausalParentId { get; set; }
	public string? DedupeKey { get; set; }
	public long CreatedAt { get; set; }
	public long? DeliveredAt { get; set; }
}

public sealed class ConversationEntry {
	public string Id { get; set; } = "";
	public string ProjectId { get; set; } = "";
	public string Author { get; set; } = "";
	public string? AgentId { get; set; }
	public string Body { get; set; } = "";
	public long CreatedAt { get; set; }
}

public sealed class ModelInfo {
	public string Id { get; set; } = "";
	public string Provider { get; set; } = "";
	public string UpstreamModel { get; set; } = "";
	public string DisplayName { get; set; } = "";
	public int? ContextTokens { get; set; }
	public int? MaxOutputTokens { get; set; }
	public string? InputPerMillion { get; set; }
	public string? CachedInputPerMillion { get; set; }
	public string? OutputPerMillion { get; set; }
	public string PriceProvenance { get; set; } = "";
	public string Billing { get; set; } = "api";
	public string Premium { get; set; } = "1";
	public bool Enabled { get; set; }
	public string Notes { get; set; } = "";

	public decimal? InputRate => InputPerMillion is null ? null : decimal.Parse(InputPerMillion, System.Globalization.CultureInfo.InvariantCulture);
	public decimal? CachedInputRate => CachedInputPerMillion is null ? null : decimal.Parse(CachedInputPerMillion, System.Globalization.CultureInfo.InvariantCulture);
	public decimal? OutputRate => OutputPerMillion is null ? null : decimal.Parse(OutputPerMillion, System.Globalization.CultureInfo.InvariantCulture);
	public decimal PremiumValue => decimal.Parse(Premium, System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class ToolInvocation {
	public string Id { get; set; } = "";
	public string ProjectId { get; set; } = "";
	public string SessionId { get; set; } = "";
	public string AgentId { get; set; } = "";
	public string CallId { get; set; } = "";
	public string ToolName { get; set; } = "";
	public string ToolVersion { get; set; } = "";
	public string Arguments { get; set; } = "{}";
	public string State { get; set; } = InvocationStates.Queued;
	public string? ResultArtifact { get; set; }
	public int? ResultChars { get; set; }
	public string? Error { get; set; }
	public string? ParentInvocationId { get; set; }
	public long? DeadlineAt { get; set; }
	public long? StartedAt { get; set; }
	public long? FinishedAt { get; set; }
	public long CreatedAt { get; set; }
}

public sealed class ModelRequestRecord {
	public string Id { get; set; } = "";
	public string ProjectId { get; set; } = "";
	public string? SessionId { get; set; }
	public string? AgentId { get; set; }
	public string? ObjectiveId { get; set; }
	public string Purpose { get; set; } = "turn";
	public string ModelId { get; set; } = "";
	public string Provider { get; set; } = "";
	public string UpstreamModel { get; set; } = "";
	public string State { get; set; } = "dispatched";
	public string Quote { get; set; } = "{}";
	public string? Usage { get; set; }
	public string? RequestArtifact { get; set; }
	public string? ResponseArtifact { get; set; }
	public string? Error { get; set; }
	public int? ContextRevision { get; set; }
	public long StartedAt { get; set; }
	public long? FinishedAt { get; set; }
}

public sealed class CostEvent {
	public string Id { get; set; } = "";
	public string ProjectId { get; set; } = "";
	public string? ObjectiveId { get; set; }
	public string? AgentId { get; set; }
	public string? SponsorAgentId { get; set; }
	public string? SessionId { get; set; }
	public string? ModelRequestId { get; set; }
	public string Category { get; set; } = "direct";
	public long? CashNanos { get; set; }
	public string CashBasis { get; set; } = "usage_priced";
	public long EffectiveNanos { get; set; }
	public string? Valuation { get; set; }
	public long CreatedAt { get; set; }
}

public sealed class KnowledgeRevision {
	public string Id { get; set; } = "";
	public string ProjectId { get; set; } = "";
	public string DocKey { get; set; } = "";
	public int Revision { get; set; }
	public string Kind { get; set; } = "observation";
	public string Title { get; set; } = "";
	public string Content { get; set; } = "";
	public string? AuthorAgentId { get; set; }
	public string Provenance { get; set; } = "";
	public string? ObjectiveId { get; set; }
	public string? ReplacesRevisionId { get; set; }
	public long CreatedAt { get; set; }
}
