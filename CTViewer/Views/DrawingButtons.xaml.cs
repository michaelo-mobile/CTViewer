#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace CTViewer.Views
{
    /// <summary>
    /// Interaction logic for Window1.xaml
    /// </summary>
    //Add public events or callbacks in AnnotationMenu.xaml.cs:

    public partial class DrawingButtons : System.Windows.Controls.UserControl
    {
        private bool _drawMode = false;
        public event Action<double> StrokeSizeChanged;
        public event Action<System.Windows.Media.Brush> StrokeColorChanged;
        public event Action UndoClicked;
        public event Action ClearClicked;
        public event Action HideClicked;
        public event Action<bool> DrawModeToggled;

        public DrawingButtons()
        {
            InitializeComponent();
            DrawingToggleButton.Click += (s, e) =>
            {
                _drawMode = !_drawMode;
                DrawingToggleButton.Content = _drawMode ? "🛑 Stop Drawing" : "🖌️ Draw";
                DrawModeToggled?.Invoke(_drawMode);
            };

            StrokeSizeSlider.ValueChanged += (s, e) =>
                StrokeSizeChanged?.Invoke(StrokeSizeSlider.Value);

            StrokeColorComboBox.SelectionChanged += (s, e) =>
            {
                if (StrokeColorComboBox.SelectedItem is ComboBoxItem item)
                {
                    System.Windows.Media.Brush color = item.Content.ToString() switch
                    {
                        "Red" => System.Windows.Media.Brushes.Red,
                        "Blue" => System.Windows.Media.Brushes.Blue,
                        "Green" => System.Windows.Media.Brushes.Green,
                        "Yellow" => System.Windows.Media.Brushes.Yellow,
                        _ => System.Windows.Media.Brushes.Black
                    };
                    StrokeColorChanged?.Invoke(color);
                }
            };

            UndoButton.Click += (s, e) => UndoClicked?.Invoke();
            ClearButton.Click += (s, e) => ClearClicked?.Invoke();
            HideButton.Click += (s, e) => HideClicked?.Invoke();
        }
    }
}
