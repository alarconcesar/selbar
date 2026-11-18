using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MediaColor = System.Windows.Media.Color;

namespace Selbar.Ui;

/// <summary>
/// Animaciones cortas y coherentes. Respeta el ajuste de Windows
/// "Efectos de animación" (SystemParameters.ClientAreaAnimation): si está
/// desactivado, todo es instantáneo (accesibilidad y equipos lentos).
/// </summary>
public static class Motion
{
    public const int PressMilliseconds = 100;
    public const int ReleaseMilliseconds = 100;
    public const int RevealMilliseconds = 150;
    public const int ModalMilliseconds = 180;

    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, PropertyAnimation>> Animations = new();
    private static readonly List<WeakReference<Operation>> Active = new();
    private static bool? _overrideEnabled;

    static Motion() => SystemParameters.StaticPropertyChanged += OnSystemSettingChanged;

    /// <summary>Fuerza el estado de las animaciones (tests/capturas deterministas). null = sistema.</summary>
    public static bool? OverrideEnabled
    {
        get => _overrideEnabled;
        set
        {
            _overrideEnabled = value;
            FinishIfDisabled();
        }
    }

    /// <summary>¿Están permitidas las animaciones en este equipo?</summary>
    public static bool Enabled
    {
        get
        {
            if (OverrideEnabled.HasValue) return OverrideEnabled.Value;
            try { return SystemParameters.ClientAreaAnimation; }
            catch { return true; }
        }
    }

    /// <summary>Duración efectiva: 0 si el usuario desactivó las animaciones.</summary>
    public static Duration Duration(int milliseconds) =>
        Enabled ? TimeSpan.FromMilliseconds(Math.Max(1, milliseconds)) : TimeSpan.Zero;

    /// <summary>Entrada explícita de contenido de ventana ocasional, nunca un Loaded global.</summary>
    public static void FadeInWindow(Window window, double offsetY = 10, int milliseconds = ModalMilliseconds)
    {
        if (window == null) return;
        CancelOwned(window, UIElement.OpacityProperty);
        window.SetCurrentValue(UIElement.OpacityProperty, 1d);
        if (window.Content is FrameworkElement root) FadeIn(root, milliseconds, offsetY);
    }

    /// <summary>
    /// Reveal opt-in para paneles ocasionales: llamar explícitamente al mostrarlos.
    /// No usar en búsqueda, navegación por teclado ni cada Loaded. Sin animación queda visible.
    /// </summary>
    public static void FadeIn(FrameworkElement element, int milliseconds = RevealMilliseconds,
        double offsetY = 0, double scaleFrom = 1, double scaleOriginX = 0.5, double scaleOriginY = 0.5)
    {
        Reveal(element, milliseconds, offsetY, scaleFrom, scaleOriginX, scaleOriginY, TimeSpan.Zero);
    }

    /// <summary>Compatibilidad para entradas ocasionales; no usar en listas de alta frecuencia.</summary>
    public static void FadeInStaggered(FrameworkElement element, int index, int baseDelay = 0,
        int stepMs = 22, int milliseconds = RevealMilliseconds)
    {
        var delay = Math.Max(0L, (long)baseDelay + Math.Clamp(index, 0, 14) * (long)stepMs);
        Reveal(element, milliseconds, 0, 1, 0.5, 0.5, TimeSpan.FromMilliseconds(delay));
    }

    private static void Reveal(FrameworkElement element, int milliseconds, double offsetY,
        double scaleFrom, double originX, double originY, TimeSpan delay)
    {
        if (element == null) return;
        TransformLease? lease = null;
        var previous = element.GetValue(TransformLeaseProperty) as TransformLease;
        var fromOpacity = IsOwned(element, UIElement.OpacityProperty) ? element.Opacity : 0;
        // Retarget from the sampled visual values before removing our previous clocks.
        var fromY = previous?.Translate.Y ?? offsetY;
        var fromScale = previous?.Scale.ScaleX ?? Math.Clamp(scaleFrom, 0.9, 1);
        previous?.Dispose();
        CancelOwned(element, UIElement.OpacityProperty);
        try
        {
            element.SetCurrentValue(UIElement.OpacityProperty, 1d);
            if (!Enabled || !element.IsEnabled) return;
            if (Math.Abs(fromY) > 0.01 || Math.Abs(fromScale - 1) > 0.001)
            {
                lease = new TransformLease(element, fromScale, fromY, originX, originY);
                AnimateDouble(lease.Translate, TranslateTransform.YProperty, 0, milliseconds, delay: delay);
                AnimateDouble(lease.Scale, ScaleTransform.ScaleXProperty, 1, milliseconds, delay: delay);
                AnimateDouble(lease.Scale, ScaleTransform.ScaleYProperty, 1, milliseconds, delay: delay);
            }
            AnimateDouble(element, UIElement.OpacityProperty, 1, milliseconds, fromOpacity,
                delay, cleanup: lease == null ? null : lease.Dispose);
        }
        catch
        {
            lease?.Dispose();
            element.SetCurrentValue(UIElement.OpacityProperty, 1d);
        }
    }

    /// <summary>Salida rápida. La opacidad base queda en 1 incluso si Closing cancela y oculta.</summary>
    public static void FadeOutAndClose(Window window, int milliseconds = 110)
    {
        if (window == null) return;
        AnimateDouble(window, UIElement.OpacityProperty, 0, milliseconds,
            completed: () => { try { window.Close(); } catch { } }, baseValue: 1);
    }

    /// <summary>Fundido de opacidad simple, sin retener el clock después del estado final.</summary>
    public static void Fade(FrameworkElement element, double to, int milliseconds = 140)
    {
        if (element != null) AnimateDouble(element, UIElement.OpacityProperty, to, milliseconds);
    }

    /// <summary>Traslación explícita; el propietario no debe llamarla por navegación de teclado.</summary>
    public static void AnimateX(TranslateTransform transform, double to, int milliseconds = ModalMilliseconds)
    {
        if (transform != null) AnimateDouble(transform, TranslateTransform.XProperty, to, milliseconds);
    }

    /// <summary>Brillo continuo. Sin movimiento: punto estático visible, sin clock repetitivo.</summary>
    public static Storyboard Pulse(UIElement? target, double min = 0.25, int milliseconds = 650)
    {
        var storyboard = new Storyboard();
        if (target == null) return storyboard;
        if (Loops.TryGetValue(target, out var previous)) previous.Dispose();
        target.SetCurrentValue(UIElement.OpacityProperty, 1d);
        if (!Enabled) return storyboard;

        var animation = new DoubleAnimation(1, Math.Clamp(min, 0, 1), TimeSpan.FromMilliseconds(Math.Max(1, milliseconds)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(UIElement.OpacityProperty));
        storyboard.Children.Add(animation);
        StartLoop(storyboard, target);
        return storyboard;
    }

    /// <summary>
    /// Progreso en bucle. El llamante conserva Stop(); un Transform no tiene evento Unloaded.
    /// Sin movimiento queda en el centro del recorrido, nunca un clock de duración cero.
    /// </summary>
    public static Storyboard Indeterminate(TranslateTransform transform, double from, double to, int milliseconds = 1200)
    {
        var storyboard = new Storyboard();
        if (transform == null || transform.IsFrozen) return storyboard;
        if (Loops.TryGetValue(transform, out var previous)) previous.Dispose();
        transform.SetCurrentValue(TranslateTransform.XProperty, (from + to) / 2);
        if (!Enabled) return storyboard;

        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(Math.Max(1, milliseconds)))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(animation, transform);
        Storyboard.SetTargetProperty(animation, new PropertyPath(TranslateTransform.XProperty));
        storyboard.Children.Add(animation);
        StartLoop(storyboard, transform);
        return storyboard;
    }

    /// <summary>Solo pinceles privados mutables; nunca un pincel DynamicResource compartido.</summary>
    public static void AnimateColor(SolidColorBrush brush, MediaColor to, int milliseconds = 120)
    {
        if (brush == null || brush.IsFrozen) return;
        var from = brush.Color;
        CancelOwned(brush, SolidColorBrush.ColorProperty);
        brush.SetCurrentValue(SolidColorBrush.ColorProperty, to);
        if (!Enabled) return;
        StartAnimation(brush, SolidColorBrush.ColorProperty,
            new ColorAnimation(from, to, Duration(milliseconds)) { EasingFunction = EaseOut });
    }

    internal static void AnimateDouble(DependencyObject target, DependencyProperty property, double to,
        int milliseconds, double? from = null, TimeSpan delay = default, Action? cleanup = null,
        Action? completed = null, double? baseValue = null)
    {
        var start = from ?? (double)target.GetValue(property);
        CancelOwned(target, property);
        try
        {
            // SetCurrentValue keeps bindings/resources intact. Stop/failed setup exposes this final value.
            target.SetCurrentValue(property, baseValue ?? to);
            if (!Enabled || milliseconds <= 0)
            {
                cleanup?.Invoke();
                completed?.Invoke();
                return;
            }
            var duration = TimeSpan.FromMilliseconds(Math.Max(1, milliseconds));
            AnimationTimeline animation;
            if (delay > TimeSpan.Zero)
            {
                // BeginTime would expose the final base value until the delay expires, then jump backwards.
                var delayed = new DoubleAnimationUsingKeyFrames { Duration = delay + duration };
                delayed.KeyFrames.Add(new DiscreteDoubleKeyFrame(start, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                delayed.KeyFrames.Add(new DiscreteDoubleKeyFrame(start, KeyTime.FromTimeSpan(delay)));
                delayed.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(delay + duration))
                    { EasingFunction = EaseOut });
                animation = delayed;
            }
            else animation = new DoubleAnimation(start, to, duration) { EasingFunction = EaseOut };
            StartAnimation(target, property, animation, cleanup, completed);
        }
        catch
        {
            cleanup?.Invoke();
            completed?.Invoke();
        }
    }

    private static bool IsOwned(DependencyObject target, DependencyProperty property) =>
        Animations.TryGetValue(target, out var animations) && animations.ContainsKey(property);

    private static void CancelOwned(DependencyObject target, DependencyProperty property)
    {
        if (Animations.TryGetValue(target, out var animations) && animations.TryGetValue(property, out var owner))
            owner.Dispose();
    }

    private static void StartAnimation(DependencyObject target, DependencyProperty property,
        AnimationTimeline timeline, Action? cleanup = null, Action? completed = null)
    {
        var owner = new PropertyAnimation(target, property, timeline, cleanup, completed);
        Animations.GetOrCreateValue(target)[property] = owner;
        try { owner.Start(); }
        catch { owner.Finish(); }
    }

    private static void OnSystemSettingChanged(object? sender, PropertyChangedEventArgs e) => FinishIfDisabled();

    private static void FinishIfDisabled()
    {
        if (Enabled) return;
        Operation[] operations;
        lock (Active)
        {
            operations = Active.Select(reference => reference.TryGetTarget(out var operation) ? operation : null)
                .OfType<Operation>().ToArray();
            Active.Clear();
        }
        foreach (var operation in operations)
        {
            if (operation.Target.Dispatcher.CheckAccess()) operation.Finish();
            else if (!operation.Target.Dispatcher.HasShutdownStarted)
                operation.Target.Dispatcher.BeginInvoke((Action)operation.Finish);
        }
    }

    internal abstract class Operation : IDisposable
    {
        internal DependencyObject Target { get; }
        private bool _ended;
        protected Operation(DependencyObject target) => Target = target;
        protected void Track()
        {
            lock (Active)
            {
                Active.RemoveAll(reference => !reference.TryGetTarget(out _));
                Active.Add(new WeakReference<Operation>(this));
            }
            if (Target is FrameworkElement element)
            {
                element.Unloaded += OnUnloaded;
                element.IsEnabledChanged += OnIsEnabledChanged;
            }
        }
        protected bool End()
        {
            if (_ended) return false;
            _ended = true;
            lock (Active) Active.RemoveAll(reference => !reference.TryGetTarget(out var value) || ReferenceEquals(value, this));
            if (Target is FrameworkElement element)
            {
                element.Unloaded -= OnUnloaded;
                element.IsEnabledChanged -= OnIsEnabledChanged;
            }
            return true;
        }
        private void OnUnloaded(object sender, RoutedEventArgs e) => Dispose();
        private void OnIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is false) Finish();
        }
        public virtual void Finish() => Dispose();
        public abstract void Dispose();
    }

    private sealed class PropertyAnimation : Operation
    {
        private readonly DependencyProperty _property;
        private readonly AnimationClock _clock;
        private readonly Action? _cleanup;
        private readonly Action? _completed;
        internal PropertyAnimation(DependencyObject target, DependencyProperty property, AnimationTimeline timeline,
            Action? cleanup, Action? completed) : base(target)
        {
            _property = property;
            _cleanup = cleanup;
            _completed = completed;
            timeline.FillBehavior = FillBehavior.Stop;
            _clock = (AnimationClock)timeline.CreateClock(true);
        }
        internal void Start()
        {
            _clock.Completed += OnCompleted;
            if (Target is UIElement element) element.ApplyAnimationClock(_property, _clock, HandoffBehavior.SnapshotAndReplace);
            else if (Target is Animatable animatable) animatable.ApplyAnimationClock(_property, _clock, HandoffBehavior.SnapshotAndReplace);
            Track();
        }
        private void OnCompleted(object? sender, EventArgs e) => Finish();
        public override void Finish()
        {
            if (!Release()) return;
            _completed?.Invoke();
        }
        public override void Dispose() => Release();
        private bool Release()
        {
            if (!End()) return false;
            _clock.Completed -= OnCompleted;
            if (Animations.TryGetValue(Target, out var animations) &&
                animations.TryGetValue(_property, out var current) && ReferenceEquals(current, this))
                animations.Remove(_property);
            // Remove precisely our clock: never BeginAnimation(property, null) over a newer owner's clock.
            try { _clock.Controller?.Remove(); }
            catch { } // Dispatcher shutdown must not skip transform/attached-state cleanup.
            finally { _cleanup?.Invoke(); }
            return true;
        }
    }

    private static readonly DependencyProperty TransformLeaseProperty = DependencyProperty.RegisterAttached(
        "TransformLease", typeof(TransformLease), typeof(Motion), new PropertyMetadata(null));

    internal sealed class TransformLease : Operation
    {
        private readonly FrameworkElement _element;
        private readonly Transform? _original;
        private readonly Action? _disposed;
        private readonly TransformGroup _group = new();
        internal ScaleTransform Scale { get; }
        internal TranslateTransform Translate { get; }
        internal bool IsOwned => ReferenceEquals(_element.RenderTransform, _group);
        internal TransformLease(FrameworkElement element, double scale = 1, double offsetY = 0,
            double originX = 0.5, double originY = 0.5, Action? disposed = null) : base(element)
        {
            _element = element;
            _disposed = disposed;
            (element.GetValue(TransformLeaseProperty) as TransformLease)?.Dispose();
            _original = element.RenderTransform;
            // Preserve the original transform object (including bindings/animations) and its origin.
            Scale = new ScaleTransform(scale, scale,
                (originX - element.RenderTransformOrigin.X) * element.ActualWidth,
                (originY - element.RenderTransformOrigin.Y) * element.ActualHeight);
            Translate = new TranslateTransform(0, offsetY);
            if (_original != null) _group.Children.Add(_original);
            _group.Children.Add(Scale);
            _group.Children.Add(Translate);
            try
            {
                element.SetValue(TransformLeaseProperty, this);
                element.SetCurrentValue(UIElement.RenderTransformProperty, _group);
                Track();
            }
            catch { Dispose(); throw; }
        }
        public override void Dispose()
        {
            if (!End()) return;
            try
            {
                CancelOwned(Scale, ScaleTransform.ScaleXProperty);
                CancelOwned(Scale, ScaleTransform.ScaleYProperty);
                CancelOwned(Translate, TranslateTransform.YProperty);
            }
            finally
            {
                // A binding/style/consumer may have replaced our wrapper during the animation.
                try
                {
                    if (IsOwned) _element.SetCurrentValue(UIElement.RenderTransformProperty, _original);
                }
                finally
                {
                    if (ReferenceEquals(_element.GetValue(TransformLeaseProperty), this))
                        _element.ClearValue(TransformLeaseProperty);
                    _disposed?.Invoke();
                }
            }
        }
    }

    private static readonly ConditionalWeakTable<DependencyObject, LoopAnimation> Loops = new();
    private static void StartLoop(Storyboard storyboard, DependencyObject target)
    {
        if (Loops.TryGetValue(target, out var previous)) previous.Dispose();
        var owner = new LoopAnimation(storyboard, target);
        Loops.Add(target, owner);
        try { owner.Start(); }
        catch { owner.Dispose(); }
    }

    private sealed class LoopAnimation : Operation
    {
        private readonly Storyboard _storyboard;
        internal LoopAnimation(Storyboard storyboard, DependencyObject target) : base(target) => _storyboard = storyboard;
        internal void Start()
        {
            _storyboard.CurrentStateInvalidated += OnStateChanged;
            _storyboard.Begin(); // Parameterless Begin/Stop/Remove share the same controllable root.
            Track();
        }
        private void OnStateChanged(object? sender, EventArgs e)
        {
            if (sender is Clock { CurrentState: ClockState.Stopped }) Dispose();
        }
        public override void Dispose()
        {
            if (!End()) return;
            _storyboard.CurrentStateInvalidated -= OnStateChanged;
            if (Loops.TryGetValue(Target, out var current) && ReferenceEquals(current, this)) Loops.Remove(Target);
            try { _storyboard.Remove(); } catch { }
        }
    }
}
