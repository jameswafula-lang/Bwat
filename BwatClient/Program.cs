/*
 * Bwat Client / Program.cs - starts the Windows Forms client.
 *
 * Players: open the room link the admin shared (bwat://host:port/CODE), type a name, play.
 * Admins:  log in on the same start screen.
 *
 * The first time Bwat runs it registers the "bwat://" link type for the current
 * Windows user (HKEY_CURRENT_USER - no administrator rights needed). After that,
 * clicking a bwat:// link (in WhatsApp desktop, an e-mail, a browser...) starts
 * BwatClient.exe with the link, and the start screen fills it in automatically.
 */
using System;
using System.Windows.Forms;
using Microsoft.Win32;
using Bwat.Common;

namespace Bwat.Client
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                MessageBox.Show("Something went wrong: " + e.Exception.Message, "Bwat", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            RegisterLinkType();

            string link = null;
            foreach (string a in args)
                if (a.StartsWith(Protocol.LinkScheme + "://", StringComparison.OrdinalIgnoreCase)) link = a;

            Application.Run(new StartForm(link));
        }

        /// <summary>Lets Windows open bwat:// links with this program.</summary>
        static void RegisterLinkType()
        {
            try
            {
                if (Environment.OSVersion.Platform != PlatformID.Win32NT) return;
                string exe = Application.ExecutablePath;
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + Protocol.LinkScheme))
                {
                    key.SetValue("", "URL:Bwat quiz link");
                    key.SetValue("URL Protocol", "");
                    using (RegistryKey cmd = key.CreateSubKey(@"shell\open\command"))
                        cmd.SetValue("", "\"" + exe + "\" \"%1\"");
                }
            }
            catch (Exception)
            {
                // Not important: players can still paste the link on the start screen.
            }
        }
    }
}
