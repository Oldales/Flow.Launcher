using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.DependencyInjection;
using Flow.Launcher.Infrastructure.UserSettings;
using Flow.Launcher.ViewModel;

namespace Flow.Launcher
{
    public partial class ResultListBox
    {
        protected Lock _lock = new();
        private Point _lastpos;
        private ListBoxItem curItem = null;

        // Row appear animation (Zen Nebula style): fade in and grow from 95%, 50ms apart per row
        private const string AppearAnimationResourceKey = "ResultAppearAnimation";
        private const int AppearStaggerMs = 50;
        private const int AppearMaxStaggerRows = 8;
        private const int AppearFadeMs = 350;
        private const int AppearScaleMs = 300;
        private const double AppearStartScale = 0.95;
        private static readonly KeySpline AppearEasing = Frozen(new KeySpline(0.4, 0, 0.2, 1));

        // Selection highlight and the accent glow behind it
        private const int HighlightFadeInMs = 120;
        private const int HighlightFadeOutMs = 200;
        private const int GlowFadeMs = 500;
        private const double GlowScrollDistance = 3; // the glow gradient is three rows wide
        private static readonly TimeSpan GlowScrollDuration = TimeSpan.FromSeconds(4);
        private static readonly long GlowClockOrigin = Stopwatch.GetTimestamp();
        private static readonly IEasingFunction HighlightEasing = Frozen(new QuadraticEase { EasingMode = EasingMode.EaseOut });
        private static readonly IEasingFunction GlowEasing = Frozen(new SineEase { EasingMode = EasingMode.EaseInOut });

        private readonly Settings _settings = Ioc.Default.GetRequiredService<Settings>();
        // Row count once the last results update settled; rows loading below it are recycled, not new
        private int _settledItemCount;
        // Selected index before the last results update, so a row rebuilt while typing keeps its highlight
        private int _lastSelectedIndex = -1;
        private bool _replayPending;

        public ResultListBox()
        {
            InitializeComponent();
            ((INotifyCollectionChanged)Items).CollectionChanged += OnItemsCollectionChanged;
            IsVisibleChanged += OnIsVisibleChanged;
        }

        private static T Frozen<T>(T freezable) where T : Freezable
        {
            freezable.Freeze();
            return freezable;
        }

        private void OnItemsCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            // Updates clear and refill the list in one go; record the count once that has finished
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => _settledItemCount = Items.Count);
        }

        private void OnItemLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not ListBoxItem item)
                return;

            var index = ItemContainerGenerator.IndexFromContainer(item);
            if (item.IsSelected)
            {
                // Typing rebuilds every row; the one that was already highlighted should not fade in again
                var rebuiltInPlace = index == _lastSelectedIndex && index < _settledItemCount;
                SetHighlight(item, true, animate: !rebuiltInPlace);
            }

            if (!_replayPending && IsVisible && index >= _settledItemCount)
            {
                PlayAppearAnimation(item, index);
            }
        }

        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is not true)
            {
                // Hidden launcher: stop the glow so nothing keeps animating in the background
                ForEachRealizedRow((row, _) => SetHighlight(row, row.IsSelected, animate: false));
                return;
            }

            // Shown again: replay the row animation and fade the glow back in
            _replayPending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                _replayPending = false;
                ForEachRealizedRow((row, index) =>
                {
                    PlayAppearAnimation(row, index);
                    if (row.IsSelected)
                        SetHighlight(row, true, animate: true);
                });
            });
        }

        private void ForEachRealizedRow(Action<ListBoxItem, int> action)
        {
            for (var i = 0; i < Items.Count; i++)
            {
                if (ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem row)
                    action(row, i);
            }
        }

        private void SetHighlight(ListBoxItem row, bool selected, bool animate)
        {
            if (row.Template?.FindName("SelectionBg", row) is not UIElement selectionBg ||
                row.Template.FindName("GlowHost", row) is not UIElement glowHost ||
                row.Template.FindName("GlowShift", row) is not TranslateTransform glowShift)
                return;

            animate &= _settings.UseAnimation;

            FadeTo(selectionBg, selected ? 1 : 0,
                animate ? (selected ? HighlightFadeInMs : HighlightFadeOutMs) : 0, HighlightEasing);

            if (selected && IsVisible)
            {
                glowHost.Visibility = Visibility.Visible;
                if (!glowShift.HasAnimatedProperties)
                    StartGlowScroll(glowShift);
                FadeTo(glowHost, 1, animate ? GlowFadeMs : 0, GlowEasing);
            }
            else if (glowHost.Visibility == Visibility.Visible)
            {
                // Keep scrolling through the fade-out, then stop so unselected rows cost nothing
                FadeTo(glowHost, 0, animate ? GlowFadeMs : 0, GlowEasing, () =>
                {
                    if (row.IsSelected && IsVisible)
                        return;
                    glowHost.Visibility = Visibility.Collapsed;
                    glowShift.BeginAnimation(TranslateTransform.XProperty, null);
                });
            }
        }

        private static void FadeTo(UIElement element, double opacity, int durationMs, IEasingFunction easing, Action completed = null)
        {
            var animation = new DoubleAnimation(opacity, TimeSpan.FromMilliseconds(durationMs)) { EasingFunction = easing };
            if (completed != null)
                animation.Completed += (_, _) => completed();
            element.BeginAnimation(OpacityProperty, animation);
        }

        private static void StartGlowScroll(TranslateTransform glowShift)
        {
            // Every row shares one scroll phase, so a rebuilt row carries on where the previous one was
            var elapsed = Stopwatch.GetElapsedTime(GlowClockOrigin).Ticks % GlowScrollDuration.Ticks;
            var scroll = new DoubleAnimation(0, GlowScrollDistance, GlowScrollDuration)
            {
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromTicks(-elapsed)
            };
            glowShift.BeginAnimation(TranslateTransform.XProperty, scroll);
        }

        private void PlayAppearAnimation(ListBoxItem item, int index)
        {
            if (!_settings.UseAnimation || item.TryFindResource(AppearAnimationResourceKey) is not true)
                return;

            var delay = TimeSpan.FromMilliseconds(Math.Min(index, AppearMaxStaggerRows) * AppearStaggerMs);

            // Base scale stays 1 so the row rests at full size once the animation is released
            var scale = new ScaleTransform(1, 1);
            item.RenderTransformOrigin = new Point(0.5, 0.5);
            item.RenderTransform = scale;

            // Hold the start value through the stagger delay so the row stays hidden until its turn
            var fade = CreateAppearAnimation(0, 1, delay, AppearFadeMs);
            var grow = CreateAppearAnimation(AppearStartScale, 1, delay, AppearScaleMs);

            // Drop the transform afterwards so icons and text render untransformed on whole pixels
            fade.Completed += (_, _) =>
            {
                if (item.RenderTransform == scale)
                    item.RenderTransform = Transform.Identity;
            };

            item.BeginAnimation(OpacityProperty, fade);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }

        private static DoubleAnimationUsingKeyFrames CreateAppearAnimation(double from, double to, TimeSpan delay, int durationMs)
        {
            var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(delay)));
            animation.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(delay + TimeSpan.FromMilliseconds(durationMs)), AppearEasing));
            return animation;
        }

        public static readonly DependencyProperty RightClickResultCommandProperty =
            DependencyProperty.Register("RightClickResultCommand", typeof(ICommand), typeof(ResultListBox), new UIPropertyMetadata(null));

        public ICommand RightClickResultCommand
        {
            get
            {
                return (ICommand)GetValue(RightClickResultCommandProperty);
            }
            set
            {
                SetValue(RightClickResultCommandProperty, value);
            }
        }

        public static readonly DependencyProperty LeftClickResultCommandProperty =
            DependencyProperty.Register("LeftClickResultCommand", typeof(ICommand), typeof(ResultListBox), new UIPropertyMetadata(null));

        public ICommand LeftClickResultCommand
        {
            get
            {
                return (ICommand)GetValue(LeftClickResultCommandProperty);
            }
            set
            {
                SetValue(LeftClickResultCommandProperty, value);
            }
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0 && e.AddedItems[0] != null)
            {
                ScrollIntoView(e.AddedItems[0]);
            }

            // Rows being rebuilt have no container yet; OnItemLoaded picks those up
            foreach (var removed in e.RemovedItems)
            {
                if (ItemContainerGenerator.ContainerFromItem(removed) is ListBoxItem container)
                    SetHighlight(container, false, animate: true);
            }
            foreach (var added in e.AddedItems)
            {
                if (ItemContainerGenerator.ContainerFromItem(added) is ListBoxItem container)
                    SetHighlight(container, true, animate: true);
            }

            // Results updates clear the selection before restoring it; keep the last real index
            if (SelectedIndex >= 0)
                _lastSelectedIndex = SelectedIndex;
        }

        private void OnMouseEnter(object sender, MouseEventArgs e)
        {
            lock (_lock)
            {
                curItem = (ListBoxItem)sender;
                var p = e.GetPosition((IInputElement)sender);
                _lastpos = p;
            }
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            lock (_lock)
            {
                var p = e.GetPosition((IInputElement)sender);
                if (_lastpos != p)
                {
                    ((ListBoxItem)sender).IsSelected = true;
                }
            }
        }

        private void ListBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            lock (_lock)
            {
                if (curItem != null)
                {
                    curItem.IsSelected = true;
                }
            }
        }

        private Point _start;
        private string _path;
        private string _trimmedQuery;
        // this method is called by the UI thread, which is single threaded, so we can be sloppy with locking
        private bool _isDragging;

        private void ResultList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (Mouse.DirectlyOver is not FrameworkElement
                {
                    DataContext: ResultViewModel
                    {
                        Result:
                        {
                            CopyText: { } copyText,
                            OriginQuery.TrimmedQuery: { } trimmedQuery
                        }
                    }
                }) return;

            _path = copyText;
            _trimmedQuery = trimmedQuery;
            _start = e.GetPosition(null);
            _isDragging = true;
        }

        private void ResultList_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || !_isDragging)
            {
                _start = default;
                _path = string.Empty;
                _trimmedQuery = string.Empty;
                _isDragging = false;
                return;
            }

            if (!File.Exists(_path) && !Directory.Exists(_path))
                return;

            Point mousePosition = e.GetPosition(null);
            Vector diff = _start - mousePosition;

            if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance
                || Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            _isDragging = false;

            App.API.HideMainWindow();

            var data = new DataObject(DataFormats.FileDrop, new[]
            {
                _path
            });

            // Reassigning query to a new variable because for some reason
            // after DragDrop.DoDragDrop call, 'query' loses its content, i.e. becomes empty string
            var trimmedQuery = _trimmedQuery;
            var effect = DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move | DragDropEffects.Copy);
            if (effect == DragDropEffects.Move)
                App.API.ChangeQuery(trimmedQuery, true);
        }

        private void ResultListBox_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (Mouse.DirectlyOver is not FrameworkElement { DataContext: ResultViewModel result })
                return;

            RightClickResultCommand?.Execute(result.Result);
        }

        private void ResultListBox_OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (Mouse.DirectlyOver is not FrameworkElement { DataContext: ResultViewModel result })
                return;

            LeftClickResultCommand?.Execute(null);
        }
    }
}
