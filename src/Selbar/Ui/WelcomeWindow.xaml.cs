using System.Windows;

namespace Selbar.Ui;

/// <summary>Bienvenida de primer arranque (solo una vez): tres pasos y dos salidas.</summary>
public partial class WelcomeWindow : FluentWindow
{
    public WelcomeWindow()
    {
        InitializeComponent();

        try
        {
            HeroIcon.Source = AppIcon.Get();

            var triggers = Selbar.App.SettingsMgr.Settings.Triggers;
            bool hotkeyReady = triggers.HotkeyEnabled && !string.IsNullOrWhiteSpace(triggers.Hotkey);
            if (hotkeyReady)
            {
                HotkeyText.Text = "2 · Abre la barra con " + triggers.Hotkey;
                HotkeyHintText.Text = triggers.AutoOnMouseSelection || triggers.AutoOnKeyboardSelection
                    ? "Selecciona y pulsa el atajo: la barra se abre con esa selección. Los avisos automáticos también están activos."
                    : "Selecciona y pulsa el atajo: la barra se abre con esa selección. No aparece sola hasta que la llamas (puedes activar los avisos en Ajustes → Selección y atajos).";
            }
            else
            {
                HotkeyText.Text = "2 · Abre la barra con un clic";
                HotkeyHintText.Text = "El atajo está desactivado. Actívalo en Ajustes → Selección y atajos; mientras tanto, el menú de la bandeja abre Ajustes y la barra.";
            }
        }
        catch { }
    }

    private void OnConfigureAi(object s, RoutedEventArgs e)
    {
        Selbar.App.OpenSettings("ia");
        Close();
    }

    private void OnStart(object s, RoutedEventArgs e) => Close();
}
