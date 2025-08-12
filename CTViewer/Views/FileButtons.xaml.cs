using System;
using System.Collections.Generic;
using System.Linq;
#nullable disable
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace CTViewer.Views
{
    /// <summary>
    /// Interaction logic for Documentation.xaml
    /// </summary>
    public partial class FileButtons : UserControl
    {
        public event EventHandler<bool> TwoPlayerModeChanged;
        public FileButtons()
        {
            InitializeComponent();
        }
        private void OpenButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog();
            dialog.Filter = "DICOM files (*.dcm)|*.dcm|All files (*.*)|*.*";
            if (dialog.ShowDialog() == true)
            {
                FileOpened?.Invoke(this, dialog.FileName);

            }

        }
        public event EventHandler<string> FileOpened;

        private void ExitButton_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }
        public static readonly RoutedEvent SaveAsClickedEvent =
        EventManager.RegisterRoutedEvent(
            "SaveAsClicked", RoutingStrategy.Bubble,
            typeof(RoutedEventHandler), typeof(FileButtons));

        public event RoutedEventHandler SaveAsClicked
        {
            add { AddHandler(SaveAsClickedEvent, value); }
            remove { RemoveHandler(SaveAsClickedEvent, value); }
        }

        // wire this to your Save As button in FileButtons.xaml: Click="SaveAsBtn_Click"
        private void SaveAs_Click(object sender, RoutedEventArgs e)
        {      RaiseEvent(new RoutedEventArgs(SaveAsClickedEvent));
    }
        private void TwoPlayerMode_Checked(object sender, RoutedEventArgs e)
        {
            // Handle the Two Player Mode checkbox checked event
            // You can add your logic here
            TwoPlayerModeChanged?.Invoke(this, true);
        }
        private void TwoPlayerMode_Unchecked(object sender, RoutedEventArgs e)
        {
            // Handle the Two Player Mode checkbox unchecked event
            // You can add your logic here
            TwoPlayerModeChanged?.Invoke(this, false);
        }
    }
}
