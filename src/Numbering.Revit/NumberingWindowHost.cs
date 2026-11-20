using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Numbering.Revit;

// Revit's final transaction runs on its main thread. The window has its own
// dispatcher so the user can still move, resize and scroll it during that time.
internal sealed class NumberingWindowHost
{
    private readonly ConcurrentQueue<Action<NumberingViewModel>> _updates = new();
    private Dispatcher? _dispatcher;
    private NumberingWindow? _window;
    private int _closed;
    private int _startupFailed;

    public NumberingWindowHost(IntPtr owner, NumberingSettings settings, string? settingsError,
        Action<CategoryDefinition> categoryChanged, Action<Preview> applyRequested)
    {
        var thread = new Thread(() => Run(owner, settings, settingsError, categoryChanged, applyRequested))
        {
            IsBackground = true,
            Name = "Element Numbering WPF"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public bool IsClosed => Volatile.Read(ref _closed) == 1;
    public bool StartupFailed => Volatile.Read(ref _startupFailed) == 1;

    public void Activate() => Post(_ =>
    {
        if (_window!.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window!.Activate();
    });

    public void Close() => Post(_ => _window!.Close());

    public void Post(Action<NumberingViewModel> update)
    {
        if (IsClosed)
            return;

        _updates.Enqueue(update);
        var dispatcher = Volatile.Read(ref _dispatcher);
        dispatcher?.BeginInvoke(DispatcherPriority.Normal, new Action(Drain));
    }

    private void Run(IntPtr owner, NumberingSettings settings, string? settingsError,
        Action<CategoryDefinition> categoryChanged, Action<Preview> applyRequested)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        try
        {
            var viewModel = new NumberingViewModel(settings);
            _window = new NumberingWindow(viewModel);
            new WindowInteropHelper(_window).Owner = owner;
            if (settingsError != null)
                viewModel.SetReport(settingsError, "Журнал: " + AddinLog.Path);

            var settingsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            settingsTimer.Tick += (_, _) => SaveSettings();
            viewModel.SettingsChanged += () =>
            {
                settingsTimer.Stop();
                settingsTimer.Start();
            };
            viewModel.CategoryChanged += () => categoryChanged(viewModel.Category);
            viewModel.ApplyRequested += applyRequested;

            _window.Closed += (_, _) =>
            {
                Volatile.Write(ref _closed, 1);
                if (settingsTimer.IsEnabled)
                    SaveSettings();
                viewModel.InvalidatePreview();
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            };

            _window.Show();
            Volatile.Write(ref _dispatcher, dispatcher);
            Drain();
            Dispatcher.Run();

            void SaveSettings()
            {
                settingsTimer.Stop();
                try
                {
                    SettingsStore.Save(settings);
                }
                catch (Exception error)
                {
                    AddinLog.Write("Сохранение настроек", error);
                    viewModel.SetReport("Не удалось сохранить настройки.", "Журнал: " + AddinLog.Path);
                }
            }
        }
        catch (Exception error)
        {
            AddinLog.Write("Работа окна нумерации", error);
            Volatile.Write(ref _startupFailed, 1);
        }
        finally
        {
            Volatile.Write(ref _closed, 1);
            Volatile.Write(ref _dispatcher, null);
        }
    }

    private void Drain()
    {
        if (_window?.DataContext is not NumberingViewModel viewModel)
            return;

        while (!IsClosed && _updates.TryDequeue(out var update))
            update(viewModel);
    }
}
