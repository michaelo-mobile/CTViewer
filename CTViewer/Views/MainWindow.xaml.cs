#nullable disable
using CTViewer.Tools;
using CTViewer.Views;
using Microsoft.Win32;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CTViewer.Views
{
    public partial class MainWindow : Window
    {
        private readonly ImageTool dicomTool = new ImageTool();
        private readonly DrawingCanvasTool viewerTool = new DrawingCanvasTool();
        private readonly ContrastTool contrastTool = new ContrastTool();
        //private readonly FolderTool folderTool = new FolderTool();
        private readonly DrawingManager drawingManager = new DrawingManager();

        public MainWindow()
        {
            InitializeComponent();
            drawingManager.Attach(OverlayCanvas);
           
            AnnotationMenu.StrokeSizeChanged += drawingManager.SetStrokeSize;
            AnnotationMenu.StrokeColorChanged += drawingManager.SetStrokeColor;
            AnnotationMenu.UndoClicked += drawingManager.Undo;
            AnnotationMenu.ClearClicked += drawingManager.Clear;
            AnnotationMenu.HideClicked += drawingManager.ToggleVisibility;
            AnnotationMenu.DrawModeToggled += drawingManager.SetDrawingEnabled;

        }
        private void OverlayCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (drawingManager.IsDrawing)
            {
                var pos = e.GetPosition(OverlayCanvas);
                drawingManager.BeginStroke(pos);
            }
        }

        private void OverlayCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (drawingManager.IsDrawing && e.LeftButton == MouseButtonState.Pressed)
            {
                var pos = e.GetPosition(OverlayCanvas);
                drawingManager.AddToStroke(pos);
            }
        }

        private void OpenDicom_Click(object sender, RoutedEventArgs e)
        {
            dicomTool.Attach(MainImage, OverlayCanvas);
            contrastTool.Attach(MainImage, OverlayCanvas);
            viewerTool.Attach(MainImage, OverlayCanvas);

            ResetSliders();
        }

        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            contrastTool.ApplyContrast(BrightnessSlider.Value, ContrastSlider.Value);
        }

        private void ResetSliders()
        {
            BrightnessSlider.Value = 0;
            ContrastSlider.Value = 1;
        }

        private void ResetView_Click(object sender, RoutedEventArgs e)
        {
            viewerTool.ResetTransforms();
        }

        private void ResetContrast_Click(object sender, RoutedEventArgs e)
        {
            ResetSliders();
            contrastTool.ApplyContrast(0, 1);
        }

        //        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        //        {
        //            folderTool.ShowImage(MainImage); // Only needs Image
        //            folderTool.OpenFolderAndLoadDicoms();
        //        }

        //        private void NextImage_Click(object sender, RoutedEventArgs e)
        //        {
        //            folderTool.LoadNext();
        //        }

        //        private void PreviousImage_Click(object sender, RoutedEventArgs e)
        //        {
        //            folderTool.LoadPrevious();
        //        }

        //        private void Window_KeyDown(object sender, KeyEventArgs e)
        //        {
        //            if (e.Key == Key.Right || e.Key == Key.D2)
        //                folderTool.LoadNext();
        //            else if (e.Key == Key.Left || e.Key == Key.D1)
        //                folderTool.LoadPrevious();
        //        }
    }
}
