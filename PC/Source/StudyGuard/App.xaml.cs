using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace StudyGuard;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private static System.Threading.Mutex? instanceMutex;
    private bool ownsInstanceMutex;
    private bool restartRequested;
    private const byte ActivateCommand = 1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    protected override void OnStartup(StartupEventArgs e)
    {
        string mutexName = $"Local\\StudyGuard-{Environment.UserName}";
        instanceMutex = new System.Threading.Mutex(initiallyOwned: true, mutexName, out ownsInstanceMutex);
        string pipeName = GetActivationPipeName();
        if (!ownsInstanceMutex)
        {
            if (!SignalExistingInstance(pipeName))
            {
                System.Windows.MessageBox.Show(
                    "يوجد إصدار آخر من StudyGuard يعمل لكنه لا يستجيب لطلب إظهار النافذة. أنهِه من أيقونة StudyGuard بجوار الساعة، ثم شغّل هذا الإصدار مجددًا.",
                    "تعذر إظهار StudyGuard",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            Shutdown();
            return;
        }

        base.OnStartup(e);
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => _ = RunActivationServerAsync(pipeName)));
    }

    private static string GetActivationPipeName()
    {
        string userHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName)))[..16];
        using System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess();
        return $"StudyGuard-{userHash}-{process.SessionId}";
    }

    private static bool SignalExistingInstance(string pipeName)
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
                client.Connect(400);
                Span<byte> processIdBytes = stackalloc byte[sizeof(int)];
                client.ReadExactly(processIdBytes);
                int processId = BitConverter.ToInt32(processIdBytes);
                AllowSetForegroundWindow(processId);
                client.WriteByte(ActivateCommand);
                client.Flush();
                return true;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
            catch (TimeoutException)
            {
                Thread.Sleep(100);
            }
        }
        return false;
    }

    private async Task RunActivationServerAsync(string pipeName)
    {
        while (ownsInstanceMutex)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync();
                byte[] processIdBytes = BitConverter.GetBytes(Environment.ProcessId);
                await server.WriteAsync(processIdBytes);
                await server.FlushAsync();
                int command = server.ReadByte();
                if (command == ActivateCommand)
                    _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(ActivateMainWindow));
            }
            catch (IOException)
            {
                // A secondary launch can disappear while sending its activation request.
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    private void ActivateMainWindow()
    {
        Window? window = MainWindow;
        if (window is null)
            return;

        window.ShowInTaskbar = true;
        if (!window.IsVisible)
            window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        window.Activate();
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
            SetForegroundWindow(handle);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsInstanceMutex && instanceMutex is not null)
            instanceMutex.ReleaseMutex();
        instanceMutex?.Dispose();
        base.OnExit(e);

        if (restartRequested && !string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            try
            {
                string executablePath = Environment.ProcessPath!;
                Process.Start(new ProcessStartInfo(executablePath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory
                });
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"أُغلقت النسخة الحالية، لكن تعذر تشغيل StudyGuard مجددًا:\n{ex.Message}",
                    "تعذر إعادة التشغيل",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }

    public void RequestRestart() => restartRequested = true;
}

