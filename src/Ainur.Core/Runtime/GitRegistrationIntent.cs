namespace Ainur.Core.Runtime;

/// <summary>Caller identity and exact durable destination fences; no credentials or raw Git diagnostics.</summary>
public sealed class GitRegistrationIntent {
	public string Id { get; set; } = "";
	public string ProjectId { get; set; } = "";
	public string ActorId { get; set; } = "";
	public string RequestedPath { get; set; } = "";
	public string IntegrationBranch { get; set; } = "";
	public string OwnerId { get; set; } = "";
	public string? Remote { get; set; }
	public string? RemoteBranch { get; set; }
	public string State { get; set; } = "";
	public string? RepositoryId { get; set; }
	public string? CanonicalPath { get; set; }
	public string? CommonDir { get; set; }
	public string? FetchEndpointHash { get; set; }
	public string? PushEndpointHash { get; set; }
	public long CreatedAt { get; set; }
	public long UpdatedAt { get; set; }
}
