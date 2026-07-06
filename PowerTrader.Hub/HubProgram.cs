using System;
using System.Windows.Forms;

namespace PowerTrader.Hub
{
    internal static class HubProgram
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try { Application.Run(new MainForm()); }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "PowerTrader Hub - fatal error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
