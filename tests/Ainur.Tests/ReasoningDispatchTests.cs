using System.Text.Json.Nodes;
using Ainur.Core;
using Ainur.Core.Providers;
using Ainur.Core.Model;
using Ainur.Core.Persistence;
using Ainur.Core.Runtime;
using Ainur.Core.Tools;

namespace Ainur.Tests;

/// <summary>Real gateway/provider calls with a fake provider. No external model calls.</summary>
public class ReasoningDispatchTests {
	static (TempHome home, AinurRuntime runtime, FakeProvider provider, string projectId) Setup() {
		var home = new TempHome();
		var provider = new FakeProvider((_, _) => FakeProvider.Text("{\"tools\":[\"powershell\"],\"why\":\"test\"}"));
		var runtime = home.Runtime(provider, o => o.AutoStartHosts = false);
		var project = runtime.CreateProject("T", "d", home.Workspace);
		return (home, runtime, provider, project.Id);
	}

	[Theory]
	[InlineData("none")]
	[InlineData("minimal")]
	[InlineData("low")]
	[InlineData("turbo")]
	[InlineData("")]
	public async Task GatewayRejectsExplicitInvalidEffortBeforeProviderOrReservation(string effort) {
		var (home, rt, provider, projectId) = Setup();
		using(home) using(rt) {
			var before = rt.Store.ModelRequestsInState("dispatched").Count;
			var error = await Assert.ThrowsAsync<DomainException>(() => rt.Gateway.CallAsync(Call(rt, projectId, effort), default));
			Assert.Contains("'medium' is the platform floor", error.Message);
			Assert.Empty(provider.Requests);
			Assert.Equal(before, rt.Store.ModelRequestsInState("dispatched").Count);
			Assert.Empty(rt.Store.ModelRequestsInState("failed"));
		}
	}

	[Theory]
	[InlineData(null, "high")]
	[InlineData("medium", "medium")]
	[InlineData("high", "high")]
	[InlineData("max", "max")]
	public async Task GatewayUsesDefaultOrExplicitPermittedEffortAtProvider(string? effort, string expected) {
		var (home, rt, provider, projectId) = Setup();
		using(home) using(rt) {
			await rt.Gateway.CallAsync(Call(rt, projectId, effort), default);
			Assert.Equal(expected, Assert.Single(provider.Requests).ReasoningEffort);
		}
	}

	[Fact]
	public async Task LegacyStoredLowCannotReachProviderAndExplicitRepairRestoresDispatch() {
		var (home, rt, provider, projectId) = Setup();
		using(home) using(rt) {
			var root = rt.Store.GetAgent(rt.Store.GetProject(projectId)!.RootAgentId!)!;
			rt.Db.Write(u => { root.ReasoningEffort = "low"; rt.Store.UpdateAgent(u, root); });
			var session = rt.Store.SessionsForAgent(root.Id).Single(s => s.Kind == "primary");
			// This is the exact effort and model that a legacy SessionHost will supply at the gateway boundary.
			var error = await Assert.ThrowsAsync<DomainException>(() => rt.Gateway.CallAsync(
				Call(rt, projectId, rt.Store.GetAgent(root.Id)!.ReasoningEffort), default));
			Assert.Contains("'medium' is the platform floor", error.Message);
			Assert.Empty(provider.Requests);
			Assert.Empty(rt.Store.ModelRequestsInState("dispatched"));
			rt.SetAgentModel(root.Id, root.Id, null, "high");
			await rt.Gateway.CallAsync(Call(rt, projectId, rt.Store.GetAgent(root.Id)!.ReasoningEffort), default);
			Assert.Equal("high", Assert.Single(provider.Requests).ReasoningEffort);
			Assert.Equal(session.Id, rt.Store.SessionsForAgent(root.Id).Single(s => s.Kind == "primary").Id);
		}
	}

	[Fact]
	public async Task LegacyLowPostUserMessageCannotReachProviderUntilExplicitRepair() {
		var (home, rt, provider, projectId) = Setup();
		using(home) using(rt) {
			var project = rt.Store.GetProject(projectId)!;
			var root = rt.Store.GetAgent(project.RootAgentId!)!;
			rt.Db.Write(u => { root.ReasoningEffort = "low"; rt.Store.UpdateAgent(u, root); });
			rt.PostUserMessage(projectId, "wake at legacy low");
			await Wait.Until(() => rt.Store.GetAgent(root.Id)!.State == AgentStates.Paused,
				TimeSpan.FromSeconds(15), "legacy effort must be refused by gateway");
			Assert.Empty(provider.Requests);
			Assert.Empty(rt.Store.ModelRequestsInState("dispatched"));
			Assert.Equal("low", rt.Store.GetAgent(root.Id)!.ReasoningEffort);
			rt.SetAgentModel(root.Id, root.Id, null, "high");
			await rt.Gateway.CallAsync(Call(rt, projectId, rt.Store.GetAgent(root.Id)!.ReasoningEffort), default);
			// The paused session is intentionally not automatically unpaused by model repair.
			Assert.Equal(AgentStates.Paused, rt.Store.GetAgent(root.Id)!.State);
			Assert.Equal("high", Assert.Single(provider.Requests).ReasoningEffort);
		}
	}

	[Fact]
	public async Task FindToolsActuallyDispatchesWithHighEffort() {
		var (home, rt, provider, projectId) = Setup();
		using(home) using(rt) {
			var project = rt.Store.GetProject(projectId)!;
			var agent = rt.Store.GetAgent(project.RootAgentId!)!;
			var session = rt.Store.SessionsForAgent(agent.Id).Single(s => s.Kind == "primary");
			var context = new ToolContext { Runtime = rt, Project = project, Agent = agent, Session = session,
				Host = rt.GetHost(session.Id)!, InvocationId = "test", CancellationToken = default };
			await new FindToolsTool().InvokeAsync(context, new JsonObject { ["need"] = "find tools for files" });
			var request = Assert.Single(provider.Requests);
			Assert.Equal("high", request.ReasoningEffort);
			Assert.Contains("You select tools from a registry", request.Messages[0].Content);
		}
	}

	static ModelCall Call(AinurRuntime rt, string projectId, string? effort) => new() {
		ProjectId = projectId, Purpose = "test", Category = "test", Model = rt.Store.GetModel(rt.Options.CheapModelId)!,
		Messages = [ChatMessage.User("hello")], ReasoningEffort = effort,
	};
}
