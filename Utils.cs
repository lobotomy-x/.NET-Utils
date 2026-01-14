using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Policy;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

/*
    Copyright lobotomyx 2026
    This file and all code within falls under MIT License and is free to use, modify, distribute, etc. under those terms. 

    This has been made while working on the UEVR Frontend project and should all be generalizable enough to be useful in many applications.
    
    I think nuget is evil and terrible and most nuget packages are overengineered and generally just not worth using to do simple tasks
    and aside from that I didn't want to force a dependency in a project that is not my own. That said you do need System.Management
    for WMI unless you remove those portions so regrettably nuget must be used for that (and possibly a few of the other dotnet things)

    I also cannot stand the habit every C# programmer seems to have of just making a new file for every single class, even tiny data structures,
    and conversely I think one of the best parts of C# is the ability to put an entire namespace in a single file.
    Therefore this is an atypical C# package that exists solely as this one file and can be copied and pasted into any dotnet project.

    Everything here has been tested on both dotnet 6 and dotnet 10

    You probably want to change the namespace name to your own app name, e.g. UEVR.Utils
    Then you can write `using Utils;` and call any of the class methods.
    But my recommendation would be to just write `using static Utils.ClassName;` e.g. `using static Utils.ShortcutHelper;` 
    This will allow you to directly call the class methods in your program.
*/

// Static Helper Classes
namespace Utils
 {
    // may need to expand the data you check
    public static class GitAPI {
        #region serialize
        public class Asset
        {
            public string? Name { get; set; }
            public string? Browser_Download_Url { get; set; }
        }

        public class GitHubResponseObject
        {
            public string? Tag_Name { get; set; }
            public List<Asset>? Assets { get; set; }
        }
        #endregion

        // avoids remaking a client but splits the check + download into two tasks
        public sealed class UpdateClient : IAsyncDisposable
        {
            public HttpClient Client { get; } = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            public string? DownloadUrl { get; set; }
            public string? TagName { get; set; }

            public ValueTask DisposeAsync()
            {
                Client.Dispose();
                return ValueTask.CompletedTask;
            }
        }

        // compares our local revision to the latest nightly and returns to the parent task
        // two steps because we need to ask users without autoupdate enabled if they want the update
        public static async Task<bool> CheckForUpdateAsync(UpdateClient session, string agentName, string repoUrl, string localRevision)
        {
            session.Client.DefaultRequestHeaders.UserAgent.ParseAdd(agentName);

            string response = await session.Client.GetStringAsync(repoUrl);
            if (string.IsNullOrEmpty(response))
                return false;

            var release = JsonSerializer.Deserialize<GitHubResponseObject>(
                response,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            string? tag = release?.Tag_Name;
            if (tag is not null && tag.EndsWith(localRevision, StringComparison.OrdinalIgnoreCase))
                return false;

            var asset = release?.Assets?
                .FirstOrDefault(a => a.Name?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true);

            if (asset == null)
                return false;

            session.DownloadUrl = asset.Browser_Download_Url;
            session.TagName = tag;

            return true;
        }

        // main window will handle the next steps (usage in readme for separate github post)
        public static async Task<bool> DownloadUpdateAsync(UpdateClient session, string downloadPath)
        {
            if (session.DownloadUrl is null)
                return false;

            using var resp = await session.Client.GetAsync(
                session.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead);

            if (!resp.IsSuccessStatusCode)
                return false;

            await using var fs = new FileStream(
                downloadPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true);

            await resp.Content.CopyToAsync(fs);
            return true;
        }

        
        public static async Task RunUpdateTasks(string url, string revision, string userAgentName, string downloadPath, bool automaticUpdates, Task<bool>? allowSingleUpdate)
        {
            await using var session = new UpdateClient();

            bool updateAvailable = await CheckForUpdateAsync(session, userAgentName, url, revision);
            if (!updateAvailable)
                return;

            if (!automaticUpdates)
            {
                if (allowSingleUpdate is not null)
                {
                    var result = await allowSingleUpdate;
                    if (!result) return;
                }
            }

            await DownloadUpdateAsync(session, downloadPath);
        }


  /*

  Usage Snippet: 
     string revision = "";
     try
     {
         if (File.Exists(revision_path))
             revision = File.ReadAllText(revision_path);
     }
     catch (Exception)
     {
         revision = "";
     }
  
     var downloadPath = Path.Combine(GetGlobalDir(), "UEVR", "nightly.zip");
     var url = "https://api.github.com/repos/praydog/uevr-nightly/releases/latest";
     try
     {             
      var updater = RunUpdateTasks(url, revision, "UEVR", downloadPath, canAutoUpdate, canUpdateOnce);
      
       if (!updater.IsCompletedSuccessfully)
       {
           return;
       }
     
       var nightlyDir = Path.Combine(GetGlobalDir(), "UEVR", "Nightly");
       if (!Directory.Exists(nightlyDir)) Directory.CreateDirectory(nightlyDir);

       ZipFile.ExtractToDirectory(downloadPath, nightlyDir, true);
      
      */
            
    }

    // idk if there's a more tried and true design pattern for dotnet but I think most dotnet design patterns are bad so idc either
    // I don't think these really need explanation and they do undeniably save you a few lines of code 
    //  unless you're just not checking nulls
    public static class Nullables
    {
        // the only possible oddity to mention here is that I'm expecting identical types
        // which means if you have e.g. a string? and a string you need to cast the non-nullable to nullable 
        public static bool NullableEquals(object? nullable, object? other)
        {
            if (nullable is null) return false;
            if (other is null) return false;
            if (other.GetType() != nullable.GetType()) return false;
            return nullable == other;
        }
        // might add a container version later
        public static bool NullableContains(string? nullable, string? other)
        {
            if (nullable is null) return false;
            if (other is null) return false;
            if (other.Length == 0) return false;
            return nullable.Contains(other);
        }
    }

    // UAC must be enabled to launch processes without elevation
    // Most people have it enabled and don't need this
    // if anything you can just adapt this into a generic registry key checker
    public static class UacHelper
    {
        // Registry key path for UAC settings
        private const string UacRegistryKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
        private const string EnableLUAValueName = "EnableLUA";
        public static bool IsUacEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(UacRegistryKeyPath))
                {
                    if (key != null)
                    {
                        object enableLUAValue = key.GetValue(EnableLUAValueName);

                        if (enableLUAValue != null && enableLUAValue is int)
                        {
                            // UAC is enabled if the value is non-zero (typically 1).
                            return (int)enableLUAValue != 0;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // if we failed to read it then we probably are running as a user with low perms so we'll assume its on
                return false;
            }

            return false;
        }
    }

    /*
        There's a weird void of real knowledge about shortcuts in Windows so this is kind of the juicy one.
        If you search for how to make one almost all info points towards using ancient WScript stuff from powershell
        Or encourages you to add a COM reference to your project
        Or even worse add a big nuget package to be able to work with the lnk binary format
        But you can literally just pinvoke the actual winapi stuff like anything else in Windows
        These felt odd to bury in a xaml.cs class which led me to bother making this 
        I'll confess the one thing I do vibe code is the pinvoke bindings
        But even then I really had to find the relevant winapi functions myself and directly ask for pinvoke bindings
        otherwise I kept being told to use the aforementioned methods
    */
    public static class ShortcutHelper
    {
        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        public class ShellLink { }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        public interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, out IntPtr pfd, int fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, int fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("0000010B-0000-0000-C000-000000000046")]
        public interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            void IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }

        // Lame and stupid method using powershell (still better than actually using COM in C#)
        public static void CreateShortcutPS(string shortcutPath, string targetFileLocation)
        {
            var cmd = "-Command \"$s=(New-Object -COM WScript.Shell).CreateShortcut(" +
                $"'{shortcutPath}');" +
                "$s.TargetPath=" +
               $"'{targetFileLocation}';" +
               "$s.IconLocation = $s.TargetPath + ', 0';$s.Save()\"";
            ProcessStartInfo startInfo = new ProcessStartInfo()
            {
                FileName = "powershell.exe",
                Arguments = cmd,
                UseShellExecute = false,
                CreateNoWindow = true,
                LoadUserProfile = true
            };
            Process.Start(startInfo);
        }

        // only the destination (.lnk) and target (.exe) are required
        public static void CreateShortcutNative(string shortcutPath, string target, string? args = null, string? iconPath = null, int? windowStyle = null)
        {
            var link = (IShellLinkW)new ShellLink();
            link.SetPath(target);
            if (args is not null) link.SetArguments(args);
            link.SetIconLocation(iconPath is not null ? (string)iconPath : target, 0);
            link.SetShowCmd(windowStyle is not null ? (int)windowStyle : 1); // 1 = default, 3 = maximized, 7 = minimized
            var persist = (IPersistFile)link;
            persist.Save(shortcutPath, true);
        }

        // Used in UEVR to create startup shortcuts for EGS and Steam to keep them from unnecessarily elevating
        // Which has the effect of carrying over to games, also very unnecessary
        // Games that truly need to elevate, e.g. to run an anticheat service, can still do so by asking permission
        // And the launchers can ask for privileges to install stuff and can have their background services running
        // So this doesn't do anything weird or concerning, rather it stops those programs from doing weird, concerning things
        // With this setup you basically never need to run as admin to inject into viable games which is ideal
        // Even if your app has nothing to do with injecting into games this can be very useful to have available
        public static void CreateUnelevatedShortcut(string targetFileLocation, string directory)
        {
            var shortcutPath = Path.Combine(directory, Path.GetFileNameWithoutExtension(targetFileLocation) + ".lnk");
            CreateShortcutNative(
                shortcutPath,
                "cmd.exe",
                // launch minimized cmd window and set the env var for the session
                // this env var makes it so processes will not automatically try to elevate
                // of course if you run the shortcut as admin it will run as admin
                // this also does not work whatsoever if UAC is fully disabled and the user is an admin
                "/min /C " + "\"set __COMPAT_LAYER=RUNASINVOKER && start \"\" \"" + targetFileLocation + "\"",
                targetFileLocation,
                7
            );
        }

        // You can just use File.Delete in all likelihood, this isn't necessary for COM stuff
        // its just another option if you have trouble deleting
        public static void DeleteShortcut(string shortcutPath)
        {
            var cmd = "-Command \"Remove-Item -Path \"" +
                $"{shortcutPath}" +
                "\" -Force";

            ProcessStartInfo startInfo = new ProcessStartInfo()
            {
                FileName = "powershell.exe",
                Arguments = cmd,
                UseShellExecute = false,
                CreateNoWindow = true,
                LoadUserProfile = true
            };
            Process.Start(startInfo);
        }

        // I'm only making bindings for the things I actually need but you can easily modify these two methods
        // to work with anything in the IShellLinkW interface
        public static void UpdateShortcutTarget(string shortcutPath, string newTargetPath)
        {
            var link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(shortcutPath, 0);
            link.SetPath(newTargetPath);
            ((IPersistFile)link).Save(shortcutPath, true);
        }

        // get path to the exe from existing shortcut
        public static string? GetShortcutTarget(string shortcutPath)
        {
            var link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(shortcutPath, 0);

            var sb = new StringBuilder(260);
            link.GetPath(sb, sb.Capacity, out _, 0);
            return sb.ToString();
        }

        // Placing shortcuts here will run the process on startup
        // This may require that you make an unelevated shortcut
        public static string GetShellStartupPath(string? shortcutName)
        {
            var startup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
            if (!Directory.Exists(startup))
            {
                Directory.CreateDirectory(startup);
            }
            return shortcutName is null ? startup : Path.Combine(startup, shortcutName);
        }
    }
    
    public static class ProcessManagement
    {
        /*
            Things we can do to elevated processes while unelevated
             Some may not be true if there is kernel protection 
                - check if they're running
                - get the pid
                - check if they're responding
                - get memory usage
                - get lifetime info
                - get mainwindow handle and title
                - use mainwindow handle to get window class
                - use wmi to get commandline and modules
                - kill
                - pass the mainwindowhandle or pid to an elevated service to inject
        */

        [Flags]
        public enum ProcessAccessFlags : uint
        {
            PROCESS_QUERY_LIMITED_INFORMATION = 0x00001000,
            SYNCHRONIZE = 0x00100000
        }

        // Import necessary Kernel32 functions
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(ProcessAccessFlags dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr hprocess, int dwFlags, StringBuilder lpExeName, out int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hHandle);

        public static bool IsCurrentProcessElevated()
        {
            WindowsIdentity identity = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        // tbh if you're making the process directly you could also just make bindings for CreateProcessAsUser
        public static void LaunchProcessUnelevated(string procPath)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo()
            {
                FileName = "cmd.exe",
                // sorry...
                Arguments = @$"cmd /min /C ""set __COMPAT_LAYER=RUNASINVOKER && start """" ""{procPath}""""",
                UseShellExecute = false,
                CreateNoWindow = true,
                LoadUserProfile = true
            };
            Process.Start(startInfo);
        }

        // More accurately this is checking if we can likely restart as an admin and get a handle
        // Meaning that it will return false if the process is dead or kernel protected
        public static bool IsProcessElevated(Process? p)
        {
            if (p is null || p.HasExited) return false;
            try
            {
                var handle = p.SafeHandle;
            }
            // Relying on an exception is not ideal but there's not many other ways to do this
            // probably the most reliable would be to have an elevated service that opens the process to get the token but that kind of defeats the purpose
            catch(Win32Exception e)
            {
                if(e.Message.ToString() == "Access is denied")
                {
                    // if we are elevated
                    if (!IsCurrentProcessElevated())
                        return true;
                    else // Kernel protected in all likelihood
                        return false;
                }
            }
            return false;
        }

        // I'm not aware of any cases where this would fail to get a path with a running process
        // maybe for some system procs
        public static bool IsProcessRunning(string name, out string? path)
        {
            path = null;
            foreach (var p in Process.GetProcessesByName(name))
            {
                if (p is null || p.HasExited) return false;
                try
                {
                    if (p.Responding)
                    {
                        return GetExecutablePath(p, out path);
                    }
                    else {
                      p.Kill();
                      return false;
                    }
                }
                catch  { }
            }
            return false;
        }

        // Pinvoke option for getting full path from elevated procs
        public static bool GetExecutablePath(Process p, out string? path)
        {
            path = null;
            int processId = p.Id;
            if (IsCurrentProcessElevated())
            {
                try
                {
                    var mod = p.MainModule;
                    if (mod is not null)
                    {
                        path = mod.FileName;
                        if (path is not null) return true;
                    }
                }
                catch {}
            }
            else if (IsProcessElevated(p))  // This should basically only come up if its actually an issue of the other proc being elevated
            {
                var buffer = new StringBuilder(1024);
                // Query limited information flag was added alongside protected processes and should let us get bare minimum info for anything non-kernel
                try
                {
                    IntPtr hProcess = OpenProcess(ProcessAccessFlags.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
                    if (hProcess == IntPtr.Zero) {
                      return GetExecutablePath(processId, out path);
                    }
                    int size = buffer.Capacity;
                    if (QueryFullProcessImageName(hProcess, 0, buffer, out size))
                    {
                        path = buffer.ToString();
                        CloseHandle(hProcess);
                        return true;
                    }
                    else
                    {
                        CloseHandle(hProcess);
                        return GetExecutablePath(processId, out path);
                    }
                }
                catch {}
            }
            else if (p is not null && !p.HasExited) // If we ended up here its probably a protected process or has exited
            {
                return GetExecutablePath(processId, out path);
            }
            return false;
        }


        // wmi fallback
        // This way we don't need an actual Process handle
        public static bool GetExecutablePath(int pid, out string? path)
        { 
            path = null;
            // Construct the WQL query
            string wqlQuery = $"SELECT ExecutablePath FROM Win32_Process WHERE ProcessId = {pid}";

            try
            {
                // Connect to WMI and execute the query
                using (var searcher = new ManagementObjectSearcher(wqlQuery))
                {
                    foreach (ManagementObject process in searcher.Get())
                    {
                            path = process["ExecutablePath"]?.ToString();
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error querying WMI: {ex.Message}");
            }

            return false;
        }
        
        // normally you can only get this if you monitored process creation
        public static bool GetCommandLine(int pid, out string? commandLine)
        { 
            commandLine = null;
            // Construct the WQL query
            string wqlQuery = $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}";

            try
            {
                // Connect to WMI and execute the query
                using (var searcher = new ManagementObjectSearcher(wqlQuery))
                {
                    foreach (ManagementObject process in searcher.Get())
                    {
                            commandLine = process["CommandLine"]?.ToString();
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error querying WMI: {ex.Message}");
            }

            return false;
        }
       

        /*
        Win32_ModuleLoadTrace -Property *

        'DefaultBase','FileName','ImageBase','ImageChecksum','ImageSize','ProcessID',
        'SECURITY_DESCRIPTOR','TIME_CREATED','TimeDateSTamp'
        'Caption','CommandLine','CreationClassName','CreationDate','CSCreationClassName',

        Win32_Process -Property *

        'CSName','Description','ExecutablePath','ExecutionState','Handle','HandleCount','InstallDate',
        'KernelModeTime','MaximumWorkingSetSize','MinimumWorkingSetSize','Name','OSCreationClassName',
        'OSName','OtherOperationCount','OtherTransferCount','PageFaults','PageFileUsage',
        'ParentProcessId','PeakPageFileUsage','PeakVirtualSize','PeakWorkingSetSize','Priority',
        'PrivatePageCount','ProcessId','QuotaNonPagedPoolUsage','QuotaPagedPoolUsage',
        'QuotaPeakNonPagedPoolUsage','QuotaPeakPagedPoolUsage','ReadOperationCount','ReadTransferCount','SessionId',
        'Status','TerminationDate','ThreadCount','UserModeTime','VirtualSize','WindowsVersion',
        'WorkingSetSize','WriteOperationCount','WriteTransferCount'


        Win32_ProcessStartTrace -Property *
        'ParentProcessID','ProcessID','ProcessName','SECURITY_DESCRIPTOR','SessionID','Sid',
        'TIME_CREATED'


        Win32_ThreadStartTrace -Property *
        'ProcessID','SECURITY_DESCRIPTOR','StackBase','StackLimit','StartAddr','ThreadID',
        'TIME_CREATED','UserStackBase','UserStackLimit','WaitMode','Win32StartAddr'

        Win32_Thread -Property *

        'Caption','CreationClassName','CSCreationClassName','CSName','Description',
        'ElapsedTime','ExecutionState','Handle','InstallDate','KernelModeTime','Name','OSCreationClassName',
        'OSName','Priority','PriorityBase','ProcessCreationClassName','ProcessHandle','StartAddress',
        'Status','ThreadState','ThreadWaitReason','UserModeTime'
        */

        //public static ManagementEventWatcher GetEventWatcher(string? query = null)
        //{
        //    var wmiQuery = query is not null ? query : IsCurrentProcessElevated() ?
        //            "SELECT ProcessID, ProcessName FROM Win32_ProcessStartTrace" : 
        //            $"SELECT ExecutablePath, ProcessId FROM __InstanceCreationEvent WITHIN 1 WHERE TargetInstance ISA 'Win32_Process' AND Name LIKE '%Shipping%'";
        //    var watcher = new ManagementEventWatcher(wmiQuery);

        //    return watcher;
        //}



    }

    // These are only really relevant to Unreal Engine and UEVR but could easily be adapted
    public static class WindowUtils
    {
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string? WindowClass, string? WindowName);


        delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASS
        {
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpszClassName;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern System.UInt16 RegisterClassW(
           [In] ref WNDCLASS lpWndClass
       );


        [DllImport("user32.dll", SetLastError = true)]
        static extern System.IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);


        // Delegate for the EnumWindows callback function
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);


        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);


        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);


        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
       
        
        public static List<IntPtr> EnumerateUnrealWindows()
        {
            List<IntPtr> windows = new List<IntPtr>();

            EnumWindows((hWnd, lParam) =>
            {
                const int MAX_CLASS_NAME_LENGTH = 256;
                StringBuilder classNameBuilder = new StringBuilder(MAX_CLASS_NAME_LENGTH);

                if (GetClassName(hWnd, classNameBuilder, MAX_CLASS_NAME_LENGTH) > 0)
                {
                    if (classNameBuilder.ToString() == "UnrealWindow")
                    {
                        windows.Add(hWnd);
                    }
                }
                return true;
            }, IntPtr.Zero);

            return windows;
        }

        public static bool CheckProcessForUnrealWindow(Process p)
        {
            return CheckProcessForUnrealWindow((uint)p.Id);
        }

        public static bool CheckProcessForUnrealWindow(uint pId)
        {
            // FindWindow searches all windows and processes so if this gets nothing there is no unreal process running 
            IntPtr unrealWindow = FindWindow("UnrealWindow", null);
            if (unrealWindow == IntPtr.Zero)
            {
                return false;
            }

            // FindWindow just grabs the first one it can find so we'll check that first 
            _ = GetWindowThreadProcessId(unrealWindow, out uint processId);
            if (pId == processId)
                return true;

            // We found an unreal window that didn't belong to our process so we now have to scan all windows
            // would be neat if findwindow could do repeated calls with an exclusion or something but no that would be too easy
            try
            {
                var windows = EnumerateUnrealWindows();
                foreach (var wnd in windows)
                {
                    // skip the original window
                    if (wnd == unrealWindow) continue;

                    // get process id for this window
                    _ = GetWindowThreadProcessId(wnd, out uint otherPid);

                    if (otherPid == pId)
                        return true;
                    else
                        continue;
                }
            }
            catch (Exception)
            {
                return false;
            }
            return false;
        }


        public static uint FindUnrealWindow()
        {
            IntPtr unrealWindow = FindWindow("UnrealWindow", null);
            if (unrealWindow == IntPtr.Zero)
            {
                return 0;
            }
            var excludedTitles = new string[] { "launcher", "crashreport", "ue4editor", "unrealeditor", "livecoding", "unrealinsights", "unrealswitchboard", "unrealfrontend", "livelinkhub", "zendashboard" };
            // Get the process id for the initially found window
            var tid = GetWindowThreadProcessId(unrealWindow, out uint processId);

            try
            {
                // Attempt to get the process name for the initial window
                string procName = "";
                try
                {
                    var proc = Process.GetProcessById((int)processId);
                    procName = proc?.MainModule?.FileName?.ToLowerInvariant() ?? "";
                }
                catch
                {
                    procName = "";
                }
                bool excluded = false;
                // If the initial window belongs to an excluded process, look for other UnrealWindow class windows
                foreach (var proc in excludedTitles)
                {
                    if (procName.Contains(proc)) { excluded = true; break; }
                }
                if (excluded)
                {
                    var windows = EnumerateUnrealWindows();

                    foreach (var wnd in windows)
                    {
                        // skip the original window
                        if (wnd == unrealWindow) continue;

                        // get process id for this window
                        var hresult = GetWindowThreadProcessId(wnd, out uint otherPid);

                        if (otherPid == 0 || otherPid == processId) continue;

                        try
                        {
                            var otherProc = Process.GetProcessById((int)otherPid);
                            var otherName = otherProc?.MainModule?.FileName?.ToLowerInvariant() ?? "";
                            bool other_excluded = false;
                            // return the first UnrealWindow that does NOT belong to an excluded process
                            foreach (var proc in excludedTitles)
                            {
                                if (otherName.Contains(proc)) { other_excluded = true; break; }
                            }
                            if (!other_excluded)
                            {
                                return otherPid;
                            }
                        }
                        catch
                        {
                            // ignore processes we cannot inspect and continue searching
                            continue;
                        }
                    }
                }
                else
                {
                    return processId;
                }
            }
            catch (Exception)
            {
                // fall back to returning the originally discovered process id
            }
            return 0;
        }
    }
}


