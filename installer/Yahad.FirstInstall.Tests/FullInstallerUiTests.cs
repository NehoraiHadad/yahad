using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Yahad.Setup;
internal static class FullInstallerUiTests
{
    internal static async Task Run(string output)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            try
            {
                var application=new App();application.InitializeComponent();application.ShutdownMode=ShutdownMode.OnExplicitShutdown;
                Directory.CreateDirectory(output);
                foreach(var existing in new[]{false,true})
                {
                    var window=new MainWindow(false,[],false,existing);var primary=(Button)window.FindName("Primary");var consent=(CheckBox)window.FindName("UpdateConsent");
                    var choices=(StackPanel)window.FindName("UpdateChoices");
                    if(existing){if(choices.Visibility!=Visibility.Collapsed||!primary.IsEnabled)throw new Exception("PRESERVED_CONSENT_UI");}
                    else
                    {
                        if(primary.IsEnabled||choices.Visibility!=Visibility.Visible)throw new Exception("FRESH_CONSENT_UI");
                        ((RadioButton)window.FindName("NotifyUpdates")).IsChecked=true;consent.IsChecked=true;if(!primary.IsEnabled)throw new Exception("NOTIFY_CONSENT_UI");
                        consent.IsChecked=false;if(primary.IsEnabled)throw new Exception("CONSENT_UI_REVOKE");consent.IsChecked=true;
                    }
                    var content=(FrameworkElement)window.Content;content.Width=980;content.Height=730;content.Measure(new Size(980,730));content.Arrange(new Rect(0,0,980,730));content.UpdateLayout();
                    var scroll=(ScrollViewer)window.FindName("ContentScroll");scroll.ScrollToEnd();content.UpdateLayout();
                    if(window.IsVisible)throw new Exception("SYNTHETIC_WINDOW_WAS_SHOWN");
                    var image=new RenderTargetBitmap(980,730,96,96,PixelFormats.Pbgra32);image.Render(content);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));
                    using var file=File.Create(Path.Combine(output,existing?"preserved-update-choice.png":"fresh-update-choice.png"));png.Save(file);window.Close();
                }
                done.SetResult();
            }
            catch(Exception error){done.SetException(error);}
        }){IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();await done.Task;
    }
}
