using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace OverlayUtils.App.Tray;

/// <summary>
/// Makes the tray menu follow the Windows taskbar theme (light / dark).
/// The Python version used an undocumented uxtheme call for native menus;
/// a WinForms ContextMenuStrip draws itself, so we give it a colour table.
/// </summary>
internal static class MenuTheme
{
    public static bool SystemUsesDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int light && light == 0;
    }

    public static void Apply(ContextMenuStrip menu)
    {
        var dark = SystemUsesDark();
        menu.Renderer = dark ? new ToolStripProfessionalRenderer(new DarkColors()) : new ToolStripProfessionalRenderer();
        var fore = dark ? Color.White : SystemColors.MenuText;
        menu.ForeColor = fore;
        foreach (ToolStripItem item in menu.Items) Paint(item, fore);
    }

    private static void Paint(ToolStripItem item, Color fore)
    {
        item.ForeColor = fore;
        if (item is ToolStripMenuItem { HasDropDownItems: true } parent)
        {
            if (parent.DropDown is ToolStripDropDownMenu drop) drop.Renderer = parent.Owner?.Renderer ?? drop.Renderer;
            foreach (ToolStripItem child in parent.DropDownItems) Paint(child, fore);
        }
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        private static readonly Color Back = Color.FromArgb(43, 43, 43);
        private static readonly Color Hover = Color.FromArgb(65, 65, 65);
        private static readonly Color Line = Color.FromArgb(80, 80, 80);

        public override Color ToolStripDropDownBackground => Back;
        public override Color ImageMarginGradientBegin => Back;
        public override Color ImageMarginGradientMiddle => Back;
        public override Color ImageMarginGradientEnd => Back;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Back;
        public override Color CheckBackground => Hover;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
    }
}
