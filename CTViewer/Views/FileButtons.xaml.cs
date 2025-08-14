#nullable disable
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
        public event EventHandler<string>? FileOpened;
        public event EventHandler<bool>? TwoPlayerModeChanged;

        public FileButtons() => InitializeComponent();

        private void OpenButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "DICOM files (*.dcm)|*.dcm|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
                FileOpened?.Invoke(this, dlg.FileName);
        }

        private void ExitButton_Click(object sender, RoutedEventArgs e)
            => Application.Current.Shutdown();

        // Routed event so MainWindow can hook it from XAML
        public static readonly RoutedEvent SaveAsClickedEvent =
            EventManager.RegisterRoutedEvent(
                "SaveAsClicked", RoutingStrategy.Bubble,
                typeof(RoutedEventHandler), typeof(FileButtons));

        public event RoutedEventHandler SaveAsClicked
        {
            add => AddHandler(SaveAsClickedEvent, value);
            remove => RemoveHandler(SaveAsClickedEvent, value);
        }

        private void SaveAs_Click(object sender, RoutedEventArgs e)
            => RaiseEvent(new RoutedEventArgs(SaveAsClickedEvent, this)); // set Source=this

        private void TwoPlayerMode_Checked(object sender, RoutedEventArgs e)
        {
            TwoPlayerMode.Content = "🖼️  Single Pane";
            TwoPlayerModeChanged?.Invoke(this, true);
        }

        private void TwoPlayerMode_Unchecked(object sender, RoutedEventArgs e)
        {
            TwoPlayerMode.Content = "🖼️🖼️  Side by Side"; 
            TwoPlayerModeChanged?.Invoke(this, false);
        }
    }

}

