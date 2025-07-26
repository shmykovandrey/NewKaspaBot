namespace KaspaBot.Domain.Interfaces
{
    public interface IOrderRecoveryService
    {
        Task RunRecoveryForUser(long userId, CancellationToken cancellationToken);
    }
} 