using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Ainur.Core;
using Ainur.Core.Accounting;
using Ainur.Core.Model;
using Ainur.Core.Persistence;
using Ainur.Core.Providers;
using Ainur.Core.Runtime;
using Ainur.Core.Tools;
using Ainur.Server;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ainur.Tests;

public class BudgetControlTests {
	static ModelInfo Model(string billing = "api", bool priced = true) => new() {
		Id = "budget-model", Provider = "deepseek", UpstreamModel = "budget-model", Enabled = true, Billing = billing,
		InputPerMillion = priced ? "1" : null, OutputPerMillion = priced ? "1" : null,
	};
	static ModelRequestRecord Reserve(AinurRuntime rt, Project p, ModelInfo m, long tokens = 600_000) {
		var r = new ModelRequestRecord { Id = Ids.New("req"), ProjectId = p.Id, ModelId = m.Id, Provider = m.Provider, Quote = JsonUtil.Serialize(Pricing.Quote(m, tokens, 0)) };
		rt.Db.Write(u => { rt.Store.InsertModelRequest(u, r); rt.Ledger.Reserve(u, p.Id, r.Id, Pricing.Quote(m, tokens, 0)); });
		return r;
	}

	[Theory]
	[InlineData(false, -1)] [InlineData(false, 0)] [InlineData(false, 1)]
	[InlineData(true, -1)] [InlineData(true, 0)] [InlineData(true, 1)]
	public void EffectiveAndCashLimitsAreIndependent(bool unlimited, int cash) {
		using var home = new TempHome();
		using var rt = home.Runtime(new FakeProvider((_, _) => FakeProvider.Text("unused")), start: false);
		var p = rt.CreateProject("limits", "", home.Workspace, budgetDollars: unlimited ? 0 : 0.5m, cashCeilingDollars: cash < 0 ? null : cash);
		if(!unlimited || cash == 0) Assert.Throws<BudgetExhaustedException>(() => Reserve(rt, p, Model()));
		else Reserve(rt, p, Model());
		Assert.Equal(unlimited, rt.Ledger.Summary(p.Id).NoEffectiveLimit);
		if(unlimited) Reserve(rt, p, Model("subscription")); // zero marginal cash remains independent
	}

	[Fact]
	public async Task ConcurrentReservationsCannotOverbookCashAndFailedReservationRollsBack() {
		using var home = new TempHome();
		using var rt = home.Runtime(new FakeProvider((_, _) => FakeProvider.Text("unused")), start: false);
		var p = rt.CreateProject("limits", "", home.Workspace, noEffectiveLimit: true, cashCeilingDollars: 1);
		var admitted = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => {
			try { Reserve(rt, p, Model()); return true; } catch(BudgetExhaustedException) { return false; }
		})));
		Assert.Single(admitted, x => x);
		Assert.Single(rt.Store.ModelRequestsInState("dispatched"));
		Assert.Equal(Money.FromDollars(0.6m), rt.Ledger.Summary(p.Id).ReservedCashNanos);
		Assert.Equal(Money.FromDollars(0.4m), rt.Ledger.Summary(p.Id).CashRemainingNanos);
	}

	[Theory]
	[InlineData(true, 0)] [InlineData(true, 10)] [InlineData(false, 0)]
	public async Task FallbackCannotBypassCashAdmission(bool unknownPrice, int cashCap) {
		using var home = new TempHome();
		var provider = new ScriptedGatewayProvider("deepseek", (_, _) => throw ScriptedGatewayProvider.Auth(), (_, _) => ScriptedGatewayProvider.Ok());
		using var rt = home.Runtime(provider, start: false);
		var p = rt.CreateProject("limits", "", home.Workspace, noEffectiveLimit: true, cashCeilingDollars: cashCap);
		var primary = Model("subscription"); primary.Id = "primary"; primary.FallbackModelId = "fallback";
		var fallback = Model(priced: !unknownPrice); fallback.Id = "fallback";
		rt.Db.Write(u => { rt.Store.UpsertModel(u, primary); rt.Store.UpsertModel(u, fallback); });
		await Assert.ThrowsAsync<BudgetExhaustedException>(() => rt.Gateway.CallAsync(new ModelCall {
			ProjectId = p.Id, Purpose = "test", Category = "direct", Model = primary, Messages = [ChatMessage.User("test")],
		}, default));
		Assert.Equal(1, provider.Calls);
		Assert.Single(rt.Store.ModelRequestsInState("failed"));
		Assert.Empty(rt.Store.CostEvents(p.Id));
		Assert.Equal(0, rt.Ledger.Summary(p.Id).ReservedCashNanos);
		Assert.Empty(rt.Store.ModelRequestsInState("dispatched"));
	}

	[Fact]
	public async Task UnlimitedRetainsAttributionUnknownCashAndHonestPresentation() {
		using var home = new TempHome();
		using var rt = home.Runtime(new FakeProvider((_, _) => FakeProvider.Text("unused")), start: false);
		var p = rt.CreateProject("limits", "", home.Workspace, noEffectiveLimit: true);
		var agent = rt.Store.GetAgent(p.RootAgentId!)!;
		var session = rt.Store.SessionsForAgent(agent.Id).Single();
		var m = Model(priced: false);
		var r = Reserve(rt, p, m);
		r.AgentId = agent.Id; r.ObjectiveId = p.RootObjectiveId; r.SessionId = session.Id;
		rt.Db.Write(u => rt.Ledger.Settle(u, r, Pricing.Settle(Pricing.Quote(m, 100, 0), new Usage { InputTokens = 100 }), "direct", agent.Id));
		var cost = Assert.Single(rt.Store.CostEvents(p.Id));
		Assert.Equal(agent.Id, cost.AgentId); Assert.Equal(agent.Id, cost.SponsorAgentId);
		Assert.Equal(p.RootObjectiveId, cost.ObjectiveId); Assert.Equal(session.Id, cost.SessionId);
		Assert.Equal(cost.EffectiveNanos, rt.Ledger.ByAgent(p.Id)[agent.Id].Direct);
		Assert.Null(cost.CashNanos); Assert.True(cost.EffectiveNanos > 0);
		rt.UpdateProjectBudget(p.Id, cashCeilingDollars: 10);
		Assert.Throws<BudgetExhaustedException>(() => Reserve(rt, p, Model()));
		Reserve(rt, p, Model("subscription"));
		var summary = rt.Ledger.Summary(p.Id);
		Assert.Null(summary.EffectiveLimitNanos); Assert.Null(summary.EffectiveRemainingNanos);
		Assert.Null(summary.CashRemainingNanos); Assert.Equal("unknown_cost", summary.CashRemainingStatus);
		var prompt = Prompts.System(rt, agent, session, null);
		Assert.Contains("No effective limit", prompt); Assert.Contains("unknown, not zero", prompt);
		Assert.DoesNotContain("0%", prompt);
		var tool = await new CostsTool().InvokeAsync(new ToolContext { Runtime = rt, Project = p, Agent = agent,
			Session = session, Host = null!, InvocationId = "test", CancellationToken = default }, new JsonObject { ["include_models"] = false });
		Assert.Contains("No effective limit", tool.Text); Assert.Contains("1 unknown", tool.Text); Assert.Contains("reserved", tool.Text);
	}

	[Fact]
	public void EnablingCashCapWithUnknownInFlightRequestFailsClosedUntilReleased() {
		using var home = new TempHome();
		using var rt = home.Runtime(new FakeProvider((_, _) => FakeProvider.Text("unused")), start: false);
		var p = rt.CreateProject("limits", "", home.Workspace, noEffectiveLimit: true);
		var unknown = Reserve(rt, p, Model(priced: false));
		Assert.Equal(1, rt.Ledger.Summary(p.Id).ReservedCashUnknownCount);
		rt.UpdateProjectBudget(p.Id, cashCeilingDollars: 10);
		Assert.Throws<BudgetExhaustedException>(() => Reserve(rt, p, Model()));
		Reserve(rt, p, Model("subscription"));
		Assert.Null(rt.Ledger.Summary(p.Id).CashRemainingNanos);
		Assert.Equal("unknown_cost", rt.Ledger.Summary(p.Id).CashRemainingStatus);
		rt.Db.Write(u => rt.Ledger.Release(u, unknown.Id));
		Reserve(rt, p, Model());
	}

	[Fact]
	public void SettlementOverrunRemainsVisibleRatherThanClamped() {
		using var home = new TempHome();
		using var rt = home.Runtime(new FakeProvider((_, _) => FakeProvider.Text("unused")), start: false);
		var p = rt.CreateProject("limits", "", home.Workspace, noEffectiveLimit: true, cashCeilingDollars: 1);
		var m = Model(); var r = Reserve(rt, p, m);
		rt.Db.Write(u => rt.Ledger.Settle(u, r, Pricing.Settle(Pricing.Quote(m, 600_000, 0), new Usage { InputTokens = 2_000_000 }), "direct", null));
		Assert.Equal(-Money.FromDollars(1), rt.Ledger.Summary(p.Id).CashRemainingNanos);
		Assert.Throws<BudgetExhaustedException>(() => Reserve(rt, p, m));
	}

	[Fact]
	public async Task HttpBudgetContractDefaultsClearsAndInvalidAmountsAreAtomic() {
		using var home = new TempHome();
		using var rt = home.Runtime(new FakeProvider((_, _) => FakeProvider.Text("unused")), start: false);
		var builder = WebApplication.CreateBuilder();
		builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
		builder.Services.AddSingleton(rt); builder.Services.AddSingleton(new ServerOptions());
		builder.Services.AddSingleton<EventHub>();
		builder.Services.ConfigureHttpJsonOptions(o => {
			o.SerializerOptions.PropertyNamingPolicy = JsonUtil.Options.PropertyNamingPolicy;
			o.SerializerOptions.DefaultIgnoreCondition = JsonUtil.Options.DefaultIgnoreCondition;
			foreach(var converter in JsonUtil.Options.Converters) o.SerializerOptions.Converters.Add(converter);
		});
		await using var app = builder.Build(); Api.Map(app); await app.StartAsync();
		using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
		async Task<HttpResponseMessage> Send(string method, string path, string body) => await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/api/v1" + path) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
		var create = await Send("POST", "/projects", """{"name":"legacy"}"""); create.EnsureSuccessStatusCode();
		var json = JsonNode.Parse(await create.Content.ReadAsStringAsync())!;
		var id = json["id"]!.GetValue<string>();
		Assert.False(json["no_effective_limit"]!.GetValue<bool>());
		Assert.Equal(Money.FromDollars(5), json["effective_limit_nanos"]!.GetValue<long>());
		var path = "/projects/" + id;
		async Task AssertEffectiveContract(long? limit, decimal? remaining) {
			foreach(var (url, key) in new[] { (path, "costs"), (path + "/costs", "summary") }) {
				var response = await client.GetAsync("/api/v1" + url); response.EnsureSuccessStatusCode();
				var summary = JsonNode.Parse(await response.Content.ReadAsStringAsync())![key]!.AsObject();
				Assert.True(summary.ContainsKey("effective_limit_nanos"), summary.ToJsonString());
				Assert.True(summary.ContainsKey("effective_remaining_nanos"), summary.ToJsonString());
				Assert.Equal(limit, summary["effective_limit_nanos"]?.GetValue<long>());
				Assert.Equal(remaining, summary["effective_remaining_nanos"]?.GetValue<decimal>());
			}
		}
		var project = rt.Store.GetProject(id)!;
		var priced = Model();
		var settled = Reserve(rt, project, priced);
		rt.Db.Write(u => rt.Ledger.Settle(u, settled, Pricing.Settle(Pricing.Quote(priced, 600_000, 0), new Usage { InputTokens = 600_000 }), "direct", null));
		Reserve(rt, project, priced);
		await AssertEffectiveContract(Money.FromDollars(5), Money.FromDollars(3.8m));
		(await Send("PATCH", path, """{"no_effective_limit":true,"cash_ceiling_dollars":0}""")).EnsureSuccessStatusCode();
		(await Send("PATCH", path, """{"budget_dollars":null,"cash_ceiling_dollars":null}""")).EnsureSuccessStatusCode();
		Assert.Equal(0, rt.Store.GetProject(id)!.EffectiveBudgetNanos); Assert.Equal(0, rt.Store.GetProject(id)!.CashCeilingNanos);
		await AssertEffectiveContract(null, null);
		foreach(var body in new[] {
			"""{"budget_dollars":-1,"description":"bad"}""", """{"budget_dollars":1e100}""", """{"cash_ceiling_dollars":9223372037}""",
			"""{"budget_dollars":"NaN"}""", """{"budget_dollars":NaN}""", """{"budget_dollars":0.0000000001}""",
			"""{"no_effective_limit":false}""", """{"no_effective_limit":true,"budget_dollars":3}""",
			"""{"clear_cash_ceiling":true,"cash_ceiling_dollars":5}""", """{"budget_dollars":5,"cash_ceiling_dollars":-1}""",
		}) {
			var before = JsonUtil.Serialize(rt.Store.GetProject(id));
			Assert.Equal(HttpStatusCode.BadRequest, (await Send("PATCH", path, body)).StatusCode);
			Assert.Equal(before, JsonUtil.Serialize(rt.Store.GetProject(id)));
		}
		(await Send("PATCH", path, """{"no_effective_limit":false,"budget_dollars":2,"clear_cash_ceiling":true}""")).EnsureSuccessStatusCode();
		Assert.Null(rt.Store.GetProject(id)!.CashCeilingNanos); Assert.Equal(Money.FromDollars(2), rt.Store.GetProject(id)!.EffectiveBudgetNanos);
		foreach(var body in new[] { """{"name":"explicit","no_effective_limit":true,"cash_ceiling_dollars":0}""", """{"name":"zero","budget_dollars":0}""" }) {
			var response = await Send("POST", "/projects", body); response.EnsureSuccessStatusCode();
			Assert.True(JsonNode.Parse(await response.Content.ReadAsStringAsync())!["no_effective_limit"]!.GetValue<bool>());
		}
		var count = rt.Store.ListProjects().Count;
		Assert.Equal(HttpStatusCode.BadRequest, (await Send("POST", "/projects", """{"name":"bad","budget_dollars":-1}""")).StatusCode);
		Assert.Equal(count, rt.Store.ListProjects().Count);
		await app.StopAsync();
	}
}
