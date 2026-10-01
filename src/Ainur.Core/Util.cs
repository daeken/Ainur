using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ainur.Core;

/// <summary>Stable, time-sortable identifiers with a readable type prefix.</summary>
public static class Ids {
	public static string New(string prefix) => $"{prefix}_{Guid.CreateVersion7():N}";
}

public static class Clock {
	public static Func<long> NowMs = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
	public static long Now => NowMs();
}

public static class JsonUtil {
	public static readonly JsonSerializerOptions Options = new() {
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		WriteIndented = false,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
	};

	public static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };

	public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
	public static T? Deserialize<T>(string? json) => string.IsNullOrEmpty(json) ? default : JsonSerializer.Deserialize<T>(json, Options);
	public static JsonNode? Parse(string? json) => string.IsNullOrEmpty(json) ? null : JsonNode.Parse(json);
}

public static class Hash {
	public static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
	public static string Sha256(string data) => Sha256(Encoding.UTF8.GetBytes(data));
}

/// <summary>Monetary amounts are stored as integer nanodollars to keep fixed precision for tiny per-request charges.</summary>
public static class Money {
	public const long NanosPerDollar = 1_000_000_000;
	public static long FromDollars(decimal dollars) => (long) decimal.Round(dollars * NanosPerDollar, MidpointRounding.AwayFromZero);
	public static decimal ToDollars(long nanos) => (decimal) nanos / NanosPerDollar;
	public static string Format(long nanos) => ToDollars(nanos) switch {
		var d when Math.Abs(d) >= 1m => $"${d:0.00}",
		var d when Math.Abs(d) >= 0.01m => $"${d:0.0000}",
		var d => $"${d:0.000000}",
	};

	/// <summary>Cost of <paramref name="tokens"/> at a per-million-token USD rate, rounded up so estimates stay conservative.</summary>
	public static long ForTokens(long tokens, decimal perMillion) =>
		(long) decimal.Ceiling(tokens * perMillion * NanosPerDollar / 1_000_000m);
}

public static class Tokens {
	/// <summary>Rough provider-independent token estimate. Calibrated per session against reported usage.</summary>
	public static int Estimate(string? text) => string.IsNullOrEmpty(text) ? 0 : (int) Math.Ceiling(Encoding.UTF8.GetByteCount(text) / 3.6) + 1;
}

public static class TextUtil {
	public static string Truncate(string text, int maxChars, string marker = "…") =>
		text.Length <= maxChars ? text : text[..Math.Max(0, maxChars - marker.Length)] + marker;

	/// <summary>Head and tail preview that states how much was omitted.</summary>
	public static string Preview(string text, int maxChars) {
		if(text.Length <= maxChars) return text;
		var head = maxChars * 2 / 3;
		var tail = maxChars - head;
		return $"{text[..head]}\n…[{text.Length - head - tail} characters omitted]…\n{text[^tail..]}";
	}
}
