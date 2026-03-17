using System.Threading;

namespace Octocon.IntegrationTests;

internal static class ApiProcessGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        return new Releaser();
    }

    private sealed class Releaser : IAsyncDisposable
    {
        private bool _released;

        public ValueTask DisposeAsync()
        {
            if (!_released)
            {
                _released = true;
                Gate.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
