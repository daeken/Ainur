namespace Ainur.Core.Runtime;

/// <summary>
/// Session-owned live .NET objects retained across model turns. The model sees typed handles; handles record the
/// runtime generation so they can never silently resolve to a different value after a restart.
/// </summary>
public sealed class ObjectRegistry(string sessionId, long generation) {
	public sealed record Entry(string Handle, object Value, string TypeName, string? ToolVersion, string Summary, long CreatedAt);

	readonly Dictionary<string, Entry> Entries = new();
	int Counter;

	public Entry Put(object value, string? toolVersion, string? summary = null) {
		var handle = $"obj:{sessionId[^8..]}:g{generation}:{++Counter}";
		var entry = new Entry(handle, value, value.GetType().FullName ?? value.GetType().Name, toolVersion, summary ?? Describe(value), Clock.Now);
		Entries[handle] = entry;
		return entry;
	}

	public object Get(string handle) {
		if(Entries.TryGetValue(handle, out var e)) return e.Value;
		var parts = handle.Split(':');
		if(parts.Length == 4 && parts[2] != $"g{generation}")
			throw new Tools.ToolException($"Handle {handle} belongs to runtime generation {parts[2][1..]}, but this is generation {generation}. Live objects do not survive restarts; rebuild it from durable sources.");
		throw new Tools.ToolException($"Unknown or released handle {handle}");
	}

	public bool Release(string handle) => Entries.Remove(handle);
	public IReadOnlyCollection<Entry> List() => Entries.Values;

	public static string Describe(object value) => value switch {
		string s => $"string ({s.Length} chars)",
		System.Collections.ICollection c => $"{value.GetType().Name} with {c.Count} items",
		_ => value.GetType().Name,
	};

	/// <summary>Retain only values that carry more than their text rendering: not scalars, and not plain lists of scalars.</summary>
	public static bool ShouldRegister(object? value) => value switch {
		null => false,
		string or decimal => false,
		_ when value.GetType().IsPrimitive => false,
		System.Collections.IEnumerable e => e.Cast<object?>().Any(x => x is not null && x is not string && !x.GetType().IsPrimitive && x is not decimal),
		_ => true,
	};
}
