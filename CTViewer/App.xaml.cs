using System.Configuration;
using System.Data;
using System.Windows;

namespace CTViewer
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            MessageBox.Show("OnStartup triggered: launching MainWindow");
            var window = new CTViewer.Views.MainWindow();
            MainWindow.Show();
        }
    }


}
