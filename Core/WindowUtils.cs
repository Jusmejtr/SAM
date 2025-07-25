﻿using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using SteamAuth;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using Win32Interop.WinHandles;

namespace SAM.Core
{
    class WindowUtils
    {
        #region dll imports

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern int GetWindowThreadProcessId(IntPtr handle, out int processId);

        [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr SendMessage(IntPtr hWnd, UInt32 Msg, IntPtr wParam, [Out] StringBuilder lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, int wParam, IntPtr lParam);

        delegate bool EnumThreadDelegate(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool EnumThreadWindows(int dwThreadId, EnumThreadDelegate lpfn, IntPtr lParam);

        #endregion

        public const int WM_GETTEXT = 0xD;
        public const int WM_GETTEXTLENGTH = 0xE;

        private static bool loginAllCancelled = false;

        private static IEnumerable<IntPtr> EnumerateProcessWindowHandles(Process process)
        {
            var handles = new List<IntPtr>();

            foreach (ProcessThread thread in process.Threads)
                EnumThreadWindows(thread.Id, (hWnd, lParam) => { handles.Add(hWnd); return true; }, IntPtr.Zero);

            return handles;
        }

        private static string GetWindowTextRaw(IntPtr hwnd)
        {
            // Allocate correct string length first
            int length = (int)SendMessage(hwnd, WM_GETTEXTLENGTH, 0, IntPtr.Zero);
            StringBuilder sb = new StringBuilder(length + 1);
            SendMessage(hwnd, WM_GETTEXT, (IntPtr)sb.Capacity, sb);
            return sb.ToString();
        }

        public static IEnumerable<Process> GetChildProcesses(Process process)
        {
            List<Process> children = new List<Process>();
            try
            {
                ManagementObjectSearcher mos = new ManagementObjectSearcher(String.Format("Select * From Win32_Process Where ParentProcessID={0}", process.Id));
                foreach (ManagementObject mo in mos.Get())
                {
                    children.Add(Process.GetProcessById(Convert.ToInt32(mo["ProcessID"])));
                }
            }
            catch(Exception e) { 
                Console.WriteLine(e.Message);
            }

            return children;
        }

        public static WindowHandle GetSteamLoginWindow(Process steamProcess)
        {
            IEnumerable<Process> children = GetChildProcesses(steamProcess);
            foreach (Process childProcess in children)
            {
                if (childProcess.ProcessName == "steamwebhelper")
                {
                    IEnumerable<IntPtr> windows = EnumerateProcessWindowHandles(childProcess);
                    return GetSteamLoginWindow(windows);
                }
            }

            return WindowHandle.Invalid;
        }

        public static Process GetSteamProcess()
        {
            Process[] steamProcess = Process.GetProcessesByName("Steam");
            if (steamProcess.Length > 0)
            {
                return steamProcess[0];
            }
            return null;
        }

        public static WindowHandle GetSteamLoginWindow()
        {
            Process[] steamProcess = Process.GetProcessesByName("Steam");
            foreach (Process process in steamProcess)
            {
                WindowHandle handle = GetSteamLoginWindow(process);
                if (handle.IsValid)
                {
                    return handle;
                }
            }

            return WindowHandle.Invalid;
        }

        private static WindowHandle GetSteamLoginWindow(IEnumerable<IntPtr> windows)
        {
            foreach (IntPtr windowHandle in windows)
            {
                string text = GetWindowTextRaw(windowHandle);

                if ((text.Contains("Steam") && text.Length > 5) || text.Equals("蒸汽平台登录"))
                {
                    return new WindowHandle(windowHandle);
                }
            }

            return WindowHandle.Invalid;
        }

        public static WindowHandle GetMainSteamClientWindow(Process steamProcess)
        {
            IEnumerable<IntPtr> windows = EnumerateProcessWindowHandles(steamProcess);
            return GetMainSteamClientWindow(windows);
        }

        public static WindowHandle GetMainSteamClientWindow(string processName)
        {
            Process[] steamProcess = Process.GetProcessesByName(processName);
            foreach (Process process in steamProcess)
            {
                IEnumerable<IntPtr> windows = EnumerateProcessWindowHandles(process);

                WindowHandle handle = GetMainSteamClientWindow(windows);
                if (handle.IsValid)
                {
                    return handle;
                }
            }

            return WindowHandle.Invalid;
        }

        private static WindowHandle GetMainSteamClientWindow(IEnumerable<IntPtr> windows)
        {
            foreach (IntPtr windowHandle in windows)
            {
                string text = GetWindowTextRaw(windowHandle);

                if (text.Equals("Steam") || text.Equals("蒸汽平台"))
                {
                   return new WindowHandle(windowHandle);
                }
            }

            return WindowHandle.Invalid;
        }

        public static bool IsSteamUpdating(Process process)
        {
            WindowHandle windowHandle = GetMainSteamClientWindow(process);

            if (windowHandle.IsValid)
            {
                using (var automation = new UIA3Automation())
                {
                    try
                    {
                        AutomationElement window = automation.FromHandle(windowHandle.RawPtr);

                        if (window == null)
                        {
                            return false;
                        }

                        if (window.Properties.ClassName.Equals("BootstrapUpdateUIClass") && window.Properties.BoundingRectangle.Value.X > 312)
                        {
                            return true;
                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.Message);
                    }
                }
            }

            return false;
        }

        public static LoginWindowState GetLoginWindowState(WindowHandle loginWindow)
        {
            if (!loginWindow.IsValid)
            {
                return LoginWindowState.Invalid;
            }

            using (var automation = new UIA3Automation())
            {
                try
                {
                    AutomationElement window = automation.FromHandle(loginWindow.RawPtr);

                    if (window == null)
                    {
                        return LoginWindowState.Invalid;
                    }

                    window.Focus();

                    AutomationElement document = window.FindFirstDescendant(e => e.ByControlType(ControlType.Document));
                    AutomationElement[] children = document.FindAllChildren();

                    if (children.Length == 0)
                    {
                        return LoginWindowState.Invalid;
                    }

                    if (children.Length == 2)
                    {
                        return LoginWindowState.Loading;
                    }

                    var inputs = new List<AutomationElement>();
                    var buttons = new List<AutomationElement>();
                    var groups = new List<AutomationElement>();
                    var images = new List<AutomationElement>();
                    var texts = new List<AutomationElement>();

                    foreach (AutomationElement element in children) {
                        switch (element.ControlType) {
                            case ControlType.Edit:
                                inputs.Add(element);
                                break;
                            case ControlType.Button:
                                buttons.Add(element);
                                break;
                            case ControlType.Group:
                                groups.Add(element);
                                break;
                            case ControlType.Image:
                                images.Add(element); 
                                break;
                            case ControlType.Text:
                                texts.Add(element);
                                break;
                        }
                    }

                    Console.WriteLine("Inputs: " + inputs.Count + " Buttons: " + buttons.Count + " Groups: " + groups.Count + " Images: " + images.Count + " Texts: " + texts.Count);

                    if (inputs.Count == 0 && images.Count == 1 && buttons.Count == 2 && texts.Count > 0)
                    {
                        foreach (var text in texts)
                        {
                            string content = text.Name.ToLower();

                            if (content.Contains("error") || content.Contains("problem"))
                            {
                                return LoginWindowState.Error;
                            }
                        }
                    }
                    if (texts.Count == 2 && images.Count == 1 && buttons.Count == 1)
                    {
                        return LoginWindowState.Error;
                    }
                    else if (inputs.Count == 0 && images.Count >= 2 && buttons.Count > 0 && texts.Count == 0)
                    {
                        return LoginWindowState.Selection;
                    }
                    else if (inputs.Count == 0 && buttons.Count == 5 && groups.Count == 0 && images.Count == 3 && texts.Count == 5)
                    {
                        return LoginWindowState.Code;
                    }
                    else if (inputs.Count == 0 && buttons.Count == 0 && groups.Count == 0 && images.Count == 3 && texts.Count == 7)
                    {
                        return LoginWindowState.MobileConfirmation;
                    }
                    else if (inputs.Count == 2 && buttons.Count == 1)
                    {
                        return LoginWindowState.Login;
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.Message);
                }
            }

            return LoginWindowState.Invalid;
        }

        public static LoginWindowState HandleAccountSelection(WindowHandle loginWindow)
        {
            using (var automation = new UIA3Automation())
            {
                try
                {
                    AutomationElement window = automation.FromHandle(loginWindow.RawPtr);

                    window.Focus();

                    AutomationElement document = window.FindFirstDescendant(e => e.ByControlType(ControlType.Document));
                    AutomationElement[] groups = document.FindAllChildren(e => e.ByControlType(ControlType.Group));

                    Button addAccountButton = groups[groups.Length - 1].AsButton();
                    addAccountButton.Invoke();

                    return LoginWindowState.Login;
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.Message);
                }
            }

            return LoginWindowState.Invalid;
        }

        public static LoginWindowState TryMobileToCodeSwitch(WindowHandle loginWindow)
        {
            using (var automation = new UIA3Automation())
            {
                try
                {
                    AutomationElement window = automation.FromHandle(loginWindow.RawPtr);

                    window.Focus();

                    AutomationElement document = window.FindFirstDescendant(e => e.ByControlType(ControlType.Document));
                    AutomationElement[] children = document.FindAllChildren();

                    var texts = new List<AutomationElement>();

                    foreach (AutomationElement element in children)
                    {
                        switch (element.ControlType)
                        {
                            case ControlType.Text:
                                texts.Add(element);
                                break;
                        }
                    }

                // Look for "Enter a code instead" text to click
                foreach (var text in texts)
                {
                    if (text.Name.ToLower().Contains("enter a code instead"))
                    {
                        text.AsButton().Invoke();
                        return LoginWindowState.Code;
                    }
                }

                }
                catch (Exception e)
                {
                    Console.WriteLine("Error switching from mobile confirmation to code entry: " + e.Message);
                }
            }

            return LoginWindowState.Invalid;
        }

        public static LoginWindowState TryCredentialsEntry(WindowHandle loginWindow, string username, string password, bool remember)
        {
            using (var automation = new UIA3Automation())
            {
                try
                {
                    AutomationElement window = automation.FromHandle(loginWindow.RawPtr);

                    window.Focus();

                    AutomationElement document = window.FindFirstDescendant(e => e.ByControlType(ControlType.Document));
                    AutomationElement[] children = document.FindAllChildren();

                    var inputs = new List<AutomationElement>();
                    var buttons = new List<AutomationElement>();
                    var groups = new List<AutomationElement>();

                    foreach (AutomationElement element in children)
                    {
                        switch (element.ControlType)
                        {
                            case ControlType.Edit:
                                inputs.Add(element);
                                break;
                            case ControlType.Button:
                                buttons.Add(element);
                                break;
                            case ControlType.Group:
                                groups.Add(element);
                                break;
                        }
                    }

                    Button signInButton = buttons[0].AsButton();

                    if (signInButton.IsEnabled)
                    {
                        TextBox usernameBox = inputs[0].AsTextBox();
                        usernameBox.WaitUntilEnabled();
                        usernameBox.Text = username;

                        TextBox passwordBox = inputs[1].AsTextBox();
                        passwordBox.WaitUntilEnabled();
                        passwordBox.Text = password;

                        Button checkBoxButton = groups[0].AsButton();
                        bool isChecked = checkBoxButton.FindFirstChild(e => e.ByControlType(ControlType.Image)) != null;

                        if (remember != isChecked)
                        {
                            checkBoxButton.Focus();
                            checkBoxButton.WaitUntilEnabled();
                            checkBoxButton.Invoke();
                        }

                        signInButton.Focus();
                        signInButton.WaitUntilEnabled();
                        signInButton.Invoke();

                        return LoginWindowState.Success;
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.Message);
                }
            }

            return LoginWindowState.Invalid;
        }

        public static LoginWindowState TryCodeEntry(WindowHandle loginWindow, string secret)
        {
            using (var automation = new UIA3Automation())
            {
                try
                {
                    AutomationElement window = automation.FromHandle(loginWindow.RawPtr);

                    window.Focus();

                    AutomationElement document = window.FindFirstDescendant(e => e.ByControlType(ControlType.Document));
                    AutomationElement[] buttons = document.FindAllChildren(e => e.ByControlType(ControlType.Button));

                    string code = Generate2FACode(secret);

                    try
                    {
                        for (int i = 0; i < buttons.Length; i++)
                        {
                            buttons[i].AsButton().Invoke();
                            Keyboard.Type(code[i]);
                            WaitForChildEdit(buttons[i]);
                        }
                    }
                    catch (Exception em)
                    {
                        Console.WriteLine(em.Message);
                        return LoginWindowState.Code;
                    }

                    return LoginWindowState.Success;
                }
                catch (Exception ex) 
                {
                    Console.WriteLine(ex.Message);
                }
            }

            return LoginWindowState.Invalid;
        }

        public static AutomationElement WaitForChildEdit(AutomationElement parent, int timeoutMs = 500, int intervalMs = 10)
        {
            var stopwatch = Stopwatch.StartNew();

            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                var textBox = parent.FindFirstChild(cf => cf.ByControlType(ControlType.Text));
                if (textBox != null && !string.IsNullOrEmpty(textBox.AsTextBox().Name))
                    return textBox;

                Thread.Sleep(intervalMs);
            }

            return null;
        }

        public static Process WaitForSteamProcess(WindowHandle windowHandle)
        {
            Process process = null;

            // Wait for valid process to wait for input idle.
            Console.WriteLine("Waiting for it to be idle.");
            while (process == null)
            {
                GetWindowThreadProcessId(windowHandle.RawPtr, out int procId);

                // Wait for valid process id from handle.
                while (procId == 0)
                {
                    Thread.Sleep(100);
                    GetWindowThreadProcessId(windowHandle.RawPtr, out procId);
                }

                try
                {
                    process = Process.GetProcessById(procId);
                }
                catch
                {
                    process = null;
                }
            }

            return process;
        }

        public static WindowHandle WaitForSteamClientWindow()
        {
            WindowHandle steamClientWindow = WindowHandle.Invalid;

            Console.WriteLine("Waiting for full Steam client to initialize.");

            int waitCounter = 0;

            while (!steamClientWindow.IsValid && !loginAllCancelled)
            {
                if (waitCounter >= 600)
                {
                    MessageBoxResult messageBoxResult = MessageBox.Show(
                    "SAM has been waiting for Steam for longer than 60 seconds." +
                    "Would you like to skip this account and continue?" +
                    "Click No to wait another 60 seconds.",
                    "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                    if (messageBoxResult == MessageBoxResult.Yes)
                    {
                        return steamClientWindow;
                    }
                    else
                    {
                        waitCounter = 0;
                    }
                }

                steamClientWindow = GetMainSteamClientWindow("Steam");
                Thread.Sleep(100);
                waitCounter += 1;
            }

            loginAllCancelled = false;

            return steamClientWindow;
        }

        public static void CancelLoginAll()
        {
            loginAllCancelled = true;
        }

        public static void ClearSteamUserDataFolder(string steamPath, int sleepTime, int maxRetry)
        {
            WindowHandle steamLoginWindow = GetSteamLoginWindow();
            int waitCount = 0;

            while (steamLoginWindow.IsValid && waitCount < maxRetry)
            {
                Thread.Sleep(sleepTime);
                waitCount++;
            }

            string path = steamPath + "\\userdata";

            if (Directory.Exists(path))
            {
                Console.WriteLine("Deleting userdata files...");
                Directory.Delete(path, true);
                Console.WriteLine("userdata files deleted!");
            }
            else
            {
                Console.WriteLine("userdata directory not found.");
            }
        }

        public static string Generate2FACode(string shared_secret)
        {
            SteamGuardAccount authAccount = new SteamGuardAccount { SharedSecret = shared_secret };
            string code = authAccount.GenerateSteamGuardCode();
            return code;
        }

        public static void SetClipboardTextSTA(string text)
        {
            var thread = new Thread(() => Clipboard.SetText(text));
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
        }

        public static IDataObject GetClipboardDataObjectSTA()
        {
            IDataObject data = null;
            var thread = new Thread(() => { data = Clipboard.GetDataObject(); });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            return data;
        }
    }
}
