using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Mapsui.Fetcher;
using Mapsui.Layers;
using Mapsui.Providers;
using Mapsui.UI.Xaml;
using Mapsui.Utilities;
using XamlVector = System.Windows.Vector;
using Mapsui.Geometries;
using SkiaSharp;
using SkiaSharp.Views;
using Point = Mapsui.Geometries.Point;

namespace Mapsui.Rendering.Skia.UI
{
    public class MapControl : Grid
    {
        private Map _map;
        private System.Windows.Point _previousMousePosition;
        private System.Windows.Point _currentMousePosition;
        private System.Windows.Point _downMousePosition;
        private readonly FpsCounter _fpsCounter = new FpsCounter();
        private readonly DoubleAnimation _zoomAnimation = new DoubleAnimation();
        private readonly Storyboard _zoomStoryBoard = new Storyboard();
        private double _toResolution = double.NaN;
        private bool _mouseDown;
        private bool _viewportInitialized;
        private bool _invalid;
        private readonly Rectangle _bboxRect;

        public event EventHandler ErrorMessageChanged;
        public event EventHandler<ViewChangedEventArgs> ViewChanged;
        public event EventHandler<MouseInfoEventArgs> MouseInfoOver;
        public event EventHandler MouseInfoLeave;
        public event EventHandler<MouseInfoEventArgs> MouseInfoUp;
        public event EventHandler<FeatureInfoEventArgs> FeatureInfo;

        public IRenderer Renderer { get; set; }
        private bool IsInBoxZoomMode { get; set; }
        [Obsolete("Use Map.HoverInfoLayers", true)]
        // ReSharper disable once UnassignedGetOnlyAutoProperty // This is here just to help upgraders
        public IList<ILayer> MouseInfoOverLayers { get; } 
        [Obsolete("Use Map.InfoLayers", true)]
        // ReSharper disable once UnassignedGetOnlyAutoProperty // This is here just to help upgraders
        public IList<ILayer> MouseInfoUpLayers { get; } 
        public event EventHandler ViewportInitialized;
        public bool ZoomToBoxMode { get; set; }

        [Obsolete("Map.Viewport instead", true)]
        public IViewport Viewport => Map.Viewport;

        private MouseInfoEventArgs _previousMouseOverEventArgs;

        private Point _scale;
        
        public Map Map
        {
            get
            {
                return _map;
            }
            set
            {
                if (_map != null)
                {
                    var temp = _map;
                    _map = null;
                    temp.DataChanged -= MapDataChanged;
                    temp.PropertyChanged -= MapPropertyChanged;
                    temp.RefreshGraphics -= MapRefreshGraphics;
                    temp.Dispose();
                }

                _map = value;
                
                if (_map != null)
                {
                    _viewportInitialized = false;
                    _map.DataChanged += MapDataChanged; 
                    _map.PropertyChanged += MapPropertyChanged;
                    _map.RefreshGraphics += MapRefreshGraphics;
                    _map.ViewChanged(true);
                }

                RefreshGraphics();
            }
        }

        private void MapRefreshGraphics(object sender, EventArgs eventArgs)
        {
            RefreshGraphics();
        }

        void MapPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!Dispatcher.CheckAccess()) Dispatcher.BeginInvoke(new Action(() => MapPropertyChanged(sender, e)));
            else
            {
                if (e.PropertyName == "Enabled")
                {
                    RefreshGraphics();
                }
                else if (e.PropertyName == "Opacity")
                {
                    RefreshGraphics();
                }
                else if (e.PropertyName == "Envelope")
                {
                    InitializeViewport();
                    _map.ViewChanged(true);
                }
                else if (e.PropertyName == "Rotation")
                {
                    _map.ViewChanged(true);
                    OnViewChanged();
                }
            }
        }

        public FpsCounter FpsCounter => _fpsCounter;

        public string ErrorMessage { get; private set; }

        public bool ZoomLocked { get; set; }
        
        private readonly SKElement _skElement;

        private readonly MapRenderer _renderer = new MapRenderer();

        // ReSharper disable once UnusedMember.Local // This registration is in order to triggers the call to OnResolutionChanged
        private static readonly DependencyProperty ResolutionProperty =
            DependencyProperty.Register(
            "Resolution", typeof(double), typeof(MapControl),
            new PropertyMetadata(OnResolutionChanged));

        private void OnPaintSurface(SKCanvas canvas, int width, int height)
        {
            if (double.IsNaN(Map.Viewport.Resolution)) return;

            Map.Viewport.Width = ActualWidth;
            Map.Viewport.Height = ActualHeight;
            
            _renderer.Render(canvas, Map.Viewport, Map.Layers, Map.BackColor);
        }

        private Point GetScale()
        {
            var presentationSource = PresentationSource.FromVisual(this);
            if (presentationSource == null) throw new Exception("PresentationSource is null");
            var compositionTarget = presentationSource.CompositionTarget;
            if (compositionTarget == null) throw new Exception("CompositionTarget is null");

            var m = compositionTarget.TransformToDevice;

            var dpiX = m.M11;
            var dpiY = m.M22;

            return new Point(dpiX, dpiY);
        }

        public MapControl()
        {
            _skElement = new SKElement
            {
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
   
            Children.Add(_skElement);
            _skElement.PaintSurface += SKElementOnPaintSurface;
            
            //Children.Add(host);
            //AddGlControl(host);

            _bboxRect = new Rectangle
                {
                    Fill = new SolidColorBrush(Colors.Red),
                    Stroke = new SolidColorBrush(Colors.Black),
                    StrokeThickness = 3,
                    RadiusX = 0.5,
                    RadiusY = 0.5,
                    StrokeDashArray = new DoubleCollection { 3.0 },
                    Opacity = 0.3,
                    VerticalAlignment = VerticalAlignment.Top,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Visibility = Visibility.Collapsed
                };
            Children.Add(_bboxRect);

            Map = new Map();
            Loaded += MapControlLoaded;
            KeyDown += MapControlKeyDown;
            KeyUp += MapControlKeyUp;
            MouseLeftButtonDown += MapControlMouseLeftButtonDown;
            MouseLeftButtonUp += MapControlMouseLeftButtonUp;

            MouseMove += MapControlMouseMove;
            MouseLeave += MapControlMouseLeave;
            MouseWheel += MapControlMouseWheel;

            SizeChanged += MapControlSizeChanged;
            CompositionTarget.Rendering += CompositionTargetRendering;
   
            ManipulationDelta += OnManipulationDelta;
            ManipulationCompleted += OnManipulationCompleted;
            ManipulationInertiaStarting += OnManipulationInertiaStarting;
            Dispatcher.ShutdownStarted += DispatcherShutdownStarted;
            IsManipulationEnabled = true;
        }

        private void SKElementOnPaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            if (!_viewportInitialized) InitializeViewport();
            if (!_viewportInitialized) return; // Stop if the line above failed. 
            if (!_invalid && !DeveloperTools.DeveloperMode) return; // In developermode always render so that fps can be counterd.

            if (_scale == null) _scale = GetScale();
            e.Surface.Canvas.Scale((float)_scale.X, (float)_scale.Y);
            OnPaintSurface(e.Surface.Canvas, e.Info.Width, e.Info.Height);
        }


        public virtual void OnViewChanged(bool userAction = false)
        {
            if (_map == null) return;

            ViewChanged?.Invoke(this, new ViewChangedEventArgs { Viewport = Map.Viewport, UserAction = userAction });
        }

        public void Refresh()
        {
            _map.ViewChanged(true);
            RefreshGraphics();
        }

        private void RefreshGraphics()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {

                InvalidateVisual();
                _invalid = true;
            }));
        }

        public void Clear()
        {
            _map?.ClearCache();
            RefreshGraphics();
        }

        public void ZoomIn()
        {
            if (ZoomLocked)
                return;

            if (double.IsNaN(_toResolution))
                _toResolution = Map.Viewport.Resolution;

            _toResolution = ZoomHelper.ZoomIn(_map.Resolutions, _toResolution);
            ZoomMiddle();
        }

        public void ZoomOut()
        {
            if (double.IsNaN(_toResolution))
                _toResolution = Map.Viewport.Resolution;

            _toResolution = ZoomHelper.ZoomOut(_map.Resolutions, _toResolution);
            ZoomMiddle();
        }

        protected void OnErrorMessageChanged(EventArgs e)
        {
            ErrorMessageChanged?.Invoke(this, e);
        }

        private static void OnResolutionChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
        {
            var newResolution = (double)e.NewValue;
            ((MapControl)dependencyObject).ZoomToResolution(newResolution);
        }

        private void ZoomToResolution(double resolution)
        {
            var current = _currentMousePosition;

            Map.Viewport.Transform(current.X, current.Y, current.X, current.Y, Map.Viewport.Resolution / resolution);

            _map.ViewChanged(true);
            OnViewChanged();
            RefreshGraphics();
        }

        private void ZoomMiddle()
        {
            _currentMousePosition = new System.Windows.Point(ActualWidth / 2, ActualHeight / 2);
            StartZoomAnimation(Map.Viewport.Resolution, _toResolution);
        }

        private void MapControlLoaded(object sender, RoutedEventArgs e)
        {
            if (!_viewportInitialized) InitializeViewport();
            UpdateSize();
            InitAnimation();
            Focusable = true;
        }

        private void InitAnimation()
        {
            _zoomAnimation.Duration = new Duration(new TimeSpan(0, 0, 0, 0, 1000));
            _zoomAnimation.EasingFunction = new QuarticEase();
            Storyboard.SetTarget(_zoomAnimation, this);
            Storyboard.SetTargetProperty(_zoomAnimation, new PropertyPath("Resolution"));
            _zoomStoryBoard.Children.Add(_zoomAnimation);
        }

        private void MapControlMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!_viewportInitialized) return;
            if (ZoomLocked) return;

            _currentMousePosition = e.GetPosition(this); //Needed for both MouseMove and MouseWheel event for mousewheel event

            if (double.IsNaN(_toResolution))
            {
                _toResolution = Map.Viewport.Resolution;
            }

            if (e.Delta > 0)
            {
                _toResolution = ZoomHelper.ZoomIn(_map.Resolutions, _toResolution);
            }
            else if (e.Delta < 0)
            {
                _toResolution = ZoomHelper.ZoomOut(_map.Resolutions, _toResolution);
            }

            e.Handled = true; //so that the scroll event is not sent to the html page.

            // Some cheating for personal gain. This workaround could be ommitted if the zoom animations was on CenterX, CenterY and Resolution, not Resolution alone.
            Map.Viewport.Center.X += 0.000000001;
            Map.Viewport.Center.Y += 0.000000001;

            StartZoomAnimation(Map.Viewport.Resolution, _toResolution);
        }

        private void StartZoomAnimation(double begin, double end)
        {
            _zoomStoryBoard.Pause(); //using Stop() here causes unexpected results while zooming very fast.
            _zoomAnimation.From = begin;
            _zoomAnimation.To = end;
            _zoomAnimation.Completed += ZoomAnimationCompleted;
            _zoomStoryBoard.Begin();
        }

        private void ZoomAnimationCompleted(object sender, EventArgs e)
        {
            _toResolution = double.NaN;
        }

        private void MapControlSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_viewportInitialized) InitializeViewport();
            Clip = new RectangleGeometry { Rect = new Rect(0, 0, ActualWidth, ActualHeight) };
            UpdateSize();
            _map.ViewChanged(true);
            OnViewChanged();
            Refresh();
        }

        private void UpdateSize()
        {
            if (Map.Viewport != null)
            {
                Map.Viewport.Width = ActualWidth;
                Map.Viewport.Height = ActualHeight;
            }
        }

        private void MapControlMouseLeave(object sender, MouseEventArgs e)
        {
            _previousMousePosition = new System.Windows.Point();
            ReleaseMouseCapture();
        }

        public void MapDataChanged(object sender, DataChangedEventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new DataChangedEventHandler(MapDataChanged), sender, e);
            }
            else
            {
                if (e == null)
                {
                    ErrorMessage = "Unexpected error: DataChangedEventArgs can not be null";
                    OnErrorMessageChanged(EventArgs.Empty);
                }
                else if (e.Cancelled)
                {
                    ErrorMessage = "Cancelled";
                    OnErrorMessageChanged(EventArgs.Empty);
                }
                else if (e.Error is System.Net.WebException)
                {
                    ErrorMessage = "WebException: " + e.Error.Message;
                    OnErrorMessageChanged(EventArgs.Empty);
                }
                else if (e.Error != null)
                {
                    ErrorMessage = e.Error.GetType() + ": " + e.Error.Message;
                    OnErrorMessageChanged(EventArgs.Empty);
                }
                else // no problems
                {
                    RefreshGraphics();
                }
            }
        }

        private void MapControlMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.StylusDevice != null) return;

            _previousMousePosition = e.GetPosition(this);
            _downMousePosition = e.GetPosition(this);
            _mouseDown = true;
            CaptureMouse();
        }

        private void MapControlMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (e.StylusDevice != null) return;

            if (IsInBoxZoomMode || ZoomToBoxMode)
            {
                ZoomToBoxMode = false;
                var previous = Map.Viewport.ScreenToWorld(_previousMousePosition.X, _previousMousePosition.Y);
                var current = Map.Viewport.ScreenToWorld(e.GetPosition(this).X, e.GetPosition(this).Y);
                ZoomToBox(previous, current);
            }
            else
            {
                HandleFeatureInfo(e);
                var eventArgs = GetMouseInfoEventArgs(e.GetPosition(this), Map.InfoLayers);
                OnMouseInfoUp(eventArgs ?? new MouseInfoEventArgs());
            }

            _map.ViewChanged(true);
            OnViewChanged(true);
            _mouseDown = false;

            _previousMousePosition = new System.Windows.Point();
            ReleaseMouseCapture();
        }

        private void HandleFeatureInfo(MouseButtonEventArgs e)
        {
            if (FeatureInfo == null) return; // don't fetch if you the call back is not set.

            if (_downMousePosition == e.GetPosition(this))
            {
                foreach (var layer in Map.Layers)
                {
                    // ReSharper disable once SuspiciousTypeConversion.Global
                    (layer as IFeatureInfo)?.GetFeatureInfo(Map.Viewport, _downMousePosition.X, _downMousePosition.Y, OnFeatureInfo);
                }
            }
        }

        private void OnFeatureInfo(IDictionary<string, IEnumerable<IFeature>> features)
        {
            FeatureInfo?.Invoke(this, new FeatureInfoEventArgs { FeatureInfo = features });
        }

        private void MapControlMouseMove(object sender, MouseEventArgs e)
        {
            if (e.StylusDevice != null) return;

            if (IsInBoxZoomMode || ZoomToBoxMode)
            {
                DrawBbox(e.GetPosition(this));
                return;
            }

            if (!_mouseDown) RaiseMouseInfoOverEvents(e.GetPosition(this));

            if (_mouseDown)
            {
                if (_previousMousePosition == default(System.Windows.Point))
                {
                    return; // It turns out that sometimes MouseMove+Pressed is called before MouseDown
                }

                _currentMousePosition = e.GetPosition(this); //Needed for both MouseMove and MouseWheel event
                Map.Viewport.Transform(_currentMousePosition.X, _currentMousePosition.Y, _previousMousePosition.X, _previousMousePosition.Y);
                _previousMousePosition = _currentMousePosition;
                _map.ViewChanged(false);
                OnViewChanged(true);
                RefreshGraphics();
            }
        }

        private void RaiseMouseInfoOverEvents(System.Windows.Point mousePosition)
        {
            var mouseOverEventArgs = GetMouseInfoEventArgs(mousePosition, Map.HoverInfoLayers);
            if (_previousMouseOverEventArgs != null && mouseOverEventArgs != null) OnMouseInfoLeave();
            else OnMouseInfoOver(mouseOverEventArgs);
            _previousMouseOverEventArgs = mouseOverEventArgs;
            
        }

        private MouseInfoEventArgs GetMouseInfoEventArgs(System.Windows.Point mousePosition, IEnumerable<ILayer> layers)
        {
            var margin = 16 * Map.Viewport.Resolution;
            var point = Map.Viewport.ScreenToWorld(new Geometries.Point(mousePosition.X, mousePosition.Y));

            foreach (var layer in layers)
            {
                var feature = layer?.GetFeaturesInView(Map.Envelope, 0)
                    .Where(f => f.Geometry.GetBoundingBox().GetCentroid().Distance(point) < margin)
                    .OrderBy(f => f.Geometry.GetBoundingBox().GetCentroid().Distance(point))
                    .FirstOrDefault();

                if (feature != null)
                {
                    return new MouseInfoEventArgs { LayerName = layer.Name, Feature = feature };
                }
            }
            return null;
        }

        protected void OnMouseInfoLeave()
        {
            MouseInfoLeave?.Invoke(this, new EventArgs());
        }

        protected void OnMouseInfoOver(MouseInfoEventArgs e)
        {
            MouseInfoOver?.Invoke(this, e);
        }

        protected void OnMouseInfoUp(MouseInfoEventArgs e)
        {
            MouseInfoUp?.Invoke(this, e);
        }

        private void InitializeViewport()
        {
            if (ActualWidth.IsNanOrZero()) return;

            if (double.IsNaN(Map.Viewport.Resolution)) // only when not set yet
            {
                if (!_map.Envelope.IsInitialized()) return;
                if (_map.Envelope.GetCentroid() == null) return;

                if (Math.Abs(_map.Envelope.Width) > Constants.Epsilon)
                    Map.Viewport.Resolution = _map.Envelope.Width / ActualWidth;
                else
                    // An envelope width of zero can happen when there is no data in the Maps' layers (yet).
                    // It should be possible to start with an empty map.
                    Map.Viewport.Resolution = Constants.DefaultResolution;
            }
            if (double.IsNaN(Map.Viewport.Center.X) || double.IsNaN(Map.Viewport.Center.Y)) // only when not set yet
            {
                if (!_map.Envelope.IsInitialized()) return;
                if (_map.Envelope.GetCentroid() == null) return;

                Map.Viewport.Center = _map.Envelope.GetCentroid();
            }

            Map.Viewport.Width = ActualWidth;
            Map.Viewport.Height = ActualHeight;

            Map.Viewport.RenderResolutionMultiplier = 1.0;

            _viewportInitialized = true;

            OnViewportInitialize();

            Map.ViewChanged(true);
        }

        private void OnViewportInitialize()
        {
            ViewportInitialized?.Invoke(this, EventArgs.Empty);
        }

        private void CompositionTargetRendering(object sender, EventArgs e)
        {
            if (!_viewportInitialized) InitializeViewport();
            if (!_viewportInitialized) return; // Stop if the line above failed. 
            if (!_invalid && !DeveloperTools.DeveloperMode) return; // In developermode always render so that fps can be counterd.
            
            _skElement.InvalidateVisual();
        }
        
        private void DispatcherShutdownStarted(object sender, EventArgs e)
        {
            CompositionTarget.Rendering -= CompositionTargetRendering;
            _map?.Dispose();
        }
        
        public void ZoomToBox(Geometries.Point beginPoint, Geometries.Point endPoint)
        {
            double x, y, resolution;
            var width = Math.Abs(endPoint.X - beginPoint.X);
            var height = Math.Abs(endPoint.Y - beginPoint.Y);
            if (width <= 0) return;
            if (height <= 0) return;

            ZoomHelper.ZoomToBoudingbox(beginPoint.X, beginPoint.Y, endPoint.X, endPoint.Y, 
                ActualWidth, ActualHeight, out x, out y, out resolution);
            resolution = ZoomHelper.ClipResolutionToExtremes(_map.Resolutions, resolution);

            Map.Viewport.Center = new Geometries.Point(x, y);
            Map.Viewport.Resolution = resolution;
            _toResolution = resolution;

            _map.ViewChanged(true);
            OnViewChanged(true);
            RefreshGraphics();
            ClearBBoxDrawing();
        }

        private void ClearBBoxDrawing()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _bboxRect.Margin = new Thickness(0, 0, 0, 0);
                _bboxRect.Width = 0;
                _bboxRect.Height = 0;
            }));
        }

        private void MapControlKeyUp(object sender, KeyEventArgs e)
        {
            var keyName = e.Key.ToString().ToLower();
            if (keyName.Equals("ctrl") || keyName.Equals("leftctrl") || keyName.Equals("rightctrl"))
            {
                IsInBoxZoomMode = false;
            }
        }

        private void MapControlKeyDown(object sender, KeyEventArgs e)
        {
            var keyName = e.Key.ToString().ToLower();
            if (keyName.Equals("ctrl") || keyName.Equals("leftctrl") || keyName.Equals("rightctrl"))
            {
                IsInBoxZoomMode = true;
            }
        }

        private void DrawBbox(System.Windows.Point newPos)
        {
            if (!_mouseDown) return;

            var from = _previousMousePosition;
            var to = newPos;

            if (from.X > to.X)
            {
                var temp = from;
                from.X = to.X;
                to.X = temp.X;
            }

            if (from.Y > to.Y)
            {
                var temp = from;
                from.Y = to.Y;
                to.Y = temp.Y;
            }

            _bboxRect.Width = to.X - from.X;
            _bboxRect.Height = to.Y - from.Y;
            _bboxRect.Margin = new Thickness(from.X, from.Y, 0, 0);
        }

        public void ZoomToFullEnvelope()
        {
            if (Map.Envelope == null) return;
            if (ActualWidth.IsNanOrZero()) return;
            Map.Viewport.Resolution = Math.Max(Map.Envelope.Width/ActualWidth, Map.Envelope.Height/ActualHeight);
            Map.Viewport.Center = Map.Envelope.GetCentroid();
        }

        private static void OnManipulationInertiaStarting(object sender, ManipulationInertiaStartingEventArgs e)
        {
            e.TranslationBehavior.DesiredDeceleration = 25 * 96.0 / (1000.0 * 1000.0);
        }

        private void OnManipulationDelta(object sender, ManipulationDeltaEventArgs e)
        {
            var previousX = e.ManipulationOrigin.X;
            var previousY = e.ManipulationOrigin.Y;
            var currentX = e.ManipulationOrigin.X + e.DeltaManipulation.Translation.X;
            var currentY = e.ManipulationOrigin.Y + e.DeltaManipulation.Translation.Y;
            var deltaScale = GetDeltaScale(e.DeltaManipulation.Scale);

            Map.Viewport.Transform(currentX, currentY, previousX, previousY, deltaScale);

            _invalid = true;
            OnViewChanged(true);
            e.Handled = true;
        }

        private double GetDeltaScale(XamlVector scale)
        {
            if (ZoomLocked) return 1;
            var deltaScale = (scale.X + scale.Y) / 2;
            if (Math.Abs(deltaScale) < Constants.Epsilon) return 1; // If there is no scaling the deltaScale will be 0.0 in Windows Phone (while it is 1.0 in wpf)
            if (!(Math.Abs(deltaScale - 1d) > Constants.Epsilon)) return 1;
            return deltaScale;
        }

        private void OnManipulationCompleted(object sender, ManipulationCompletedEventArgs e)
        {
            Refresh();
        }
    }

    public class ViewChangedEventArgs : EventArgs
    {
        public IViewport Viewport { get; set; }
        public bool UserAction { get; set; }
    }

    public class MouseInfoEventArgs : EventArgs
    {
        public MouseInfoEventArgs()
        {
            LayerName = string.Empty;
        }

        public string LayerName { get; set; }
        public IFeature Feature { get; set; }
    }

    public class FeatureInfoEventArgs : EventArgs
    {
        public IDictionary<string, IEnumerable<IFeature>> FeatureInfo { get; set; }
    }
}