using System.Threading.Channels;

namespace HealthTech.Documents;

/// <summary>
/// Очередь заданий, чей протокол сохранён, но ещё не сверен с транскриптом. Сверка - контроль
/// качества, а не результат: конвейер кладёт сюда id и завершает задание, не дожидаясь её.
/// </summary>
public interface IMinutesVerificationQueue
{
    void Enqueue(Guid jobId);
}

public sealed class MinutesVerificationQueue : IMinutesVerificationQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(Guid jobId) => _channel.Writer.TryWrite(jobId);

    internal IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>
/// Сверяет протоколы из очереди по одному. Модель одна и та же для извлечения фактов и
/// сверки, так что параллелить нечего; очередь на перезапуске не сохраняется - вместо этого
/// при старте подбираются все документы, у которых сверка осталась в состоянии "в процессе".
/// </summary>
public sealed class MinutesVerificationWorker(
    MinutesVerificationQueue queue, IDocumentService documents, ILogger<MinutesVerificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var jobId in await documents.FindPendingVerificationsAsync(stoppingToken))
        {
            logger.LogInformation("Задание {JobId}: сверка протокола не была завершена до перезапуска, поставлена в очередь", jobId);
            queue.Enqueue(jobId);
        }

        await foreach (var jobId in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await documents.VerifyPendingAsync(jobId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Документ уже сохранён; сорвавшаяся сверка - это предупреждение, не потеря.
                logger.LogWarning(ex, "Задание {JobId}: фоновая сверка протокола не удалась", jobId);
            }
        }
    }
}
