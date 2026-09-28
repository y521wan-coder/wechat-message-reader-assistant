using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

[assembly: AssemblyTitle("电脑版微信消息朗读助手")]
[assembly: AssemblyDescription("电脑版微信消息朗读助手")]
[assembly: AssemblyCompany("WeChat Message Reader Assistant")]
[assembly: AssemblyProduct("电脑版微信消息朗读助手")]
[assembly: AssemblyCopyright("Copyright 2026")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new InstallerForm());
    }
}

internal sealed class InstallerForm : Form
{
    private const string AppName = "微信消息朗读助手";
    private const string AppExeName = "WeChatMessageReaderAssistant.exe";
    private const string ShortcutName = "微信消息朗读助手.lnk";
    private static readonly string DefaultInstallDir = GetDefaultInstallDir();

    private readonly TextBox installPathTextBox;
    private readonly Button browseButton;
    private readonly CheckBox openExclusionsCheckBox;
    private readonly Button nextButton;
    private readonly Button cancelButton;
    private readonly Label statusLabel;
    private bool installed;

    public InstallerForm()
    {
        Text = AppName + "安装向导";
        AccessibleName = AppName + "安装向导";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(620, 350);
        Font = new Font("Microsoft YaHei UI", 9F);

        Label titleLabel = new Label();
        titleLabel.AutoSize = false;
        titleLabel.Location = new Point(18, 18);
        titleLabel.Size = new Size(580, 32);
        titleLabel.Font = new Font(Font, FontStyle.Bold);
        titleLabel.Text = AppName + "安装向导";

        Label introLabel = new Label();
        introLabel.AutoSize = false;
        introLabel.Location = new Point(18, 58);
        introLabel.Size = new Size(580, 56);
        introLabel.Text = "请选择安装位置。默认安装到当前用户目录，通常不需要管理员权限。可以按 Tab 在控件之间移动，直接输入路径，或按“浏览”选择文件夹。";

        Label pathLabel = new Label();
        pathLabel.AutoSize = true;
        pathLabel.Location = new Point(18, 126);
        pathLabel.Text = "安装位置(&L)：";

        installPathTextBox = new TextBox();
        installPathTextBox.Location = new Point(18, 150);
        installPathTextBox.Size = new Size(475, 26);
        installPathTextBox.TabIndex = 0;
        installPathTextBox.Text = DefaultInstallDir;
        installPathTextBox.AccessibleName = "安装位置";
        installPathTextBox.AccessibleDescription = "请输入微信消息朗读助手的安装文件夹路径。";

        browseButton = new Button();
        browseButton.Location = new Point(506, 148);
        browseButton.Size = new Size(92, 30);
        browseButton.TabIndex = 1;
        browseButton.Text = "浏览(&B)...";
        browseButton.AccessibleName = "浏览安装位置";
        browseButton.Click += BrowseButton_Click;

        Label shortcutLabel = new Label();
        shortcutLabel.AutoSize = false;
        shortcutLabel.Location = new Point(18, 190);
        shortcutLabel.Size = new Size(580, 28);
        shortcutLabel.Text = "安装完成后，会在桌面创建“微信消息朗读助手”快捷方式。";

        openExclusionsCheckBox = new CheckBox();
        openExclusionsCheckBox.AutoSize = false;
        openExclusionsCheckBox.Location = new Point(18, 222);
        openExclusionsCheckBox.Size = new Size(580, 42);
        openExclusionsCheckBox.TabIndex = 2;
        openExclusionsCheckBox.Text = "安装完成后打开 Windows 安全中心的排除项设置页面，我自己手动添加安装文件夹(&E)";
        openExclusionsCheckBox.AccessibleName = "打开 Windows 安全中心排除项设置页面";
        openExclusionsCheckBox.AccessibleDescription = "安装器不会自动修改杀毒软件设置。勾选后只打开系统设置页面，由用户自己决定是否添加安装文件夹。";

        statusLabel = new Label();
        statusLabel.AutoSize = false;
        statusLabel.Location = new Point(18, 274);
        statusLabel.Size = new Size(580, 22);
        statusLabel.AccessibleName = "安装状态";
        statusLabel.Text = "准备安装。";

        nextButton = new Button();
        nextButton.Location = new Point(384, 304);
        nextButton.Size = new Size(102, 30);
        nextButton.TabIndex = 3;
        nextButton.Text = "下一步(&N)";
        nextButton.AccessibleName = "下一步，开始安装";
        nextButton.Click += NextButton_Click;

        cancelButton = new Button();
        cancelButton.Location = new Point(496, 304);
        cancelButton.Size = new Size(102, 30);
        cancelButton.TabIndex = 4;
        cancelButton.Text = "取消(&C)";
        cancelButton.AccessibleName = "取消安装";
        cancelButton.Click += delegate { Close(); };

        AcceptButton = nextButton;
        CancelButton = cancelButton;

        Controls.Add(titleLabel);
        Controls.Add(introLabel);
        Controls.Add(pathLabel);
        Controls.Add(installPathTextBox);
        Controls.Add(browseButton);
        Controls.Add(shortcutLabel);
        Controls.Add(openExclusionsCheckBox);
        Controls.Add(statusLabel);
        Controls.Add(nextButton);
        Controls.Add(cancelButton);

        Shown += delegate { installPathTextBox.Focus(); installPathTextBox.SelectAll(); };
    }

    private void BrowseButton_Click(object sender, EventArgs e)
    {
        using (FolderBrowserDialog dialog = new FolderBrowserDialog())
        {
            dialog.Description = "请选择微信消息朗读助手的安装文件夹";
            dialog.ShowNewFolderButton = true;
            if (Directory.Exists(installPathTextBox.Text))
            {
                dialog.SelectedPath = installPathTextBox.Text;
            }

            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                installPathTextBox.Text = dialog.SelectedPath;
                installPathTextBox.Focus();
                installPathTextBox.SelectAll();
            }
        }
    }

    private void NextButton_Click(object sender, EventArgs e)
    {
        if (installed)
        {
            Close();
            return;
        }

        string installDir;
        try
        {
            installDir = NormalizeInstallPath(installPathTextBox.Text);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, AppName + "安装向导", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            installPathTextBox.Focus();
            installPathTextBox.SelectAll();
            return;
        }

        SetInstallingState(true);
        try
        {
            bool openExclusionsAfterInstall = openExclusionsCheckBox.Checked;
            statusLabel.Text = "正在安装，请稍候。";
            Refresh();
            InstallTo(installDir);

            installed = true;
            installPathTextBox.Text = installDir;
            statusLabel.Text = "安装完成。桌面快捷方式已经创建。";
            nextButton.Text = "完成(&F)";
            nextButton.AccessibleName = "完成安装";
            nextButton.Enabled = true;
            cancelButton.Enabled = false;
            if (openExclusionsAfterInstall)
            {
                OpenWindowsSecuritySettings();
            }

            MessageBox.Show(this,
                BuildSuccessMessage(installDir, openExclusionsAfterInstall),
                AppName + "安装向导",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            nextButton.Focus();
        }
        catch (Exception ex)
        {
            WriteInstallError(ex);
            statusLabel.Text = "安装失败。";
            MessageBox.Show(this,
                "安装失败。错误内容：\r\n" + ex.Message + "\r\n\r\n如果需要反馈，请查看临时目录里的：微信消息朗读助手安装错误报告.txt",
                AppName + "安装向导",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            if (!installed)
            {
                SetInstallingState(false);
            }
        }
    }

    private void SetInstallingState(bool installing)
    {
        installPathTextBox.Enabled = !installing;
        browseButton.Enabled = !installing;
        openExclusionsCheckBox.Enabled = !installing;
        nextButton.Enabled = !installing;
        cancelButton.Enabled = !installing;
    }

    private static string GetDefaultInstallDir()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            return Path.Combine(localAppData, "Programs", "微信消息朗读助手");
        }

        return @"C:\微信消息朗读助手绿色版";
    }

    private static string BuildSuccessMessage(string installDir, bool openedExclusionsSettings)
    {
        string message = "微信消息朗读助手已经安装完成。\r\n\r\n安装位置：" + installDir + "\r\n桌面快捷方式：微信消息朗读助手\r\n\r\n现在可以在桌面找到快捷方式，按回车运行。";
        if (openedExclusionsSettings)
        {
            message += "\r\n\r\n已经尝试打开 Windows 安全中心。安装器没有自动修改杀毒软件设置。如果你确认信任本软件，可以在 Windows 安全中心里选择添加排除项，类型选择文件夹，然后添加上面的安装位置。";
        }

        return message;
    }

    private static string NormalizeInstallPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("安装位置不能为空。");
        }

        string trimmed = path.Trim().Trim('"');
        foreach (char invalid in Path.GetInvalidPathChars())
        {
            if (trimmed.IndexOf(invalid) >= 0)
            {
                throw new InvalidOperationException("安装位置包含不能使用的字符，请重新输入。");
            }
        }

        string fullPath = Path.GetFullPath(trimmed);
        string root = Path.GetPathRoot(fullPath);
        if (string.Equals(fullPath.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("请不要直接安装到磁盘根目录，请选择或输入一个文件夹，例如 D:\\微信消息朗读助手。");
        }

        return fullPath.TrimEnd('\\');
    }

    private static void InstallTo(string installDir)
    {
        StopRunningApp();
        Directory.CreateDirectory(installDir);
        ExtractPayload(installDir);

        string exePath = Path.Combine(installDir, AppExeName);
        if (!File.Exists(exePath))
        {
            throw new FileNotFoundException("安装后没有找到主程序。", exePath);
        }

        CreateShortcut(exePath, installDir);
    }

    private static void StopRunningApp()
    {
        Process[] processes = Process.GetProcessesByName("WeChatMessageReaderAssistant");
        bool stillRunning = false;
        foreach (Process process in processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.CloseMainWindow();
                    if (!process.WaitForExit(5000))
                    {
                        stillRunning = true;
                    }
                }
            }
            catch
            {
                stillRunning = true;
            }
            finally
            {
                process.Dispose();
            }
        }

        if (stillRunning)
        {
            throw new InvalidOperationException("检测到微信消息朗读助手正在运行。请先从通知区域菜单选择“退出程序”，或在主窗口按 Ctrl+Q 真正退出，然后重新运行安装。");
        }
    }

    private static void ExtractPayload(string installDir)
    {
        string basePath = Path.GetFullPath(installDir);
        if (!basePath.EndsWith("\\", StringComparison.Ordinal))
        {
            basePath += "\\";
        }

        using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
        {
            if (resource == null)
            {
                throw new InvalidOperationException("安装包内部数据不存在。请重新生成安装 EXE。");
            }

            using (ZipArchive archive = new ZipArchive(resource, ZipArchiveMode.Read))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string targetPath = Path.GetFullPath(Path.Combine(basePath, entry.FullName));
                    if (!targetPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("安装包内部路径不安全，已停止安装。");
                    }

                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith("\\", StringComparison.Ordinal))
                    {
                        Directory.CreateDirectory(targetPath);
                        continue;
                    }

                    string directory = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    if (File.Exists(targetPath))
                    {
                        File.SetAttributes(targetPath, FileAttributes.Normal);
                    }

                    using (Stream source = entry.Open())
                    using (FileStream destination = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        source.CopyTo(destination);
                    }
                }
            }
        }
    }

    private static void CreateShortcut(string targetPath, string workingDirectory)
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktop) || !Directory.Exists(desktop))
        {
            desktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        }

        string shortcutPath = Path.Combine(desktop, ShortcutName);
        Type shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null)
        {
            throw new InvalidOperationException("没有找到 WScript.Shell，无法创建桌面快捷方式。");
        }

        object shellObject = Activator.CreateInstance(shellType);
        object shortcutObject = null;
        try
        {
            dynamic shell = shellObject;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcutObject = shortcut;
            shortcut.TargetPath = targetPath;
            shortcut.WorkingDirectory = workingDirectory;
            shortcut.Description = AppName;
            shortcut.IconLocation = targetPath + ",0";
            shortcut.Save();
        }
        finally
        {
            if (shortcutObject != null)
            {
                try { Marshal.FinalReleaseComObject(shortcutObject); } catch { }
            }
            if (shellObject != null)
            {
                try { Marshal.FinalReleaseComObject(shellObject); } catch { }
            }
        }
    }

    private static void OpenWindowsSecuritySettings()
    {
        string[] candidates = new[]
        {
            "windowsdefender://exclusions/",
            "ms-settings:windowsdefender"
        };

        foreach (string candidate in candidates)
        {
            try
            {
                Process.Start(new ProcessStartInfo(candidate) { UseShellExecute = true });
                return;
            }
            catch
            {
            }
        }
    }

    private static void WriteInstallError(Exception ex)
    {
        try
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "微信消息朗读助手安装错误报告.txt"), ex.ToString());
        }
        catch
        {
        }
    }
}
