using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;

namespace RefreshRateOverlay.WPF.Tests;

/// <summary>
/// WPF objects (Window, Ellipse, ComboBox, ...) require construction on an STA
/// thread with a running Dispatcher — xunit's own test-runner threads are MTA
/// and have none. OverlayWindow.xaml also references button styles
/// (FlatButton/IconButton) defined in App.xaml's Application.Resources, which
/// only resolve if a System.Windows.Application exists with those resources
/// merged in — normal at runtime (App.xaml starts one), never true by default
/// in a test process.
///
/// One background STA thread runs for the lifetime of the test process, with
/// a real Application constructed exactly once (WPF disallows a second) and
/// App.xaml's resources merged onto it. Every test marshals its work onto
/// that same thread via Dispatcher.Invoke rather than spinning up a new
/// thread each time — that would either violate WPF's "one Application per
/// process" rule or hit cross-thread ownership exceptions accessing
/// Application.Current.Resources from a different thread than the one that
/// created it.
/// </summary>
internal static class StaTestHelper
{
    private static readonly Dispatcher Dispatcher;

    static StaTestHelper()
    {
        using var ready = new ManualResetEventSlim();
        Dispatcher? dispatcher = null;

        var thread = new Thread(() =>
        {
            // App.xaml's root is <Application x:Class="...App">, so its
            // generated App.g.cs already derives from Application — loading
            // it as a bare ResourceDictionary.Source would construct a
            // *second* Application (App.xaml is parsed top-down) and throw.
            // Constructing App itself and calling its generated
            // InitializeComponent() sets Application.Current and merges
            // Application.Resources (FlatButton/IconButton) in one step,
            // without running App.xaml.cs's OnStartup (that only fires from
            // .Run(), never called here).
            var app = new RefreshRateOverlay.WPF.App();
            app.InitializeComponent();

            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true, // never blocks the test process from exiting
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        ready.Wait();
        Dispatcher = dispatcher!;
    }

    public static void Run(Action action)
    {
        Exception? captured = null;
        Dispatcher.Invoke(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
        });

        if (captured is not null)
            ExceptionDispatchInfo.Capture(captured).Throw();
    }
}
