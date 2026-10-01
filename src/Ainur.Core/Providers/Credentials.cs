using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Ainur.Core.Providers;

/// <summary>
/// Resolves provider secrets from host-managed sources: environment variables, the macOS keychain, or the
/// keychain/env names declared in a FlatlineProxy configuration. Secrets are never written to Ainur's database.
/// </summary>
public static class Credentials {
	public sealed record Source(string? EnvVar, string? KeychainService, string? KeychainAccount);

	static readonly Dictionary<string, Source> Defaults = new(StringComparer.OrdinalIgnoreCase) {
		["deepseek"] = new("DEEPSEEK_API_KEY", "ai.deepseek.api", "FlatlineProxy"),
		["openai"] = new("OPENAI_API_KEY", "ai.openai.api", null),
		["anthropic"] = new("ANTHROPIC_API_KEY", null, null),
		["xai"] = new("XAI_API_KEY", null, null),
		["zai"] = new("ZAI_API_KEY", "ai.z.api", "FlatlineProxy"),
	};

	public static string? FlatlineConfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "projects", "FlatlineProxy", "flatline.json");

	public static string? Resolve(string providerId) {
		foreach(var source in Sources(providerId)) {
			if(source.EnvVar is not null && Environment.GetEnvironmentVariable(source.EnvVar) is { Length: > 0 } env)
				return env.Trim();
			if(source.KeychainService is not null && ReadKeychain(source.KeychainService, source.KeychainAccount) is { Length: > 0 } secret)
				return secret;
		}
		return null;
	}

	static IEnumerable<Source> Sources(string providerId) {
		if(Defaults.TryGetValue(providerId, out var d)) yield return d;
		if(FlatlineConfigPath is not null && File.Exists(FlatlineConfigPath)) {
			JsonNode? config = null;
			try { config = JsonNode.Parse(File.ReadAllText(FlatlineConfigPath)); } catch { }
			if(config?["providers"] is JsonArray providers)
				foreach(var p in providers)
					if(p?["id"]?.GetValue<string>() is { } id && id.Equals(providerId, StringComparison.OrdinalIgnoreCase))
						yield return new(p["api_key_env"]?.GetValue<string>(), p["keychain_service"]?.GetValue<string>(), p["keychain_account"]?.GetValue<string>());
		}
	}

	static string? ReadKeychain(string service, string? account) {
		if(!OperatingSystem.IsMacOS()) return null;
		try {
			var psi = new ProcessStartInfo("/usr/bin/security") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
			psi.ArgumentList.Add("find-generic-password");
			psi.ArgumentList.Add("-s"); psi.ArgumentList.Add(service);
			if(account is not null) { psi.ArgumentList.Add("-a"); psi.ArgumentList.Add(account); }
			psi.ArgumentList.Add("-w");
			using var proc = Process.Start(psi)!;
			var output = proc.StandardOutput.ReadToEnd();
			proc.WaitForExit(10_000);
			return proc.ExitCode == 0 ? output.Trim() : null;
		} catch {
			return null;
		}
	}
}
