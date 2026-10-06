namespace Ainur.Core.Providers;

/// <summary>Bounds waits without claiming that a noncooperative operation has unwound.</summary>
internal static class BoundedProviderIO {
	public static async Task<T> Await<T>(Task<T> task, CancellationToken ct, ProviderRequest request) {
		try { return await task.WaitAsync(ct); }
		catch(OperationCanceledException) {
			if(!task.IsCompleted) {
				request.OnUnsettled?.Invoke(task);
				_ = task.ContinueWith(t => { if(t.IsCompletedSuccessfully && t.Result is IDisposable d) d.Dispose(); _ = t.Exception; }, TaskScheduler.Default);
			}
			throw;
		}
	}
	public static async Task Await(Task task, CancellationToken ct, ProviderRequest request) {
		try { await task.WaitAsync(ct); }
		catch(OperationCanceledException) {
			if(!task.IsCompleted) { request.OnUnsettled?.Invoke(task); _ = task.ContinueWith(t => _ = t.Exception, TaskScheduler.Default); }
			throw;
		}
	}
}

/// <summary>Idle deadline applies to bytes, including partial lines and SSE comments.</summary>
internal sealed class IdleReadStream(Stream inner, ProviderRequest request, CancellationToken callToken) : Stream {
	public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
		using var idle = CancellationTokenSource.CreateLinkedTokenSource(callToken, cancellationToken);
		idle.CancelAfter(request.StreamIdleTimeout);
		try { return await BoundedProviderIO.Await(inner.ReadAsync(buffer, idle.Token).AsTask(), idle.Token, request); }
		catch(OperationCanceledException) when(idle.IsCancellationRequested && !callToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested) {
			throw new ProviderException("Response body inactivity deadline exceeded", retryable: false, mayHaveBilled: true);
		}
	}
	public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
	public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	public override bool CanRead => true;
	public override bool CanSeek => false;
	public override bool CanWrite => false;
	public override long Length => throw new NotSupportedException();
	public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
	public override void Flush() => throw new NotSupportedException();
	public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
	public override void SetLength(long value) => throw new NotSupportedException();
	public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	protected override void Dispose(bool disposing) { if(disposing) inner.Dispose(); base.Dispose(disposing); }
}
