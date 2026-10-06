using System.Diagnostics;
using System.Text;

namespace Ainur.Core.Runtime;

/// <summary>Exit and both pipes share one deadline. Never returns raw stderr or argument values.</summary>
internal static class NativeGitProcess {
	internal sealed record Result(bool Ok, string Text);
	internal sealed class Interrupted(string phase, bool rootExited, bool pipesClosed) : Exception(
		$"Native Git interrupted in phase {phase}; root_exit_confirmed={rootExited}; pipes_closed={pipesClosed}. Admitted effects UNKNOWN; inspect, never replay.");

	internal static Result Run(ProcessStartInfo start, string phase, CancellationToken token, TimeSpan timeout, TimeSpan? cleanupTimeout = null) =>
		RunAsync(start, phase, token, timeout, cleanupTimeout ?? TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
	static async Task<Result> RunAsync(ProcessStartInfo start, string phase, CancellationToken token, TimeSpan timeout, TimeSpan cleanupTimeout) {
		// Cancellation before start has no subprocess effects.
		token.ThrowIfCancellationRequested();
		using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
		deadline.CancelAfter(timeout);
		using var p = Process.Start(start) ?? throw new InvalidOperationException("Native Git could not start.");
		using var reads = new CancellationTokenSource();
		var stdout = Drain(p.StandardOutput, 24000, reads.Token);
		var stderr = Drain(p.StandardError, 0, reads.Token);
		var exit = p.WaitForExitAsync();
		var settled = Task.WhenAll(exit, stdout, stderr);
		try {
			await settled.WaitAsync(deadline.Token).ConfigureAwait(false);
			return new(p.ExitCode == 0, (await stdout.ConfigureAwait(false)).TrimEnd('\r', '\n'));
		} catch {
			// Never replay. Killing a root/tree is best effort, not proof of descendant effects.
			try { if(!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
			try { await settled.WaitAsync(cleanupTimeout).ConfigureAwait(false); } catch { }
			var rootExited = exit.IsCompletedSuccessfully;
			var pipesClosed = stdout.IsCompletedSuccessfully && stderr.IsCompletedSuccessfully;
			reads.Cancel();
			// Observe eventual failures without waiting indefinitely on descendant-held pipes.
			_ = settled.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
				TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
			throw new Interrupted(phase, rootExited, pipesClosed);
		}
	}
	static async Task<string> Drain(StreamReader reader, int limit, CancellationToken token) {
		var text = new StringBuilder(); var buffer = new char[4096]; int n;
		while((n = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
			if(text.Length < limit) text.Append(buffer, 0, Math.Min(n, limit - text.Length));
		return text.ToString();
	}
}
