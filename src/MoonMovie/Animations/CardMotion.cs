using System.Numerics;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using MoonMovie.Services;

namespace MoonMovie.Animations;

/// <summary>
/// Xbox-style card response: a springy lift on hover/focus, raised above its neighbours, and — after a short
/// dwell — a request for the page to show this title's artwork as the ambient background.
/// </summary>
public static class CardMotion
{
    private const float LiftScale = 1.055f;
    private static readonly TimeSpan AmbientDwell = TimeSpan.FromMilliseconds(320);

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(CardMotion), new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty AmbientUrlProperty = DependencyProperty.RegisterAttached(
        "AmbientUrl", typeof(string), typeof(CardMotion), new PropertyMetadata(null));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(MotionState), typeof(CardMotion), new PropertyMetadata(null));

    public static bool GetIsEnabled(Control element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(Control element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static string? GetAmbientUrl(Control element) => (string?)element.GetValue(AmbientUrlProperty);

    public static void SetAmbientUrl(Control element, string? value) => element.SetValue(AmbientUrlProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Control control || e.NewValue is not true || control.GetValue(StateProperty) is not null)
        {
            return;
        }

        var state = new MotionState(control);
        control.SetValue(StateProperty, state);
        control.PointerEntered += (_, _) => state.SetHover(true);
        control.PointerExited += (_, _) => state.SetHover(false);
        control.PointerCanceled += (_, _) => state.SetHover(false);
        control.GotFocus += (_, _) => state.SetFocus(true);
        control.LostFocus += (_, _) => state.SetFocus(false);
        control.SizeChanged += (_, args) => state.UpdateCenter(args.NewSize);
    }

    private sealed class MotionState(Control control)
    {
        private readonly Visual _visual = ElementCompositionPreview.GetElementVisual(control);
        private DispatcherQueueTimer? _dwell;
        private bool _hover;
        private bool _focus;
        private bool _lifted;

        public void SetHover(bool value)
        {
            _hover = value;
            Update();
        }

        public void SetFocus(bool value)
        {
            // Pointer clicks also focus the button; only keyboard/gamepad focus should lift the card.
            _focus = value && control.FocusState is FocusState.Keyboard;
            Update();
        }

        public void UpdateCenter(Windows.Foundation.Size size) =>
            _visual.CenterPoint = new Vector3((float)size.Width / 2, (float)size.Height / 2, 0);

        private void Update()
        {
            var lift = _hover || _focus;
            if (lift == _lifted)
            {
                return;
            }

            _lifted = lift;
            Canvas.SetZIndex(control, lift ? 10 : 0);

            var compositor = _visual.Compositor;
            var spring = compositor.CreateSpringVector3Animation();
            spring.Target = "Scale";
            spring.FinalValue = lift ? new Vector3(LiftScale, LiftScale, 1) : Vector3.One;
            spring.DampingRatio = lift ? 0.62f : 0.9f;
            spring.Period = TimeSpan.FromMilliseconds(48);
            _visual.StartAnimation("Scale", spring);

            if (lift)
            {
                StartDwell();
            }
            else
            {
                _dwell?.Stop();
            }
        }

        private void StartDwell()
        {
            if (GetAmbientUrl(control) is null)
            {
                return;
            }

            _dwell ??= CreateDwellTimer();
            _dwell.Stop();
            _dwell.Start();
        }

        private DispatcherQueueTimer CreateDwellTimer()
        {
            var timer = control.DispatcherQueue.CreateTimer();
            timer.Interval = AmbientDwell;
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                if (_lifted && GetAmbientUrl(control) is { } url)
                {
                    WeakReferenceMessenger.Default.Send(new AmbientRequest(url));
                }
            };
            return timer;
        }
    }
}
