using System.Configuration;
using System.Data;
using System.Windows;

namespace RadialMenu1
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private static bool _exiting;

        internal static async Task RequestExitAsync()
        {
            if (_exiting) return;
            _exiting = true;
            try
            {
                await ScreenRecording.StopAsync();
                Current.Shutdown();
            }
            finally { _exiting = false; }
        }
    }

}
