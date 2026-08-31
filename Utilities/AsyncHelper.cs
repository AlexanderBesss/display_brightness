using System.Diagnostics;

namespace DisplayBrightness.Utilities;

internal static class AsyncHelper
{
    public static async void FireAndForget(Func<Task> operation, string context)
    {
        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine($"[{context}] Operation cancelled.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{context}] {ex}");
        }
    }
}
