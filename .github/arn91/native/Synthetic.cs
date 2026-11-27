using System;
using System.Drawing;
using System.Windows.Forms;

static class Synthetic
{
    [STAThread]
    static void Main()
    {
        using (var window = new Form {
            Text = "Screeny synthetic foreground",
            TopMost = true,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(32, 32),
            Size = new Size(280, 100)
        })
        {
            window.Shown += (sender, e) => window.Activate();
            Application.Run(window);
        }
    }
}
