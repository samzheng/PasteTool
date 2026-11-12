namespace PasteTool;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        using var instance = new Mutex(false, @"Local\PasteTool.ClipboardQueue");
        var ownsInstance = false;
        try
        {
            ownsInstance = instance.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            ownsInstance = true;
        }

        if (!ownsInstance)
        {
            MessageBox.Show(
                "PasteTool 已经在运行，请查看任务栏右下角的图标。",
                "PasteTool",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            Application.Run(new PasteHostForm());
        }
        finally
        {
            instance.ReleaseMutex();
        }
    }
}
