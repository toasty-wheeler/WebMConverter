using System;
using System.Windows.Forms;

namespace WebMConverter
{
    // The theme switch in the status bar: Automatic (follows Windows) -> Dark -> Light -> Automatic.
    public partial class MainForm
    {
        ToolStripButton themeButton;

        void SetupThemeButton()
        {
            themeButton = new ToolStripButton
            {
                Alignment = ToolStripItemAlignment.Right,
                DisplayStyle = ToolStripItemDisplayStyle.Text,
            };
            themeButton.Click += (s, e) => CycleTheme();
            toolStripStatusLabel.Owner.Items.Add(themeButton);

            Theme.Changed += (s, e) => UpdateThemeButton();
            UpdateThemeButton();
        }

        void CycleTheme()
        {
            switch (Theme.Mode)
            {
                case ThemeMode.Automatic: Theme.Mode = ThemeMode.Dark; break;
                case ThemeMode.Dark: Theme.Mode = ThemeMode.Light; break;
                default: Theme.Mode = ThemeMode.Automatic; break;
            }
            Properties.Settings.Default.ThemeMode = Theme.Mode.ToString();
            Properties.Settings.Default.Save();
            UpdateThemeButton();
        }

        void UpdateThemeButton()
        {
            switch (Theme.Mode)
            {
                case ThemeMode.Dark:
                    themeButton.Text = "☾ Dark";
                    break;
                case ThemeMode.Light:
                    themeButton.Text = "☀ Light";
                    break;
                default:
                    themeButton.Text = "◐ Automatic (" + (Theme.IsDark ? "dark" : "light") + ")";
                    break;
            }
            themeButton.ToolTipText = "Theme: click to switch between Automatic (follows Windows), Dark and Light";
        }
    }
}
