namespace LinkForge;

/// <summary>Runs workflow continuations off the request thread so long model/build steps do not hold HTTP requests open.</summary>
public sealed class RunDispatcher(IHostApplicationLifetime lifetime, ILogger<RunDispatcher> logger)
{
    public void Launch(Func<CancellationToken, Task> work) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await work(lifetime.ApplicationStopping);
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Background workflow work cancelled during shutdown.");
            }
            catch (Exception error)
            {
                logger.LogError(error, "Background workflow work failed.");
            }
        });
}
