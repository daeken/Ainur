using System.Collections.Concurrent;

namespace Ainur.Core.Runtime;

/// <summary>
/// A dedicated managed thread with a serial message pump. Its synchronization context returns every await
/// continuation to this thread, so agent state transitions and inline tool entry stay owned by one thread.
/// </summary>
public sealed class Dispatcher : IDisposable {
	readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> Queue = new();
	readonly Thread Thread;
	readonly Context SyncContext;
	public string Name { get; }
	public int ManagedThreadId => Thread.ManagedThreadId;
	public bool IsCurrent => Environment.CurrentManagedThreadId == Thread.ManagedThreadId;

	public Dispatcher(string name) {
		Name = name;
		SyncContext = new Context(this);
		Thread = new Thread(Run) { Name = $"ainu:{name}", IsBackground = true };
		Thread.Start();
	}

	void Run() {
		SynchronizationContext.SetSynchronizationContext(SyncContext);
		foreach(var (callback, state) in Queue.GetConsumingEnumerable())
			Execute(callback, state);
	}

	static void Execute(SendOrPostCallback callback, object? state) {
		try {
			callback(state);
		} catch(Exception e) {
			Console.Error.WriteLine($"[dispatcher] unhandled exception: {e}");
		}
	}

	public void Post(Action action) {
		if(!Queue.IsAddingCompleted) Queue.Add((_ => action(), null));
	}

	/// <summary>Runs an async function on this dispatcher and returns its task.</summary>
	public Task<T> InvokeAsync<T>(Func<Task<T>> fn) {
		var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
		Post(async () => {
			try { tcs.SetResult(await fn()); } catch(Exception e) { tcs.SetException(e); }
		});
		return tcs.Task;
	}

	public Task InvokeAsync(Func<Task> fn) => InvokeAsync(async () => { await fn(); return true; });

	/// <summary>
	/// Blocks the dispatcher thread until <paramref name="task"/> completes while continuing to pump queued work,
	/// so synchronous callers on this thread (e.g. a PowerShell pipeline) can await async tools without deadlock.
	/// </summary>
	public T WaitPumping<T>(Task<T> task) {
		if(!IsCurrent) return task.GetAwaiter().GetResult();
		while(!task.IsCompleted) {
			if(Queue.TryTake(out var item, 20))
				Execute(item.Callback, item.State);
		}
		return task.GetAwaiter().GetResult();
	}

	public void Dispose() {
		Queue.CompleteAdding();
		if(!IsCurrent) Thread.Join(TimeSpan.FromSeconds(5));
	}

	sealed class Context(Dispatcher owner) : SynchronizationContext {
		public override void Post(SendOrPostCallback d, object? state) {
			if(!owner.Queue.IsAddingCompleted) owner.Queue.Add((d, state));
		}

		public override void Send(SendOrPostCallback d, object? state) {
			if(owner.IsCurrent) d(state);
			else {
				using var done = new ManualResetEventSlim();
				Exception? error = null;
				owner.Queue.Add((s => {
					try { d(s); } catch(Exception e) { error = e; } finally { done.Set(); }
				}, state));
				done.Wait();
				if(error is not null) throw error;
			}
		}

		public override SynchronizationContext CreateCopy() => this;
	}
}
