#nullable enable

using System.Reflection;

namespace Uno.UI.Runtime.Skia.Headless.Testing;

public sealed class UnoHeadlessTestSession : IDisposable, IAsyncDisposable
{
	private static readonly Dictionary<Assembly, UnoHeadlessTestSession> _sessions = new();
	private static readonly object _gate = new();

	private readonly Type _applicationType;
	private readonly HeadlessPlatformOptions _options;
	private readonly UnoTestIsolationLevel _isolationLevel;
	private HeadlessHostRuntime? _sharedRuntime;
	private bool _disposed;

	static UnoHeadlessTestSession()
	{
		AppDomain.CurrentDomain.ProcessExit += (_, _) =>
		{
			lock (_gate)
			{
				foreach (var session in _sessions.Values.ToArray())
				{
					session.Dispose();
				}
			}
		};
	}

	private UnoHeadlessTestSession(Type applicationType, HeadlessPlatformOptions options, UnoTestIsolationLevel isolationLevel)
	{
		_applicationType = applicationType;
		_options = options.Clone();
		_isolationLevel = isolationLevel;
	}

	public static UnoHeadlessTestSession StartNew(Type applicationType, HeadlessPlatformOptions? options = null, UnoTestIsolationLevel isolationLevel = UnoTestIsolationLevel.PerTest)
		=> new(applicationType, options ?? new HeadlessPlatformOptions(), isolationLevel);

	public static UnoHeadlessTestSession GetOrStartForAssembly(Assembly? assembly = null)
	{
		assembly ??= typeof(UnoHeadlessTestSession).Assembly;

		lock (_gate)
		{
			if (!_sessions.TryGetValue(assembly, out var session))
			{
				var applicationType = assembly.GetCustomAttribute<UnoTestApplicationAttribute>()?.ApplicationType ?? typeof(Microsoft.UI.Xaml.Application);
				var isolationLevel = assembly.GetCustomAttribute<UnoTestIsolationAttribute>()?.IsolationLevel ?? UnoTestIsolationLevel.PerTest;
				session = new UnoHeadlessTestSession(applicationType, new HeadlessPlatformOptions(), isolationLevel);
				_sessions[assembly] = session;
			}

			return session;
		}
	}

	public Task DispatchAsync(Action action, CancellationToken cancellationToken = default)
		=> DispatchAsync<object?>(() =>
		{
			action();
			return Task.FromResult<object?>(null);
		}, cancellationToken);

	public async Task<TResult> DispatchAsync<TResult>(Func<Task<TResult>> action, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);

		if (_isolationLevel == UnoTestIsolationLevel.PerAssembly)
		{
			var runtime = await EnsureSharedRuntimeAsync(cancellationToken);
			return await runtime.DispatchAsync(action, cancellationToken);
		}

		await using var runtimeScope = await HeadlessHostRuntime.StartNewAsync(_applicationType, _options, cancellationToken);
		return await runtimeScope.DispatchAsync(action, cancellationToken);
	}

	public async Task ResetStateAsync(CancellationToken cancellationToken = default)
	{
		if (_disposed)
		{
			return;
		}

		if (_isolationLevel == UnoTestIsolationLevel.PerAssembly)
		{
			if (_sharedRuntime is { } runtime)
			{
				cancellationToken.ThrowIfCancellationRequested();
				await runtime.ResetStateAsync();
			}
		}
	}

	private async Task<HeadlessHostRuntime> EnsureSharedRuntimeAsync(CancellationToken cancellationToken)
	{
		if (_sharedRuntime is not null)
		{
			return _sharedRuntime;
		}

		_sharedRuntime = await HeadlessHostRuntime.StartNewAsync(_applicationType, _options, cancellationToken);
		return _sharedRuntime;
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		if (_sharedRuntime is not null)
		{
			_sharedRuntime.DisposeAsync().AsTask().GetAwaiter().GetResult();
			_sharedRuntime = null;
		}
	}

	public async ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		if (_sharedRuntime is not null)
		{
			await _sharedRuntime.DisposeAsync();
			_sharedRuntime = null;
		}
	}
}
