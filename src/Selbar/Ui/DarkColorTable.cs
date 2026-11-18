using System.Drawing;
using System.Windows.Forms;

namespace Selbar.Ui;

/// <summary>Colores del menú del tray en modo oscuro (WinForms no respeta el tema del sistema).</summary>
internal sealed class DarkColorTable : ProfessionalColorTable
{
    private static readonly Color Background = Color.FromArgb(0x2B, 0x2B, 0x2B);
    private static readonly Color Hover = Color.FromArgb(0x3A, 0x3A, 0x3A);
    private static readonly Color Pressed = Color.FromArgb(0x33, 0x33, 0x33);
    private static readonly Color Border = Color.FromArgb(0x3D, 0x3D, 0x3D);

    public override Color ToolStripDropDownBackground => Background;
    public override Color ImageMarginGradientBegin => Background;
    public override Color ImageMarginGradientMiddle => Background;
    public override Color ImageMarginGradientEnd => Background;
    public override Color MenuBorder => Border;
    public override Color MenuItemBorder => Hover;
    public override Color MenuItemSelected => Hover;
    public override Color MenuItemSelectedGradientBegin => Hover;
    public override Color MenuItemSelectedGradientEnd => Hover;
    public override Color MenuItemPressedGradientBegin => Pressed;
    public override Color MenuItemPressedGradientEnd => Pressed;
    public override Color SeparatorDark => Border;
    public override Color SeparatorLight => Border;
}
