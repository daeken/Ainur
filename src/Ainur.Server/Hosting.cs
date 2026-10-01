using System.Text.Json;
using System.Threading.Channels;
using Ainur.Core;
using Ainur.Core.Persistence;
using Ainur.Core.Providers;

namespace Ainur.Server;

public sealed class ServerOptions {
	public string Home { get; set; } = Ainur.Core.Runtime.RuntimeOptions.DefaultHome();
	public int Port { get; set; } = 5180;
	public string? Release { get; set; }
	public int DrainSeconds { get; set; } = 60;

	public static ServerOptions FromArgs(string[] args) {
		var o = new ServerOptions {
			Port = int.TryParse(Environment.GetEnvironmentVariable("AINUR_PORT"), out var p) ? p : 5180,
			Release = Environment.GetEnvironmentVariable("AINUR_RELEASE"),
		};
		for(var i = 0; i < args.Length - 1; i++)
			switch(args[i]) {
				case "--home": o.Home = Path.GetFullPath(args[++i]); break;
				case "--port": o.Port = int.Parse(args[++i]); break;
				case "--release": o.Release = args[++i]; break;
			}
		return o;
	}
}

/// <summary>Exclusive OS file lock guaranteeing a single active scheduler per home directory.</summary>
public sealed class SchedulerLock : IDisposable {
	readonly FileStream Stream;
	SchedulerLock(FileStream stream) => Stream = stream;

	public static SchedulerLock? TryAcquire(string path) {
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		try {
			var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
			fs.SetLength(0);
			var bytes = System.Text.Encoding.UTF8.GetBytes($"{Environment.ProcessId}\n");
			fs.Write(bytes);
			fs.Flush();
			return new SchedulerLock(fs);
		} catch(IOException) {
			return null;
		}
	}

	public void Dispose() => Stream.Dispose();
}

/// <summary>Binds to loopback only, rejects foreign Host headers (DNS rebinding) and cross-origin writes.</summary>
public sealed class LocalOnlyMiddleware(RequestDelegate next) {
	static readonly HashSet<string> Hosts = ["localhost", "127.0.0.1", "[::1]"];

	public async Task InvokeAsync(HttpContext ctx) {
		if(!Hosts.Contains(ctx.Request.Host.Host)) {
			ctx.Response.StatusCode = 421;
			await ctx.Response.WriteAsync("Ainur only serves loopback hosts.");
			return;
		}
		if(!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method)) {
			var origin = ctx.Request.Headers.Origin.ToString();
			if(origin.Length > 0 && !(Uri.TryCreate(origin, UriKind.Absolute, out var o) && Hosts.Contains(o.Host) && o.Port == ctx.Request.Host.Port)) {
				ctx.Response.StatusCode = 403;
				await ctx.Response.WriteAsync("Cross-origin writes are not allowed.");
				return;
			}
			if(ctx.Request.Headers["X-Ainur"] != "1") {
				ctx.Response.StatusCode = 403;
				await ctx.Response.WriteAsync("Missing X-Ainur header.");
				return;
			}
		}
		await next(ctx);
	}
}

/// <summary>Fans committed journal events and streaming model deltas out to server-sent-event subscribers.</summary>
public sealed class EventHub {
	readonly Lock Gate = new();
	readonly List<Channel<string>> Subscribers = [];

	public ChannelReader<string> Subscribe(CancellationToken ct) {
		var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest });
		lock(Gate) Subscribers.Add(channel);
		ct.Register(() => {
			lock(Gate) Subscribers.Remove(channel);
			channel.Writer.TryComplete();
		});
		return channel.Reader;
	}

	void Broadcast(string message) {
		lock(Gate)
			foreach(var s in Subscribers) s.Writer.TryWrite(message);
	}

	public void Publish(IReadOnlyList<JournalEvent> events) {
		foreach(var e in events)
			Broadcast($"event: journal\ndata: {JsonSerializer.Serialize(e, JsonUtil.Options)}\n\n");
	}

	public void PublishDelta(string sessionId, StreamDelta delta) =>
		Broadcast($"event: delta\ndata: {JsonSerializer.Serialize(new { session_id = sessionId, kind = delta.Kind, text = delta.Text }, JsonUtil.Options)}\n\n");
}
