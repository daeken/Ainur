using System.Collections.Concurrent;

namespace Ainur.Core.Runtime;

/// <summary>
/// Publishes in-flight inline invocations and their deadlines to a file the supervisor watches, written before
/// tool code is entered, so a stuck agent thread cannot disable the last-resort restart path.
/// </summary>
public sealed class Watchdog(string path) {
	public sealed record Entry(string InvocationId, string SessionId, string Tool, string ToolVersion, long DeadlineAt, long StartedAt);

	readonly ConcurrentDictionary<string, Entry> InFlight = new();
	readonly Lock WriteGate = new();
	public string Path => path;

	public void Enter(Entry entry) {
		InFlight[entry.InvocationId] = entry;
		Flush();
	}

	public void Exit(string invocationId) {
		if(InFlight.TryRemove(invocationId, out _)) Flush();
	}

	public IReadOnlyCollection<Entry> Current => InFlight.Values.ToList();

	void Flush() {
		lock(WriteGate) {
			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
			var tmp = path + ".tmp";
			File.WriteAllText(tmp, Json.Serialize(new { pid = Environment.ProcessId, updated_at = Clock.Now, in_flight = InFlight.Values.ToList() }));
			File.Move(tmp, path, overwrite: true);
		}
	}
}
