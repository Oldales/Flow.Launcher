using System;
using System.Collections.Specialized;
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
        private static readonly KeySpline AppearEasing = CreateAppearEasing();

        private readonly Settings _settings = Ioc.Default.GetRequiredService<Settings>();
        // Row count once the last results update settled; rows loading below it are recycled, not new
        private int _settledItemCount;
        private bool _replayPending;

        public ResultListBox()
        {
            InitializeComponent();
            ((INotifyCollectionChanged)Items).CollectionChanged += OnItemsCollectionChanged;
            IsVisibleChanged += OnIsVisibleChanged;
        }

        private static KeySpline CreateAppearEasing()
        {
            var spline = new KeySpline(0.4, 0, 0.2, 1);
            spline.Freeze();
            return spline;
        }

        private void OnItemsCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            // Updates clear and refill the list in one go; record the count once that has finished
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => _settledItemCount = Items.Count);
        }

        private void OnItemLoaded(object sender, RoutedEventArgs e)
        {
            if (_replayPending || !IsVisible || sender is not ListBoxItem item)
                return;

            var index = ItemContainerGenerator.IndexFromContainer(item);
            if (index >= _settledItemCount)
            {
                PlayAppearAnimation(item, index);
            }
        }

        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is not true)
                return;

            // Replay for the rows already there when the launcher (or this list) is shown again
            _replayPending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                _replayPending = false;
                for (var i = 0; i < Items.Count; i++)
                {
                    if (ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem item)
                    {
                        PlayAppearAnimation(item, i);
                    }
                }
            });
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
            animation.Freeze();
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
