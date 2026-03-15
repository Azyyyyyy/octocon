namespace Octocon.Domain.Settings;

public interface INotificationTokenRepository
{
    Task<bool> AddAsync(string systemId, string token, CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(string token, CancellationToken cancellationToken = default);
}
