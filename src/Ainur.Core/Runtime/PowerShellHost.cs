using System.Collections;
using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using System.Text.Json.Nodes;
using Ainur.Core.Tools;

namespace Ainur.Core.Runtime;

/// <summary>
/// Session-owned embedded PowerShell runspace. Pipelines run on the session's dispatcher thread, and every tool
/// available to the calling Ainu is exposed as a function (plus Invoke-AinurTool) that returns live .NET objects.
/// </summary>
public sealed class PowerShellHost : IDisposable {
	readonly SessionHost Host;
	Runspace? Runspace;
	string? FunctionStamp;
	int Depth;

	public PowerShellHost(SessionHost host) => Host = host;

	public sealed record RunResult(string Text, bool HadErrors, object? LastValue);

	public RunResult Run(string script, TimeSpan timeout, CancellationToken ct) {
		EnsureOpen();
		// A tool invoked from inside a running pipeline (e.g. a script tool called by a script) runs as a nested pipeline.
		var nested = Depth > 0;
		if(!nested) RefreshFunctions();
		using var ps = nested ? PowerShell.Create(RunspaceMode.CurrentRunspace) : PowerShell.Create();
		if(!nested) ps.Runspace = Runspace;
		Depth++;
		try {
			return Execute(ps, script, timeout, ct, nested);
		} finally {
			Depth--;
		}
	}

	RunResult Execute(PowerShell ps, string script, TimeSpan timeout, CancellationToken ct, bool nested) {
		ps.AddScript("$global:LASTEXITCODE = 0; $global:AinurLast = @(. { " + script + "\n}); $global:AinurLast | Out-String -Width 200 -Stream");
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		timeoutCts.CancelAfter(timeout);
		using var reg = timeoutCts.Token.Register(() => {
			try { ps.BeginStop(null, null); } catch { }
		});
		var sb = new StringBuilder();
		Collection<PSObject> output;
		var stopped = false;
		try {
			output = ps.Invoke();
		} catch(PipelineStoppedException) {
			output = [];
			stopped = true;
		} catch(RuntimeException e) {
			output = [];
			sb.Append("EXCEPTION: ").Append(e.ErrorRecord?.ToString() ?? e.Message).Append('\n');
			if(e.ErrorRecord?.InvocationInfo?.PositionMessage is { } pos) sb.Append(pos).Append('\n');
		}
		foreach(var line in output) sb.Append(line?.ToString()).Append('\n');
		foreach(var info in ps.Streams.Information) sb.Append(info.MessageData).Append('\n');
		foreach(var w in ps.Streams.Warning) sb.Append("WARNING: ").Append(w).Append('\n');
		var hadErrors = ps.HadErrors || stopped;
		foreach(var err in ps.Streams.Error) {
			sb.Append("ERROR: ").Append(err).Append('\n');
			if(err.InvocationInfo?.PositionMessage is { Length: > 0 } pos && err.FullyQualifiedErrorId != "NativeCommandError") sb.Append(pos).Append('\n');
		}
		if(stopped) sb.Append(ct.IsCancellationRequested ? "[pipeline canceled]\n" : $"[pipeline stopped after timeout of {timeout.TotalSeconds:0}s]\n");
		object? exitCode = null;
		try { exitCode = nested ? null : Runspace!.SessionStateProxy.GetVariable("LASTEXITCODE"); } catch(PSInvalidOperationException) { }
		if(exitCode is int code && code != 0) sb.Append($"[last native exit code: {code}]\n");
		object[]? last = null;
		try { last = Runspace!.SessionStateProxy.GetVariable("AinurLast") as object[]; } catch(PSInvalidOperationException) { }
		object? lastValue = last is { Length: 1 } ? Unwrap(last[0]) : last is { Length: > 1 } ? last.Select(Unwrap).ToList() : null;
		var text = sb.ToString().TrimEnd();
		return new(text.Length == 0 ? "[no output]" : text, hadErrors, lastValue);
	}

	void EnsureOpen() {
		if(Runspace is not null) return;
		var iss = InitialSessionState.CreateDefault2();
		if(OperatingSystem.IsWindows()) iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
		Runspace = RunspaceFactory.CreateRunspace(iss);
		// Run pipelines on the caller (the session dispatcher) so object ownership stays with the session thread.
		Runspace.ThreadOptions = PSThreadOptions.UseCurrentThread;
		Runspace.Open();
		Runspace.SessionStateProxy.SetVariable("Ainur", new Bridge(Host));
		Runspace.SessionStateProxy.Path.SetLocation(WildcardPattern.Escape(Host.Workspace));
		using var ps = PowerShell.Create();
		ps.Runspace = Runspace;
		ps.AddScript("""
			function Invoke-AinurTool {
				param([Parameter(Mandatory)][string]$Name, [object]$Arguments = @{})
				$Ainur.Invoke($Name, $Arguments)
			}
			function Get-AinurTool { $Ainur.ToolNames() }
			function Get-AinurObject { param([Parameter(Mandatory)][string]$Handle) $Ainur.Get($Handle) }
			function Save-AinurObject { param([Parameter(Mandatory, ValueFromPipeline)][object]$Value, [string]$Summary) process { $Ainur.Put($Value, $Summary) } }
			$ProgressPreference = 'SilentlyContinue'
			""");
		ps.Invoke();
	}

	/// <summary>Regenerates one function per available tool when the tool set changes.</summary>
	void RefreshFunctions() {
		var tools = Host.AvailableTools();
		var stamp = string.Join("|", tools.Select(t => t.Version));
		if(stamp == FunctionStamp) return;
		var sb = new StringBuilder();
		foreach(var tool in tools) {
			var props = tool.InputSchema["properties"] as JsonObject ?? [];
			var parameters = props.Where(p => IsIdentifier(p.Key)).Select(p => $"{PsType(p.Value as JsonObject)}${p.Key}");
			var help = TextUtil.Truncate(tool.Description.Replace("#>", "#​>").Trim(), 400);
			sb.Append($$"""
				function global:{{tool.Name}} {
					<# .SYNOPSIS
					{{help}} #>
					[CmdletBinding()] param({{string.Join(", ", parameters)}})
					$Ainur.Invoke('{{tool.Name}}', $PSBoundParameters)
				}

				""");
		}
		using var ps = PowerShell.Create();
		ps.Runspace = Runspace;
		ps.AddScript(sb.ToString());
		ps.Invoke();
		FunctionStamp = stamp;
	}

	static bool IsIdentifier(string s) => s.Length > 0 && s.All(c => char.IsLetterOrDigit(c) || c == '_') && !char.IsDigit(s[0]);

	static string PsType(JsonObject? schema) => schema?["type"]?.GetValue<string>() switch {
		"string" => "[string]",
		"integer" => "[long]",
		"number" => "[double]",
		"boolean" => "[bool]",
		_ => "[object]",
	};

	static object? Unwrap(object? o) => o is PSObject p ? p.BaseObject : o;

	/// <summary>Converts PowerShell values (hashtables, PSObjects, arrays) into JSON tool arguments.</summary>
	public static JsonNode? ToJson(object? value) {
		value = Unwrap(value);
		switch(value) {
			case null: return null;
			case JsonNode n: return n.DeepClone();
			case string s: return JsonValue.Create(s);
			case bool b: return JsonValue.Create(b);
			case SwitchParameter sw: return JsonValue.Create(sw.IsPresent);
			case int or long or short or byte or uint or ulong or ushort or sbyte: return JsonValue.Create(Convert.ToInt64(value));
			case float or double or decimal: return JsonValue.Create(Convert.ToDouble(value));
			case IDictionary d: {
				var obj = new JsonObject();
				foreach(DictionaryEntry e in d) obj[e.Key.ToString()!] = ToJson(e.Value);
				return obj;
			}
			case IEnumerable e: return new JsonArray(e.Cast<object?>().Select(ToJson).ToArray());
		}
		if(value is not null && PSObject.AsPSObject(value) is { } pso && pso.Properties.Any() && value.GetType().Name == "PSCustomObject") {
			var obj = new JsonObject();
			foreach(var prop in pso.Properties) obj[prop.Name] = ToJson(prop.Value);
			return obj;
		}
		return JsonValue.Create(value?.ToString());
	}

	public void Dispose() {
		Runspace?.Dispose();
		Runspace = null;
	}

	/// <summary>Exposed to scripts as $Ainur.</summary>
	public sealed class Bridge(SessionHost host) {
		public object? Invoke(string name, object? arguments) {
			JsonObject args = arguments switch {
				null => [],
				string s when s.TrimStart().StartsWith('{') => Schema.ParseArguments(s),
				_ => ToJson(arguments) as JsonObject ?? throw new ToolException("Tool arguments must be a hashtable or object"),
			};
			var result = host.Dispatcher.WaitPumping(host.InvokeNestedAsync(name, args, host.CurrentInvocationId, CancellationToken.None));
			if(result.IsError) throw new RuntimeException($"{name}: {result.Text}");
			return result.Value ?? result.Text;
		}

		public string[] ToolNames() => host.AvailableTools().Select(t => t.Name).ToArray();
		public object Get(string handle) => host.Objects.Get(handle);
		public string Put(object value, string? summary) => host.Objects.Put(Unwrap(value)!, null, summary).Handle;
	}
}
