using DisplayBrightness.ViewModels;

namespace DisplayBrightness.Tests;

public sealed class AsyncRelayCommandTests
{
    [Fact]
    public async Task Execute_PreventsDuplicateExecutionUntilCompletion()
    {
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int executions = 0;
        var command = new AsyncRelayCommand(async () =>
        {
            Interlocked.Increment(ref executions);
            started.TrySetResult();
            await release.Task;
        });

        command.Execute(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        command.Execute(null);

        Assert.False(command.CanExecute(null));
        Assert.Equal(1, Volatile.Read(ref executions));

        release.SetResult();
        await WaitUntilAsync(() => command.CanExecute(null));
        Assert.Equal(1, Volatile.Read(ref executions));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(condition());
    }
}
