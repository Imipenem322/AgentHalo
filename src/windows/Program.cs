using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using DrawingColor = System.Drawing.Color;
using MediaColor = System.Windows.Media.Color;
using MediaBrush = System.Windows.Media.Brush;
using MediaPen = System.Windows.Media.Pen;
using MediaPoint = System.Windows.Point;
[assembly: System.Reflection.AssemblyTitle("AgentHalo DSH")]
[assembly: System.Reflection.AssemblyDescription("Desktop status light for DeepSeek Harness")]
[assembly: System.Reflection.AssemblyCompany("Agent Halo")]
[assembly: System.Reflection.AssemblyProduct("AgentHalo DSH")]
[assembly: System.Reflection.AssemblyVersion(CodexHalo.GeneratedHaloSpec.ReleaseVersion + ".0")]
[assembly: System.Reflection.AssemblyFileVersion(CodexHalo.GeneratedHaloSpec.ReleaseVersion + ".0")]
[assembly: System.Reflection.AssemblyInformationalVersion(CodexHalo.GeneratedHaloSpec.ReleaseVersion)]

namespace CodexHalo
{
public static class Program
    {
        private static Mutex mutex;

        [STAThread]
        public static int Main()
        {
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length == 3 && args[1] == "--dsh-self-test")
                return Diagnostics.RunDeepSeekSelfTest(args[2]);
            if (args.Length == 3 && args[1] == "--deepseek-snapshot")
                return DeepSeekHarnessSnapshotCommand.Run(args[2]);

            bool managed = args.Length == 2 && args[1] == "--dsh-managed";
            if (args.Length != 1 && !managed)
            {
                Console.Error.WriteLine("Unsupported AgentHalo DSH arguments.");
                return 2;
            }
            // The inherited pipe belongs only to this plugin activation. EOF
            // covers both normal disposal and an abruptly terminated Host.
            if (managed && GetFileType(GetStdHandle(-10)) != 3)
            {
                Console.Error.WriteLine("--dsh-managed requires a parent stdin pipe.");
                return 2;
            }
            if (!managed)
            {
                DetachEphemeralConsole();
            }

            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.DispatcherUnhandledException += delegate(object sender,
                DispatcherUnhandledExceptionEventArgs e)
            {
                SettingsStorage.Log("Unhandled UI error: " + e.Exception);
                e.Handled = true;
            };

            bool created;
            mutex = new Mutex(true, "Local\\AgentHaloDSH-9A542DB9-A944-4B38-AED0-84BE7419D0BB",
                out created);
            if (!created)
            {
                mutex.Dispose();
                return 10;
            }
            try
            {
                HaloSettings settings;
                try
                {
                    settings = SettingsStorage.Load();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Cannot load AgentHalo DSH settings: " + ex.Message);
                    return 1;
                }
                HaloWindow window = new HaloWindow(settings);
                app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                app.MainWindow = window;
                window.Show();
                if (managed)
                {
                    Thread lifetime = new Thread(delegate()
                    {
                        try
                        {
                            using (Stream input = Console.OpenStandardInput())
                            {
                                byte[] buffer = new byte[64];
                                while (input.Read(buffer, 0, buffer.Length) != 0) { }
                            }
                        }
                        catch (IOException) { }
                        finally
                        {
                            if (!app.Dispatcher.HasShutdownStarted)
                                app.Dispatcher.BeginInvoke(new Action(window.Close));
                        }
                    });
                    lifetime.IsBackground = true;
                    lifetime.Name = "DSH plugin lifetime";
                    lifetime.Start();
                }
                app.Run();
                return 0;
            }
            finally
            {
                mutex.ReleaseMutex();
                mutex.Dispose();
            }
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetStdHandle(int handle);

        [DllImport("kernel32.dll")]
        private static extern uint GetFileType(IntPtr handle);

        private static void DetachEphemeralConsole()
        {
            try
            {
                IntPtr console = GetConsoleWindow();
                if (console == IntPtr.Zero)
                {
                    return;
                }
                uint[] processes = new uint[8];
                uint count = GetConsoleProcessList(processes, (uint)processes.Length);
                if (count <= 1)
                {
                    ShowWindow(console, 0);
                }
                FreeConsole();
            }
            catch
            {
            }
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("kernel32.dll")]
        private static extern uint GetConsoleProcessList(
            [Out] uint[] processList, uint processCount);

        [DllImport("kernel32.dll")]
        private static extern bool FreeConsole();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }
}
