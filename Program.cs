namespace ChatGptDictationBridge;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, "ChatGptDictationBridge.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "OpenAI Flow Dictation laeuft bereits im Hintergrund.",
                "OpenAI Flow Dictation",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new DictationTrayAppContext());
    }
}
