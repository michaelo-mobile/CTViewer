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

        public MainWindow()
        {
            InitializeComponent();
            //drawingManager.Attach(OverlayCanvas);

            //AnnotationMenu.StrokeSizeChanged += drawingManager.SetStrokeSize;
            //AnnotationMenu.StrokeColorChanged += drawingManager.SetStrokeColor;
            //AnnotationMenu.UndoClicked += drawingManager.Undo;
            //AnnotationMenu.ClearClicked += drawingManager.Clear;
            //AnnotationMenu.HideClicked += drawingManager.ToggleVisibility;
            //AnnotationMenu.DrawModeToggled += drawingManager.SetDrawingEnabled;

        }
    }
}