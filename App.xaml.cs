using System;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace MIDILINK
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        // Single instance: the mutex tells whether MIDILINK already runs in this session,
        // the event lets a second launch ask the running instance to show its window.
        private const string MutexName = @"Local\MidiLink_SingleInstance";
        private const string ShowEventName = @"Local\MidiLink_ShowWindow";
        private Mutex? _instanceMutex;
        private EventWaitHandle? _showEvent;
        private RegisteredWaitHandle? _showWait;

        public App()
        {
            // Last line of defense: an unexpected error (e.g. a MIDI device vanishing mid-call)
            // is reported in the status bar instead of closing the application.
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            // Cannot be prevented (the process ends), but at least leave a trace in the log
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                MidiLink.AppLog.Error("Fatal unhandled exception", e.ExceptionObject as Exception);
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);
            if (!isFirstInstance)
            {
                // Already running: bring it to the front (unless this is the automatic launch
                // at Windows startup, which must stay silent) and quit before opening any MIDI port.
                bool silent = MidiLink.StartupManager.IsStartupLaunch(e.Args);
                if (!silent)
                {
                    try
                    {
                        using var showEvent = EventWaitHandle.OpenExisting(ShowEventName);
                        showEvent.Set();
                    }
                    catch (Exception ex)
                    {
                        MidiLink.AppLog.Error("Cannot reach the running instance", ex);
                    }
                }
                MidiLink.AppLog.Info("Second launch: already running, exiting");
                _instanceMutex.Dispose();
                _instanceMutex = null;
                Shutdown();
                return;
            }

            MidiLink.AppLog.Info("Application started");

            // Listen for later launches asking to show the window
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, __) =>
                Dispatcher.BeginInvoke(() => (MainWindow as MidiLink.MainWindow)?.RestoreFromTray()),
                null, Timeout.Infinite, executeOnlyOnce: false);

            MainWindow = new MidiLink.MainWindow();
            MainWindow.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _showWait?.Unregister(null);
            _showEvent?.Dispose();
            if (_instanceMutex != null)
            {
                try { _instanceMutex.ReleaseMutex(); } catch { }
                _instanceMutex.Dispose();
            }
            base.OnExit(e);
        }

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            MidiLink.AppLog.Error("Unhandled UI exception", e.Exception);
            ShowError(e.Exception);
            e.Handled = true;
        }

        private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            MidiLink.AppLog.Error("Unobserved task exception", e.Exception);
            ShowError(e.Exception.InnerException ?? e.Exception);
            e.SetObserved();
        }

        private static void ShowError(Exception ex)
        {
            try
            {
                Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (Current?.MainWindow is MidiLink.MainWindow mw)
                        mw.StatusText = $"Unexpected error: {ex.Message}";
                });
            }
            catch { }
        }
    }

}
