using System;
using System.Threading;
using System.Threading.Tasks;

namespace SpotifyWPF.Service
{
    public class RequestSpacingService : IRequestSpacingService
    {
        private readonly SemaphoreSlim _requestSpacing = new SemaphoreSlim(1, 1);

        private int _spacingMilliseconds = 150;

        public int SpacingMilliseconds
        {
            get => _spacingMilliseconds;
            set => _spacingMilliseconds = Math.Max(0, value);
        }

        public async Task WaitForSpacingAsync(CancellationToken cancellationToken)
        {
            await _requestSpacing.WaitAsync(cancellationToken);

            try
            {
                if (SpacingMilliseconds > 0)
                    await Task.Delay(SpacingMilliseconds, cancellationToken);
            }
            finally
            {
                _requestSpacing.Release();
            }
        }

        public async Task RunSpacedAsync(Func<Task> request, CancellationToken cancellationToken)
        {
            await _requestSpacing.WaitAsync(cancellationToken);

            try
            {
                if (SpacingMilliseconds > 0)
                    await Task.Delay(SpacingMilliseconds, cancellationToken);

                await request();
            }
            finally
            {
                _requestSpacing.Release();
            }
        }
    }
}
