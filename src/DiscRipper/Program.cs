using DiscRipper.UI;

namespace DiscRipper;

static class Program
{
    [STAThread]
    static void Main()
    {
        // SystemAware: Windows ci dà il ridimensionamento dello schermo principale (100/125/150%)
        // e tutte le misure dell'interfaccia vengono scalate di conseguenza (Ui.S).
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        UI.Ui.Init();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.Message, "DiscRipper", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Application.Run(new MainForm());
    }
}
