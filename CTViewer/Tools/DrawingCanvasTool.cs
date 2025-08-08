#nullable disable
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace CTViewer.Tools
{
    public class DrawingCanvasTool : IImageTool
    {
        public string Name => "Zoom and Pan";

        private System.Windows.Controls.Image _image;
        private Canvas _overlay;

        private ScaleTransform _scaleTransform = new ScaleTransform(1.0, 1.0);
        private TranslateTransform _panTransform = new TranslateTransform(0, 0);
        private TransformGroup _transformGroup;

        private System.Windows.Point _origin;
        private System.Windows.Point _start;
        private bool _isDragging = false;

        public void Attach(System.Windows.Controls.Image image, Canvas overlay)
        {
            _image = image;
            _overlay = overlay;

            _transformGroup = new TransformGroup();
            _transformGroup.Children.Add(_scaleTransform);
            _transformGroup.Children.Add(_panTransform);
            _image.RenderTransform = _transformGroup;
            _image.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);

            _image.MouseWheel += ZoomImage_MouseWheel;
            _image.MouseLeftButtonDown += ZoomImage_MouseLeftButtonDown;
            _image.MouseMove += ZoomImage_MouseMove;
            _image.MouseLeftButtonUp += ZoomImage_MouseLeftButtonUp;
        }

        public void Detach()
        {
            if (_image != null)
            {
                _image.MouseWheel -= ZoomImage_MouseWheel;
                _image.MouseLeftButtonDown -= ZoomImage_MouseLeftButtonDown;
                _image.MouseMove -= ZoomImage_MouseMove;
                _image.MouseLeftButtonUp -= ZoomImage_MouseLeftButtonUp;

                _image.RenderTransform = null;
                _image = null;
            }
        }

        private void ZoomImage_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            double zoom = e.Delta > 0 ? 1.1 : 1 / 1.1;

            _scaleTransform.CenterX = _image.ActualWidth / 2;
            _scaleTransform.CenterY = _image.ActualHeight / 2;

            _scaleTransform.ScaleX *= zoom;
            _scaleTransform.ScaleY *= zoom;
        }

        private void ZoomImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_image == null) return;

            _isDragging = true;

            // Get position in screen coordinates
            _start = e.GetPosition(System.Windows.Application.Current.MainWindow);

            _origin = new System.Windows.Point(_panTransform.X, _panTransform.Y);

            _image.CaptureMouse();
        }

        private void ZoomImage_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_isDragging && _image != null)
            {
                // Get new position in screen coordinates
                System.Windows.Point current = e.GetPosition(System.Windows.Application.Current.MainWindow);
                Vector delta = current - _start;

                _panTransform.X = _origin.X + delta.X;
                _panTransform.Y = _origin.Y + delta.Y;
            }
        }

        private void ZoomImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isDragging = false;
            _image.ReleaseMouseCapture();
        }


        // 🔄 Public method to reset zoom and pan
        public void ResetTransforms()
        {
            _scaleTransform.ScaleX = 1.0;
            _scaleTransform.ScaleY = 1.0;
            _panTransform.X = 0;
            _panTransform.Y = 0;
        }
    }
}
//#nullable disable
//using System;
//using System.Collections.Generic;
//using System.Linq;
//using System.Text;
//using System.Threading.Tasks;
//using System.Windows.Controls;
//using System.Windows.Shapes;
//using System.Windows.Media;
//using System.Windows;

//namespace CTViewer.Tools
//{
//    public class DrawingManager
//    {
//        private Canvas _overlay;
//        private List<Polyline> _strokes = new List<Polyline>();
//        private System.Windows.Media.Brush _currentColor = System.Windows.Media.Brushes.Black;
//        private double _currentStrokeSize = 2;
//        private bool _isVisible = true;
//        private bool _isDrawingEnabled = false;

//        public void SetDrawingEnabled(bool enabled)
//        {
//            _isDrawingEnabled = enabled;
//        }

//        public void Attach(Canvas overlay)
//        {
//            _overlay = overlay;
//        }

//        public void SetStrokeColor(System.Windows.Media.Brush color)
//        {
//            _currentColor = color;
//        }

//        public void SetStrokeSize(double size)
//        {
//            _currentStrokeSize = size;
//        }

//        public void BeginStroke(System.Windows.Point point)
//        {
//            var stroke = new Polyline
//            {
//                Stroke = _currentColor,
//                StrokeThickness = _currentStrokeSize
//            };
//            stroke.Points.Add(point);
//            _strokes.Add(stroke);
//            _overlay.Children.Add(stroke);
//        }

//        public void AddToStroke(System.Windows.Point point)
//        {
//            _strokes.LastOrDefault()?.Points.Add(point);
//        }

//        public void Undo()
//        {
//            if (_strokes.Count > 0)
//            {
//                var last = _strokes.Last();
//                _overlay.Children.Remove(last);
//                _strokes.Remove(last);
//            }
//        }

//        public void Clear()
//        {
//            foreach (var stroke in _strokes)
//                _overlay.Children.Remove(stroke);
//            _strokes.Clear();
//        }

//        public void ToggleVisibility()
//        {
//            _isVisible = !_isVisible;
//            foreach (var stroke in _strokes)
//                stroke.Visibility = _isVisible ? Visibility.Visible : Visibility.Hidden;
//        }

//        // ✅ Public property to check if drawing is currently enabled
//        public bool IsDrawing => _isDrawingEnabled;
//    }
//}

