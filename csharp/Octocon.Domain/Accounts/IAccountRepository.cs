namespace Octocon.Domain.Accounts;

public interface IAccountRepository
{
    Task<bool> UpdateUsernameAsync(string systemId, string username, CancellationToken cancellationToken = default);

    Task<bool> UpdateDescriptionAsync(string systemId, string description, CancellationToken cancellationToken = default);
}
