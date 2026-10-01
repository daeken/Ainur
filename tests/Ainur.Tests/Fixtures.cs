using System.Collections.Concurrent;
using Ainur.Core;
using Ainur.Core.Providers;
using Ainur.Core.Runtime;

namespace Ainur.Tests;

/// <summary>Scripted provider for deterministic runloop tests. Each call is answered by the handler.</summary>
public sealed class FakeProvider(Func<ProviderRequest, int, ProviderResponse> handler, string id = "deepseek") : IModelProvider {
	public readonly ConcurrentQueue<ProviderRequest> Requests = new();
	int Count;
	public string Id => id;

	public Task<ProviderResponse> CompleteAsync(ProviderRequest request, Action<StreamDelta>? onDelta, CancellationToken ct) {
		Requests.Enqueue(request);
		var n = Interlocked.Increment(ref Count);
		var r = handler(request, n);
		r.Usage = r.Usage.InputTokens > 0 ? r.Usage : new Usage { InputTokens = 1000, OutputTokens = 100 };
		r.FinishReason ??= r.ToolCalls.Count > 0 ? "tool_calls" : "stop";
		return Task.FromResult(r);
	}

	public static ProviderResponse Text(string text) => new() { Content = text };
	public static ProviderResponse Call(string name, string args, string? id = null) => new() { ToolCalls = [new ToolCall(id ?? $"call_{Guid.NewGuid():N}"[..14], name, args)] };
}

public sealed class TempHome : IDisposable {
	public readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ainur-test-" + Guid.NewGuid().ToString("N")[..10]);
	public string Workspace => System.IO.Path.Combine(Path, "workspace");

	public TempHome() {
		Directory.CreateDirectory(Workspace);
	}

	public AinurRuntime Runtime(IModelProvider? provider = null, Action<RuntimeOptions>? configure = null, bool start = true) {
		var options = new RuntimeOptions { Home = Path };
		configure?.Invoke(options);
		ProviderRegistry? registry = null;
		if(provider is not null) {
			registry = new ProviderRegistry();
			registry.Register(provider);
		}
		var rt = new AinurRuntime(options, registry);
		if(start) rt.Start("test");
		return rt;
	}

	public void Dispose() {
		try { Directory.Delete(Path, true); } catch { }
	}
}

public static class Wait {
	public static async Task Until(Func<bool> condition, TimeSpan timeout, string what) {
		var deadline = DateTime.UtcNow + timeout;
		while(DateTime.UtcNow < deadline) {
			if(condition()) return;
			await Task.Delay(50);
		}
		if(!condition()) throw new TimeoutException($"Timed out waiting for {what}");
	}
}
