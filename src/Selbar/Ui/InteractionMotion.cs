using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

// El proyecto usa WinForms para el tray (NotifyIcon); ImplicitUsings mete
// System.Windows.Forms y choca con los tipos WPF homónimos.
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseEventHandler = System.Windows.Input.MouseEventHandler;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace Selbar.Ui;

/// <summary>Mouse-only feedback on an explicit template content part; never changes input routing.</summary>
public static class InteractionMotion
{
    /// <summary>
    /// Opt in on a ButtonBase whose template exposes a FrameworkElement named PART_PressFeedback.
    /// Only that part scales (0.98 / 100 ms, release 100 ms); the focus border and hit area stay fixed.
    /// A custom template without this part is intentionally inert. No keyboard or global Loaded animation.
    /// </summary>
    public static readonly DependencyProperty PressFeedbackProperty = DependencyProperty.RegisterAttached(
        "PressFeedback", typeof(bool), typeof(InteractionMotion), new PropertyMetadata(false, OnPressFeedbackChanged));

    public static bool GetPressFeedback(DependencyObject target) => (bool)target.GetValue(PressFeedbackProperty);
    public static void SetPressFeedback(DependencyObject target, bool value) => target.SetValue(PressFeedbackProperty, value);

    private static readonly DependencyProperty PressStateProperty = DependencyProperty.RegisterAttached(
        "PressState", typeof(PressState), typeof(InteractionMotion), new PropertyMetadata(null));

    private sealed class PressState
    {
        internal Motion.TransformLease Lease { get; }
        internal bool PointerDown = true;
        internal bool Releasing;
        internal PressState(Motion.TransformLease lease) => Lease = lease;
    }

    private static void OnPressFeedbackChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not ButtonBase button) return;
        // Remove first so style replacement cannot accumulate subscriptions.
        button.RemoveHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnMouseDown));
        button.RemoveHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnMouseUp));
        button.RemoveHandler(UIElement.LostMouseCaptureEvent, new MouseEventHandler(OnLostMouseCapture));
        button.MouseLeave -= OnMouseLeave;
        button.Unloaded -= OnUnloaded;
        button.IsEnabledChanged -= OnIsEnabledChanged;
        Reset(button);
        if (e.NewValue is not true) return;
        button.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnMouseDown), true);
        button.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnMouseUp), true);
        button.AddHandler(UIElement.LostMouseCaptureEvent, new MouseEventHandler(OnLostMouseCapture), true);
        button.MouseLeave += OnMouseLeave;
        button.Unloaded += OnUnloaded;
        button.IsEnabledChanged += OnIsEnabledChanged;
    }

    private static void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ButtonBase button || e.ChangedButton != MouseButton.Left || e.StylusDevice != null) return;
        var previous = button.GetValue(PressStateProperty) as PressState;
        var from = previous?.Lease.IsOwned == true ? previous.Lease.Scale.ScaleX : 1;
        Reset(button);
        if (!Motion.Enabled || !button.IsEnabled || !button.IsLoaded) return;
        // Never fall back to transforming the button/window itself, including custom consumer templates.
        if (button.Template?.FindName("PART_PressFeedback", button) is not FrameworkElement part) return;
        Motion.TransformLease? lease = null;
        try
        {
            lease = new Motion.TransformLease(part, from, disposed: () =>
            {
                // Part unload, template replacement and a live reduced-motion change also release button ownership.
                if (button.GetValue(PressStateProperty) is PressState state && ReferenceEquals(state.Lease, lease))
                    button.ClearValue(PressStateProperty);
            });
            button.SetValue(PressStateProperty, new PressState(lease));
            Motion.AnimateDouble(lease.Scale, ScaleTransform.ScaleXProperty, 0.98, Motion.PressMilliseconds);
            Motion.AnimateDouble(lease.Scale, ScaleTransform.ScaleYProperty, 0.98, Motion.PressMilliseconds);
        }
        catch
        {
            Reset(button);
            lease?.Dispose();
        }
    }

    private static void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is ButtonBase button && e.ChangedButton == MouseButton.Left) Release(button);
    }

    private static void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is ButtonBase button) Release(button);
    }

    private static void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        // Normal ButtonBase release drops capture after PreviewMouseUp; let its return finish.
        if (sender is ButtonBase button && button.GetValue(PressStateProperty) is PressState { PointerDown: true })
            Reset(button);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ButtonBase button) Reset(button);
    }

    private static void OnIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is ButtonBase button && e.NewValue is false) Reset(button);
    }

    private static void Release(ButtonBase button)
    {
        if (button.GetValue(PressStateProperty) is not PressState state) return;
        if (!Motion.Enabled || !button.IsEnabled || !state.Lease.IsOwned)
        {
            Reset(button);
            return;
        }
        if (state.Releasing) return;
        state.PointerDown = false;
        state.Releasing = true;
        try
        {
            Motion.AnimateDouble(state.Lease.Scale, ScaleTransform.ScaleXProperty, 1, Motion.ReleaseMilliseconds);
            Motion.AnimateDouble(state.Lease.Scale, ScaleTransform.ScaleYProperty, 1, Motion.ReleaseMilliseconds,
                cleanup: () =>
                {
                    if (ReferenceEquals(button.GetValue(PressStateProperty), state)) Reset(button);
                });
        }
        catch { Reset(button); }
    }

    private static void Reset(ButtonBase button)
    {
        if (button.GetValue(PressStateProperty) is not PressState state) return;
        button.ClearValue(PressStateProperty);
        state.Lease.Dispose();
    }
}
