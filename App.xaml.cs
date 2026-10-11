using System.Threading;
using System.Windows;
using System.IO;
using System.Security.Principal;
using System.Windows.Threading;

namespace NeClipboardWin;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationRegistration;
    private static readonly string InstanceScope = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
    private static string MutexName => $"Global\\NeClipboardWin.{InstanceScope}.SingleInstance";
    private static string ActivationEventName => $"Global\\NeClipboardWin.{InstanceScope}.Activate";

    protected override void OnStartup(StartupEventArgs e)
    {
        string? isolatedQaDirectory = null;
        try
        {
            isolatedQaDirectory = GetIsolatedQaDirectory(e.Args);
            if (isolatedQaDirectory is not null)
            {
                var desktopQa = e.Args.Any(argument => string.Equals(argument, "--qa-desktop", StringComparison.OrdinalIgnoreCase));
                var interactiveQa = desktopQa || e.Args.Any(argument => string.Equals(argument, "--qa-interactive", StringComparison.OrdinalIgnoreCase));
                var seedQaData = e.Args.Any(argument => string.Equals(argument, "--qa-seed", StringComparison.OrdinalIgnoreCase));
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                base.OnStartup(e);
                ThemeManager.QaLightThemeOverride = GetQaThemeOverride(e.Args);
                ThemeManager.ApplyApplicationPalette();
                MainWindow = new MainWindow(isolatedQaDirectory, isolatedQaMode: true, seedQaData, desktopQaMode: desktopQa);
                if (interactiveQa)
                {
                    MainWindow.Title = desktopQa ? "NeClipboard Win — Desktop QA" : "NeClipboard Win — QA";
                    MainWindow.Show();
                }
                else
                {
                    ScheduleIsolatedQaShutdown();
                }
                return;
            }

            // Publish the activation event before claiming the mutex. An immediate
            // second launch can signal it even while the first window initializes.
            _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
            _singleInstanceMutex = new Mutex(true, MutexName, out var createdNew);
            if (!createdNew)
            {
                TryActivateRunningInstance();
                Shutdown();
                return;
            }

            _ownsSingleInstanceMutex = true;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);
            ThemeManager.ApplyApplicationPalette();
            MainWindow = new MainWindow();
            var autostart = e.Args.Any(argument => string.Equals(argument, "--autostart", StringComparison.OrdinalIgnoreCase));
            if (autostart)
                new System.Windows.Interop.WindowInteropHelper(MainWindow).EnsureHandle();
            else
                MainWindow.Show();
            _activationRegistration = ThreadPool.RegisterWaitForSingleObject(_activationEvent, (_, _) =>
            {
                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                Dispatcher.BeginInvoke(() =>
                {
                    if (!Dispatcher.HasShutdownStarted && MainWindow is NeClipboardWin.MainWindow window)
                        window.ShowPaletteFromActivation();
                });
            }, null, Timeout.Infinite, false);
        }
        catch (Exception exception)
        {
            var diagnosticsDirectory = isolatedQaDirectory
                ?? (e.Args.Any(argument => argument.StartsWith("--qa-", StringComparison.OrdinalIgnoreCase))
                    ? Path.Combine(Path.GetTempPath(), "NeClipboardWin-QA-startup-errors")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeClipboard Win"));
            try
            {
                Directory.CreateDirectory(diagnosticsDirectory);
                File.WriteAllText(Path.Combine(diagnosticsDirectory, "startup-error.log"), exception.ToString());
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            var startupError = exception is StorageUnavailableException
                ? "Запуск остановлен, чтобы не заменить недоступную историю или настройки пустыми данными. Закройте программу, которая удерживает файлы NeClipboard Win, проверьте доступ к папке данных и повторите запуск."
                : "NeClipboard Win не удалось запустить. Техническая запись сохранена локально в папке приложения.";
            System.Windows.MessageBox.Show(startupError, "NeClipboard Win", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationRegistration?.Unregister(null);
        _activationEvent?.Dispose();
        if (_ownsSingleInstanceMutex) _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void TryActivateRunningInstance()
    {
        try
        {
            using var activationEvent = EventWaitHandle.OpenExisting(ActivationEventName);
            activationEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The first instance is still initializing or already exiting.
        }
    }

    private static string? GetIsolatedQaDirectory(IReadOnlyList<string> arguments)
    {
        var argumentIndex = -1;
        for (var index = 0; index < arguments.Count; index++)
        {
            if (string.Equals(arguments[index], "--qa-isolated", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arguments[index], "--qa-desktop", StringComparison.OrdinalIgnoreCase))
            {
                argumentIndex = index;
                break;
            }
        }

        if (argumentIndex < 0) return null;
        if (argumentIndex + 1 >= arguments.Count)
            throw new ArgumentException("Для --qa-isolated требуется путь к временной папке.");

        var directory = Path.GetFullPath(arguments[argumentIndex + 1]);
        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(directory).StartsWith("NeClipboardWin-QA-", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Изолированный QA-режим разрешает только одноразовую папку NeClipboardWin-QA-* внутри %TEMP%.");

        return directory;
    }

    private void ScheduleIsolatedQaShutdown()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            // Application.Shutdown does not await the asynchronous Closing
            // handler. Let the window's normal exit path finish its queued
            // history writes before it approves shutdown instead.
            MainWindow?.Close();
        };
        timer.Start();
    }

    private static bool? GetQaThemeOverride(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (!string.Equals(arguments[index], "--qa-theme", StringComparison.OrdinalIgnoreCase)) continue;
            return arguments[index + 1].ToLowerInvariant() switch
            {
                "light" => true,
                "dark" => false,
                _ => throw new ArgumentException("Для --qa-theme допустимы только light или dark.")
            };
        }
        return null;
    }
}
