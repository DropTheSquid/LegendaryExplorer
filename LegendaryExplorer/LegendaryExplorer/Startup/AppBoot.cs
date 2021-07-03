﻿using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.IO;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Threading;
using LegendaryExplorer.Dialogs.Splash;
using LegendaryExplorer.DialogueEditor;
using LegendaryExplorer.GameInterop;
using LegendaryExplorer.MainWindow;
using LegendaryExplorer.Misc;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.Misc.Telemetry;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.SharedUI.PeregrineTreeView;
using LegendaryExplorer.Tools.PackageEditor;
using LegendaryExplorer.Tools.Sequence_Editor;
using LegendaryExplorer.Tools.Soundplorer;
using LegendaryExplorer.UnrealExtensions.Classes;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Compression;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;

namespace LegendaryExplorer.Startup
{
    /// <summary>
    /// Contains the application bootup code that is shown when loading, during the splash screen
    /// </summary>
    public static class AppBoot
    {
        public static DPIAwareSplashScreen LEXSplashScreen;

        public static bool IsLoaded = false;
        public static Queue<string[]> Arguments;

        /// <summary>
        /// Invoked during the splash screen sequence for the application
        /// </summary>
        public static void Startup(App app)
        {
            LEXSplashScreen = new DPIAwareSplashScreen();
            LEXSplashScreen.Show();
            Arguments = new Queue<string[]>();
            Arguments.Enqueue(Environment.GetCommandLineArgs());

            // Prevent working directory from being locked if opened via file assoc
            // We must do this this way for single file support as it will otherwise return dll instead
            Directory.SetCurrentDirectory(Directory.GetParent(Process.GetCurrentProcess().MainModule.FileName).FullName);

            //Peregrine's Dispatcher (for WPF Treeview selecting on virtualized lists)
            DispatcherHelper.Initialize();
            Settings.LoadSettings();
            initCoreLib();

            // AppCenter setup
#if !DEBUG
            //We should only track things like this in release mode so we don't pollute our dataset
            if (Settings.Global_Analytics_Enabled && APIKeys.HasAppCenterKey)
            {
                Microsoft.AppCenter.AppCenter.Start(APIKeys.AppCenterKey,
                    typeof(Microsoft.AppCenter.Analytics.Analytics), typeof(Microsoft.AppCenter.Crashes.Crashes));
            }
#endif

            // Winforms setup
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            WindowsFormsHost.EnableWindowsFormsInterop();

            // WPF setup
            ToolTipService.ShowDurationProperty.OverrideMetadata(typeof(DependencyObject), new FrameworkPropertyMetadata(int.MaxValue));

            // Initialize VLC
            LibVLCSharp.Shared.Core.Initialize();

            //set up AppData Folder
            if (!Directory.Exists(AppDirectories.AppDataFolder))
            {
                Directory.CreateDirectory(AppDirectories.AppDataFolder);
            }

            Settings.LoadSettings();

            ToolSet.Initialize();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.Dispatcher.UnhandledException += app.OnDispatcherUnhandledException; //only start handling them after bootup

            RootCommand cliHandler = CommandLineArgs.CreateCLIHandler();
            Task.Run(() =>
            {
                //Fetch core count from WMI - this can take like 1-2 seconds
                try
                {
                    App.CoreCount = 2;
                    foreach (var item in new System.Management.ManagementObjectSearcher("Select NumberOfCores from Win32_Processor").Get())
                    {
                        App.CoreCount = int.Parse(item["NumberOfCores"].ToString());
                    }
                }
                catch
                {
                    Debug.WriteLine("Unable to determine core count from WMI, defaulting to 2");
                }

                

#if DEBUG
                //StandardLibrary.InitializeStandardLib();
#endif
            }).ContinueWithOnUIThread(x =>
            {
                IsLoaded = true;

                var mainWindow = new LEXMainWindow();
                app.MainWindow = mainWindow;
                app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                mainWindow.TransitionFromSplashToMainWindow(LEXSplashScreen);

                GameController.InitializeMessageHook(mainWindow);

                while (Arguments.Any())
                {
                    cliHandler.InvokeAsync(Arguments.Dequeue());
                }
            });
        }

        private static void initCoreLib()
        {
#if DEBUG
            MemoryAnalyzer.IsTrackingMemory = true;
#endif
            void packageSaveFailed(string message)
            {
                // I'm not sure if this requires ui thread since it's win32 but i'll just make sure
                Application.Current.Dispatcher.Invoke(() =>
                {
                    MessageBox.Show(message);
                });
            }

            LegendaryExplorerCoreLib.InitLib(TaskScheduler.FromCurrentSynchronizationContext(), packageSaveFailed);
            CoreLibSettingsBridge.MapSettingsIntoBridge();
            PackageSaver.CheckME3Running = () =>
            {
                GameController.TryGetMEProcess(MEGame.ME3, out var me3Proc);
                return me3Proc != null;
            };
            PackageSaver.NotifyRunningTOCUpdateRequired = GameController.SendME3TOCUpdateMessage;
        }

        /// <summary>
        /// Invoked when a second instance of Legendary Explorer is opened
        /// </summary>
        /// <param name="args"></param>
        public static void HandleDuplicateInstanceArgs(string[] args)
        {
            if (IsLoaded)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    CommandLineArgs.CreateCLIHandler().InvokeAsync(args);
                });
            }
            else
            {
                Arguments.Enqueue(args);
            }
        }
    }
}
