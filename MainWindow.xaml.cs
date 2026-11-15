
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace FFBoosterUltimate;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer timer = new();
    private readonly DispatcherTimer gpuTimer = new();

    private string? originalPowerPlan;
    private bool boostActive;
    private bool restoring;

    private readonly System.Collections.Generic.Dictionary<int, ProcessPriorityClass>
        oldPriorities = new();

    private ulong lastIdle, lastKernel, lastUser;
    private bool firstCpuSample = true;

    public MainWindow()
    {
        InitializeComponent();

        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += (_, _) => UpdateDashboard();

        gpuTimer.Interval = TimeSpan.FromSeconds(5);
        gpuTimer.Tick += async (_, _) =>
        {
            var result = await System.Threading.Tasks.Task.Run(ReadGpu);
            GpuValue.Text = result.Value;
            GpuDetail.Text = result.Detail;
        };

        UpdateDashboard();
        timer.Start();
        gpuTimer.Start();
    }

    private static Process[] FindPlayers()
    {
        return Process.GetProcessesByName("HD-Player");
    }

    private void UpdateDashboard()
    {
        double cpu = ReadCpu();
        double ram = ReadRam();

        CpuValue.Text = $"{cpu:0}%";
        CpuBar.Value = cpu;

        RamValue.Text = $"{ram:0}%";
        RamBar.Value = ram;

        var players = FindPlayers();

        if (players.Length == 0)
        {
            GameStatus.Text = "BlueStacks não encontrado";
            PlayerValue.Text = "OFFLINE";
            PriorityValue.Text = "Prioridade: --";

            if (boostActive)
                RestoreBoost();

            return;
        }

        GameStatus.Text = "BlueStacks detectado";
        PlayerValue.Text = "ONLINE";

        PriorityValue.Text =
            $"Prioridade: {players[0].PriorityClass}";

        if (boostActive)
        {
            StatusText.Text = "BOOST ATIVO";
            StatusText.Foreground = Brushes.LimeGreen;
        }
    }

    private static double ReadRam()
    {
        var info = new MemoryStatus();
        info.Length = (uint)Marshal.SizeOf<MemoryStatus>();

        if (!GlobalMemoryStatusEx(ref info) ||
            info.TotalPhysical == 0)
            return 0;

        return 100.0 *
            (info.TotalPhysical - info.AvailablePhysical) /
            info.TotalPhysical;
    }

    private double ReadCpu()
    {
        if (!GetSystemTimes(
            out var idle,
            out var kernel,
            out var user))
            return 0;

        ulong i = ToUInt64(idle);
        ulong k = ToUInt64(kernel);
        ulong u = ToUInt64(user);

        if (firstCpuSample)
        {
            lastIdle = i;
            lastKernel = k;
            lastUser = u;
            firstCpuSample = false;
            return 0;
        }

        ulong idleDelta = i - lastIdle;
        ulong totalDelta =
            (k - lastKernel) + (u - lastUser);

        lastIdle = i;
        lastKernel = k;
        lastUser = u;

        return totalDelta == 0
            ? 0
            : Math.Clamp(
                100.0 * (totalDelta - idleDelta) / totalDelta,
                0, 100);
    }

    private static ulong ToUInt64(FileTime value)
    {
        return ((ulong)value.High << 32) | value.Low;
    }

    private static (string Value, string Detail) ReadGpu()
    {
        try
        {
            string[] paths =
            {
                @"C:\Windows\System32\nvidia-smi.exe",
                @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe"
            };

            string? exe = paths.FirstOrDefault(File.Exists);

            if (exe == null)
                return ("N/D", "nvidia-smi não encontrado");

            using var p = new Process();

            p.StartInfo.FileName = exe;
            p.StartInfo.Arguments =
                "--query-gpu=utilization.gpu,memory.used,temperature.gpu --format=csv,noheader,nounits";

            p.StartInfo.UseShellExecute = false;
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardError = true;

            p.Start();

            var readTask = p.StandardOutput.ReadToEndAsync();

            if (!p.WaitForExit(2000))
            {
                p.Kill();
                return ("N/D", "Tempo limite da leitura NVIDIA");
            }

            string output = readTask.GetAwaiter().GetResult();
            string[] parts = output.Trim().Split(',');

            if (parts.Length < 3)
                return ("N/D", "Leitura da GPU indisponível");

            return (
                parts[0].Trim() + "%",
                $"VRAM: {parts[1].Trim()} MB | Temp: {parts[2].Trim()} °C"
            );
        }
        catch
        {
            return ("N/D", "GPU indisponível");
        }
    }

    private static string? GetPowerPlan()
    {
        using var p = new Process();

        p.StartInfo.FileName = "powercfg";
        p.StartInfo.Arguments = "/getactivescheme";
        p.StartInfo.UseShellExecute = false;
        p.StartInfo.CreateNoWindow = true;
        p.StartInfo.RedirectStandardOutput = true;

        p.Start();

        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();

        var match = Regex.Match(
            output,
            @"[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}");

        return match.Success ? match.Value : null;
    }

    private static bool SetPowerPlan(string plan)
    {
        try
        {
            using var p = new Process();

            p.StartInfo.FileName = "powercfg";
            p.StartInfo.Arguments = $"/setactive {plan}";
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.CreateNoWindow = true;

            p.Start();
            p.WaitForExit();

            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        var players = FindPlayers();

        if (players.Length == 0)
        {
            LogText.Text = "Abra o BlueStacks antes de iniciar.";
            return;
        }

        if (!boostActive)
            originalPowerPlan = GetPowerPlan();

        bool powerChanged = SetPowerPlan("SCHEME_MIN");

        if (!powerChanged)
        {
            LogText.Text =
                "Não foi possível alterar o plano de energia.";
            return;
        }

        oldPriorities.Clear();

        foreach (var player in players)
        {
            try
            {
                oldPriorities[player.Id] = player.PriorityClass;
                player.PriorityClass =
                    ProcessPriorityClass.AboveNormal;
            }
            catch
            {
                LogText.Text =
                    "Algumas prioridades não puderam ser alteradas.";
            }
        }

        boostActive = true;

        StatusText.Text = "BOOST ATIVO";
        StatusText.Foreground = Brushes.LimeGreen;
        GameStatus.Text = "BlueStacks otimizado";
        PlayerValue.Text = "ONLINE";

        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;

        LogText.Text =
            "Alto desempenho e prioridade AboveNormal aplicados.";

        UpdateDashboard();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        RestoreBoost();
    }

    private void RestoreBoost()
    {
        if (restoring)
            return;

        restoring = true;

        try
        {
            foreach (var item in oldPriorities)
            {
                try
                {
                    using var process =
                        Process.GetProcessById(item.Key);

                    process.PriorityClass = item.Value;
                }
                catch { }
            }

            if (originalPowerPlan != null)
                SetPowerPlan(originalPowerPlan);

            oldPriorities.Clear();
            originalPowerPlan = null;
            boostActive = false;

            StatusText.Text = "BOOST DESATIVADO";
            StatusText.Foreground = Brushes.Orange;
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;

            LogText.Text =
                "Configurações anteriores restauradas.";
        }
        finally
        {
            restoring = false;
        }
    }

    private void RefreshButton_Click(
        object sender, RoutedEventArgs e)
    {
        UpdateDashboard();
    }

    protected override void OnClosed(EventArgs e)
    {
        timer.Stop();
        gpuTimer.Stop();

        if (boostActive)
            RestoreBoost();

        base.OnClosed(e);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(
        out FileTime idle,
        out FileTime kernel,
        out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(
        ref MemoryStatus buffer);
}
