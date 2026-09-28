using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Threading;
using WeChatMessageReaderAssistant.App.Services;

namespace WeChatMessageReaderAssistant.App;

public partial class MainWindow : Window
{
	private readonly SpeechService _speechService;

	private readonly NotificationListenerService _notificationService;

	private readonly WeChatWindowAutomationService _weChatWindowService;

	private readonly GlobalKeyboardStopService _globalKeyboardStopService;

	private readonly AppSettingsService _settingsService;

	private readonly WeChatReadHistoryService _weChatReadHistoryService;

	private readonly MsaaWeChatScanService _msaaScanService;

	private readonly DispatcherTimer _notificationTimer;

	private readonly DispatcherTimer _weChatChatMonitorTimer;

	private readonly DispatcherTimer _maintenanceTimer;

	private NotifyIcon? _notifyIcon;

	private ContextMenuStrip? _trayMenu;

	private readonly HashSet<string> _seenWeChatChatMessages = new HashSet<string>();

	private bool _isListening;

	private bool _isWeChatChatMonitoring;

	private bool _isWeChatChatPolling;

	private bool _needSeedVisibleMessagesOnNextSuccessfulScan;

	private bool _isReallyExiting;

	private bool _hasShownTrayTip;

	private DateTime _nextAllowedWeChatScanAt = DateTime.MinValue;

	private DateTime _lastWeChatWaitStatusAt = DateTime.MinValue;

	private DateTime _lastWeChatHistoryBrowsingStatusAt = DateTime.MinValue;

	private DateTime _suppressWeChatReadingUntil = DateTime.MinValue;

	private int _consecutiveWeChatScanFailures;

	private DateTime _lastMaintenanceAt = DateTime.MinValue;

	private const int DefaultWeChatMonitorIntervalMilliseconds = 500;

	private const int MaxSeenMessageKeys = 500;

	private const int HistoryBatchNewMessageThreshold = 4;

	private const int MaxNotificationLogLines = 200;

	private const int MaxNotificationLogCharacters = 30000;

	private const int MaxTroubleshootingSectionCharacters = 30000;

	private const int MaxTroubleshootingReportCharacters = 180000;

	private const string TroubleshootingReportFileName = "排查信息.txt";

	public RelayCommand SpeakCommand { get; }

	public RelayCommand VoiceMessageCommand { get; }

	public RelayCommand StopCommand { get; }

	public RelayCommand RequestNotificationAccessCommand { get; }

	public RelayCommand ToggleNotificationListeningCommand { get; }

	public RelayCommand TestNotificationParseCommand { get; }

	public RelayCommand DetectWeChatWindowCommand { get; }

	public RelayCommand DetectMsaaWeChatWindowCommand { get; }

	public RelayCommand ToggleWeChatChatMonitorCommand { get; }

	public RelayCommand EngineDiagnosticCommand { get; }

	public RelayCommand CopyTroubleshootingReportCommand { get; }

	public RelayCommand SaveSettingsCommand { get; }

	public RelayCommand ExitCommand { get; }

	public MainWindow()
	{
		SpeakCommand = new RelayCommand(SpeakCurrentText);
		VoiceMessageCommand = new RelayCommand(SpeakVoiceMessageHint);
		StopCommand = new RelayCommand(StopSpeaking);
		RequestNotificationAccessCommand = new RelayCommand(RequestNotificationAccess);
		ToggleNotificationListeningCommand = new RelayCommand(ToggleNotificationListening);
		TestNotificationParseCommand = new RelayCommand(TestNotificationParse);
		DetectWeChatWindowCommand = new RelayCommand(DetectWeChatWindow);
		DetectMsaaWeChatWindowCommand = new RelayCommand(DetectMsaaWeChatWindow);
		ToggleWeChatChatMonitorCommand = new RelayCommand(ToggleWeChatChatMonitor);
		EngineDiagnosticCommand = new RelayCommand(ShowEngineDiagnostic);
		CopyTroubleshootingReportCommand = new RelayCommand(CopyTroubleshootingReportToClipboard);
		SaveSettingsCommand = new RelayCommand(SaveSettingsFromUi);
		ExitCommand = new RelayCommand(RequestExit);
		base.DataContext = this;
		InitializeComponent();
		_speechService = new SpeechService();
		_notificationService = new NotificationListenerService();
		_weChatWindowService = new WeChatWindowAutomationService();
		_globalKeyboardStopService = new GlobalKeyboardStopService();
		_settingsService = new AppSettingsService();
		_weChatReadHistoryService = new WeChatReadHistoryService();
		_msaaScanService = new MsaaWeChatScanService();
		_globalKeyboardStopService.ControlKeyPressed += delegate
		{
			base.Dispatcher.BeginInvoke(new Action(StopSpeakingByControlKey));
		};
		_globalKeyboardStopService.NavigationKeyPressed += delegate
		{
			base.Dispatcher.BeginInvoke(new Action(SuppressWeChatReadingAfterNavigationKey));
		};
		_globalKeyboardStopService.GlobalMonitorHotkeyPressed += delegate
		{
			base.Dispatcher.BeginInvoke(new Action(ToggleWeChatMonitorByGlobalHotkey));
		};
		_globalKeyboardStopService.Start();
		_notificationService.NotificationReceived += NotificationService_NotificationReceived;
		_notificationTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(2.0)
		};
		_notificationTimer.Tick += async delegate
		{
			await PollNotificationsAsync();
		};
		_weChatChatMonitorTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(500.0)
		};
		_weChatChatMonitorTimer.Tick += async delegate
		{
			await PollCurrentWeChatChatAsync();
		};
		_maintenanceTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMinutes(30.0)
		};
		_maintenanceTimer.Tick += delegate
		{
			RunMaintenanceCleanup();
		};
		_maintenanceTimer.Start();
		InitializeTrayIcon();
		base.Closing += MainWindow_Closing;
		base.StateChanged += delegate
		{
			if (base.WindowState == WindowState.Minimized && !_isReallyExiting)
			{
				HideToNotificationArea(showTip: false);
			}
		};
		base.Closed += delegate
		{
			SaveSettingsFromUi(showStatus: false);
			_weChatChatMonitorTimer.Stop();
			_maintenanceTimer.Stop();
			_globalKeyboardStopService.Dispose();
			_speechService.Dispose();
			if (_notifyIcon != null)
			{
				_notifyIcon.Visible = false;
				_notifyIcon.Dispose();
			}
			_trayMenu?.Dispose();
		};
		RateSlider.ValueChanged += delegate
		{
			int value = (int)RateSlider.Value;
			RateValueText.Text = $"当前 {value}";
			AutomationProperties.SetName(RateValueText, $"当前语速值 {value}");
		};
		VolumeSlider.ValueChanged += delegate
		{
			int value = (int)VolumeSlider.Value;
			VolumeValueText.Text = $"当前 {value}";
			AutomationProperties.SetName(VolumeValueText, $"当前音量值 {value}");
		};
		MonitorIntervalSlider.ValueChanged += delegate
		{
			UpdateMonitorIntervalText();
			if (_weChatChatMonitorTimer != null)
			{
				ApplyMonitorIntervalFromUi();
			}
		};
		LoadSettingsToUi();
		RunMaintenanceCleanup();
		base.Loaded += delegate
		{
			SpeechBackendComboBox.Focus();
			StartWeChatChatMonitor(autoStart: true);
			base.Dispatcher.BeginInvoke((Action)delegate
			{
				HideToNotificationArea(showTip: true);
			}, DispatcherPriority.ApplicationIdle);
		};
	}

	private void SpeakButton_Click(object sender, RoutedEventArgs e)
	{
		SpeakCurrentText();
	}

	private void VoiceMessageButton_Click(object sender, RoutedEventArgs e)
	{
		SpeakVoiceMessageHint();
	}

	private void StopButton_Click(object sender, RoutedEventArgs e)
	{
		StopSpeaking();
	}

	private void RequestAccessButton_Click(object sender, RoutedEventArgs e)
	{
		RequestNotificationAccess();
	}

	private void ToggleListeningButton_Click(object sender, RoutedEventArgs e)
	{
		ToggleNotificationListening();
	}

	private void TestParseButton_Click(object sender, RoutedEventArgs e)
	{
		TestNotificationParse();
	}

	private void DetectWeChatWindowButton_Click(object sender, RoutedEventArgs e)
	{
		DetectWeChatWindow();
	}

	private void DetectMsaaWeChatWindowButton_Click(object sender, RoutedEventArgs e)
	{
		DetectMsaaWeChatWindow();
	}

	private void ToggleWeChatMonitorButton_Click(object sender, RoutedEventArgs e)
	{
		ToggleWeChatChatMonitor();
	}

	private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
	{
		SaveSettingsFromUi();
	}

	private void SpeechBackendComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		ApplySpeechBackendFromUi(base.IsLoaded);
	}

	private void ExitButton_Click(object sender, RoutedEventArgs e)
	{
		RequestExit();
	}

	private void InitializeTrayIcon()
	{
		_trayMenu = new ContextMenuStrip();
		_trayMenu.Items.Add(CreateTrayMenuItem("打开主窗口和设置(&O)", "打开微信消息朗读助手主窗口，可以调整语速、音量和微信检测间隔。", delegate
		{
			ShowMainWindowFromTray(focusSpeechBackend: false);
		}));
		_trayMenu.Items.Add(CreateTrayMenuItem("复制排查信息给开发者(&C)", "自动汇总当前运行状态、配置、朗读引擎、微信窗口检测、通知记录、错误报告和运行日志，并复制到剪贴板。复制后可直接粘贴到聊天窗口发给开发者。", CopyTroubleshootingReportToClipboard));
		_trayMenu.Items.Add(new ToolStripSeparator());
		_trayMenu.Items.Add(CreateTrayMenuItem("停止朗读(&S)", "停止当前正在朗读的内容。", StopSpeaking));
		_trayMenu.Items.Add(CreateTrayMenuItem("开始或停止微信监控(&M)", "开始或停止监控当前微信聊天文字消息。", ToggleWeChatChatMonitor));
		_trayMenu.Items.Add(new ToolStripSeparator());
		_trayMenu.Items.Add(CreateTrayMenuItem("退出程序(&Q)", "真正退出微信消息朗读助手。", RequestExit));
		_notifyIcon = new NotifyIcon
		{
			Text = "微信消息朗读助手，正在后台监听微信文字消息",
			Icon = SystemIcons.Application,
			Visible = true,
			ContextMenuStrip = _trayMenu
		};
		_notifyIcon.DoubleClick += delegate
		{
			base.Dispatcher.BeginInvoke((Action)delegate
			{
				ShowMainWindowFromTray(focusSpeechBackend: false);
			});
		};
	}

	private ToolStripMenuItem CreateTrayMenuItem(string text, string accessibleDescription, Action action)
	{
		ToolStripMenuItem toolStripMenuItem = new ToolStripMenuItem(text);
		toolStripMenuItem.AccessibleName = text.Replace("&", string.Empty);
		toolStripMenuItem.AccessibleDescription = accessibleDescription;
		toolStripMenuItem.Click += delegate
		{
			base.Dispatcher.BeginInvoke(action);
		};
		return toolStripMenuItem;
	}

	private void HideToNotificationArea(bool showTip)
	{
		if (_notifyIcon != null && !_isReallyExiting)
		{
			base.ShowInTaskbar = false;
			Hide();
			if (showTip && !_hasShownTrayTip)
			{
				_hasShownTrayTip = true;
				_notifyIcon.BalloonTipTitle = "微信消息朗读助手已在后台运行";
				_notifyIcon.BalloonTipText = "软件已自动监听微信文字消息。请在通知区域找到微信消息朗读助手，按菜单键或右键可打开主窗口、设置或复制排查信息。";
				_notifyIcon.ShowBalloonTip(4000);
			}
		}
	}

	private void ShowMainWindowFromTray(bool focusSpeechBackend)
	{
		Show();
		base.ShowInTaskbar = true;
		if (base.WindowState == WindowState.Minimized)
		{
			base.WindowState = WindowState.Normal;
		}
		Activate();
		SpeechBackendComboBox.Focus();
		SetStatus("\u4e3b\u7a97\u53e3\u5df2\u6253\u5f00\u3002\u65e5\u5e38\u754c\u9762\u53ea\u663e\u793a\u6717\u8bfb\u65b9\u5f0f\u3001\u8bed\u901f\u3001\u97f3\u91cf\u3001\u4fdd\u5b58\u8bbe\u7f6e\u548c\u9000\u51fa\u7a0b\u5e8f\u3002\u6d4b\u8bd5\u6717\u8bfb\u3001\u5fae\u4fe1\u68c0\u6d4b\u3001\u8bca\u65ad\u548c\u901a\u77e5\u8bb0\u5f55\u5df2\u653e\u5165\u6545\u969c\u8bca\u65ad\u548c\u6d4b\u8bd5\u5de5\u5177\uff0c\u5e73\u65f6\u9ed8\u8ba4\u6536\u8d77\u3002\u5173\u95ed\u7a97\u53e3\u4f1a\u9690\u85cf\u5230\u901a\u77e5\u533a\u57df\uff1b\u5982\u9700\u771f\u6b63\u9000\u51fa\uff0c\u8bf7\u6309 Ctrl \u52a0 Q\uff0c\u6216\u5728\u901a\u77e5\u533a\u57df\u83dc\u5355\u9009\u62e9\u9000\u51fa\u7a0b\u5e8f\u3002");
	}

	private void MainWindow_Closing(object? sender, CancelEventArgs e)
	{
		if (!_isReallyExiting)
		{
			e.Cancel = true;
			HideToNotificationArea(showTip: true);
		}
	}

	private void RequestExit()
	{
		_isReallyExiting = true;
		Close();
	}

	private void AdjustSpeechRate(int delta)
	{
		int num = Math.Clamp((int)RateSlider.Value + delta, (int)RateSlider.Minimum, (int)RateSlider.Maximum);
		RateSlider.Value = num;
		SaveSettingsFromUi(showStatus: false);
		string text = $"语速已调整为 {num}，设置已保存。";
		SetStatus(text);
		AppendNotificationLog(text);
	}

	private void AdjustVolume(int delta)
	{
		int num = Math.Clamp((int)VolumeSlider.Value + delta, (int)VolumeSlider.Minimum, (int)VolumeSlider.Maximum);
		VolumeSlider.Value = num;
		SaveSettingsFromUi(showStatus: false);
		string text = $"音量已调整为 {num}，设置已保存。";
		SetStatus(text);
		AppendNotificationLog(text);
	}

	private async void SpeakCurrentText()
	{
		try
		{
			string text = MessageTextBox.Text.Trim();
			if (string.IsNullOrWhiteSpace(text))
			{
				SetStatus("请输入要朗读的内容。焦点已回到朗读内容编辑框。");
				MessageTextBox.Focus();
				return;
			}
			SetStatus("正在朗读测试文字。可以按 Alt 加 S 停止朗读。当前实际朗读通道：" + _speechService.ActualChannelDescription);
			await _speechService.SpeakAsync(text, (int)RateSlider.Value, (int)VolumeSlider.Value);
			SetStatus("已提交朗读任务。当前实际朗读通道：" + _speechService.ActualChannelDescription + "。当前引擎名称：" + _speechService.EngineName + "。如果显示 Windows SAPI 回退，说明读屏接口当前没有连接成功，已用系统语音库保底朗读。可以按 Alt 加 S 停止。");
		}
		catch (Exception ex)
		{
			SetStatus("朗读失败：" + ex.Message);
		}
	}

	private async void SpeakVoiceMessageHint()
	{
		try
		{
			SetStatus("正在测试语音消息提示。可以按 Alt 加 S 停止朗读。当前实际朗读通道：" + _speechService.ActualChannelDescription);
			await _speechService.SpeakAsync("李四给您发来一条语音消息。", (int)RateSlider.Value, (int)VolumeSlider.Value);
			SetStatus("已提交语音消息提示朗读任务。朗读内容是：李四给您发来一条语音消息。当前实际朗读通道：" + _speechService.ActualChannelDescription + "。当前引擎名称：" + _speechService.EngineName);
		}
		catch (Exception ex)
		{
			SetStatus("语音提示失败：" + ex.Message);
		}
	}

	private void ShowEngineDiagnostic()
	{
		try
		{
			string diagnosticText = _speechService.GetDiagnosticText(_settingsService.SettingsPath);
			AppendNotificationLog(diagnosticText);
			SetStatus("朗读引擎诊断已写入通知记录。请在通知记录框按 Ctrl 加 A 全选，再按 Ctrl 加 C 复制。当前实际朗读通道：" + _speechService.ActualChannelDescription);
		}
		catch (Exception ex)
		{
			string text = "朗读引擎诊断失败：" + ex.Message;
			AppendNotificationLog(text);
			SetStatus(text);
		}
	}

	private async void CopyTroubleshootingReportToClipboard()
	{
		try
		{
			SetStatus("正在汇总排查信息，请稍等。完成后会自动复制到剪贴板，并在程序目录保存一份排查信息文件。");
			TroubleshootingSnapshot snapshot = CreateTroubleshootingSnapshot();
			string report = await Task.Run(() => BuildTroubleshootingReport(snapshot));
			string reportPath = SaveTroubleshootingReportFile(report);
			bool copied = await SetClipboardTextWithRetryAsync(report);
			string message = copied
				? "排查信息已复制到剪贴板。请到聊天窗口按 Ctrl 加 V 粘贴发给开发者。已同时保存到文件：" + reportPath
				: "剪贴板暂时被其它程序占用，排查信息已保存到文件：" + reportPath + "。请稍等几秒后再点一次复制排查信息，或把这个文件内容发给开发者。";
			AppendNotificationLog(message);
			SetStatus(message);
		}
		catch (Exception ex)
		{
			string message = "复制排查信息失败：" + ex.Message;
			AppendNotificationLog(message);
			SetStatus(message);
		}
	}

	private void StopSpeaking()
	{
		_speechService.Stop();
		AppendNotificationLog("\u5df2\u505c\u6b62\u6717\u8bfb\u3002");
	}

	private async void ToggleWeChatMonitorByGlobalHotkey()
	{
		try
		{
			if (_isWeChatChatMonitoring)
			{
				_weChatChatMonitorTimer.Stop();
				_isWeChatChatMonitoring = false;
				_needSeedVisibleMessagesOnNextSuccessfulScan = true;
				ToggleWeChatMonitorButton.Content = "监控当前聊天(_M)";
				_speechService.Stop();
				AppendNotificationLog("已通过全局快捷键暂停微信聊天监控。");
				SetStatus("已暂停微信聊天监控。再次按全局快捷键可恢复。");
				await _speechService.SpeakAsync("暂停", (int)RateSlider.Value, (int)VolumeSlider.Value);
			}
			else
			{
				StartWeChatChatMonitor(autoStart: false);
				AppendNotificationLog("已通过全局快捷键恢复微信聊天监控。");
				await _speechService.SpeakAsync("恢复", (int)RateSlider.Value, (int)VolumeSlider.Value);
			}
		}
		catch (Exception ex)
		{
			AppendNotificationLog("全局快捷键切换微信监控失败：" + ex.Message);
			SetStatus("全局快捷键切换微信监控失败：" + ex.Message);
		}
	}

	private void SuppressWeChatReadingAfterNavigationKey()
	{
		WeChatFocusedElementState focusState = _weChatWindowService.GetFocusedElementState();
		if (!focusState.IsWeChat || focusState.IsInputField)
		{
			return;
		}

		_suppressWeChatReadingUntil = DateTime.Now.AddSeconds(3.0);
	}

	private void StopSpeakingByControlKey()
	{
		_speechService.Stop();
			AppendNotificationLog("\u5df2\u6309 Control \u952e\u505c\u6b62\u5f53\u524d\u6717\u8bfb\u3002");
	}

	private async void RequestNotificationAccess()
	{
		try
		{
			SetStatus("正在申请通知监听权限。请留意 Windows 是否弹出权限提示。");
			string text = await _notificationService.RequestAccessAsync();
			SetStatus(text);
			AppendNotificationLog("权限状态：" + text);
		}
		catch (Exception ex)
		{
			SetStatus("申请通知权限失败：" + ex.Message);
			AppendNotificationLog("申请通知权限失败：" + ex.Message);
		}
	}

	private void ToggleNotificationListening()
	{
		if (_isListening)
		{
			_notificationTimer.Stop();
			_isListening = false;
			ToggleListeningButton.Content = "开始监听(_L)";
			SetStatus("已停止监听 Windows 通知。按 Alt 加 L 可再次开始监听。");
		}
		else
		{
			StartNotificationListening();
		}
	}

	private async void StartNotificationListening()
	{
		try
		{
			_notificationService.ResetSeenNotifications();
			int value = await _notificationService.MarkCurrentNotificationsAsSeenAsync();
			_notificationTimer.Start();
			_isListening = true;
			ToggleListeningButton.Content = "停止监听(_L)";
			SetStatus($"已开始监听 Windows 通知。已忽略当前通知中心里已有的 {value} 条旧通知。现在请让微信收到一条新消息测试。");
			AppendNotificationLog($"开始监听：已忽略 {value} 条旧通知。后续只处理新通知。");
		}
		catch (Exception ex)
		{
			SetStatus("开始监听失败：" + ex.Message);
			AppendNotificationLog("开始监听失败：" + ex.Message);
		}
	}

	private async Task PollNotificationsAsync()
	{
		try
		{
			bool includeNonWeChat = OnlyWeChatCheckBox.IsChecked != true;
			IReadOnlyList<NotificationMessage> readOnlyList = await _notificationService.ReadCurrentNotificationsAsync(includeNonWeChat);
			if (readOnlyList.Count > 0)
			{
				SetStatus($"本次检查到 {readOnlyList.Count} 条符合条件的新通知。最新通知已写入通知记录。");
			}
		}
		catch (Exception ex)
		{
			_notificationTimer.Stop();
			_isListening = false;
			ToggleListeningButton.Content = "开始监听(_L)";
			SetStatus("监听通知失败，已自动停止监听。错误：" + ex.Message);
			AppendNotificationLog("监听通知失败：" + ex.Message);
		}
	}

	private async void NotificationService_NotificationReceived(object? sender, NotificationMessage message)
	{
		string message2 = FormatNotificationLog(message);
		AppendNotificationLog(message2);
		if (message.IsProbablyWeChat && AutoReadWeChatCheckBox.IsChecked == true)
		{
			await _speechService.SpeakAsync(message.SpeakText, (int)RateSlider.Value, (int)VolumeSlider.Value);
			SetStatus("已捕获疑似微信通知并提交朗读。" + message.SpeakText);
		}
		else if (message.IsProbablyWeChat)
		{
			SetStatus("已捕获疑似微信通知，但自动朗读已关闭。" + message.SpeakText);
		}
	}

	private async void TestNotificationParse()
	{
		NotificationMessage notificationMessage = _notificationService.ParseForTest("微信", "张三", "您好，这是一条模拟的客户文字消息。");
		AppendNotificationLog("模拟通知：" + FormatNotificationLog(notificationMessage));
		SetStatus("已生成模拟微信通知并提交朗读。" + notificationMessage.SpeakText);
		await _speechService.SpeakAsync(notificationMessage.SpeakText, (int)RateSlider.Value, (int)VolumeSlider.Value);
	}

	private void ToggleWeChatChatMonitor()
	{
		if (_isWeChatChatMonitoring)
		{
			_weChatChatMonitorTimer.Stop();
			_isWeChatChatMonitoring = false;
			ToggleWeChatMonitorButton.Content = "监控当前聊天(_M)";
			SetStatus("已停止监控当前微信聊天。按 Alt 加 M 可再次开始。");
			AppendNotificationLog("已停止监控当前微信聊天。按 Alt+M 可再次开始。");
		}
		else
		{
			StartWeChatChatMonitor(autoStart: false);
		}
	}

	private void StartWeChatChatMonitor(bool autoStart)
	{
		if (!_isWeChatChatMonitoring)
		{
			_seenWeChatChatMessages.Clear();
			_needSeedVisibleMessagesOnNextSuccessfulScan = true;
			_consecutiveWeChatScanFailures = 0;
			_nextAllowedWeChatScanAt = DateTime.Now.AddSeconds(2.0);
			ApplyMonitorIntervalFromUi();
			_weChatChatMonitorTimer.Start();
			_isWeChatChatMonitoring = true;
			ToggleWeChatMonitorButton.Content = "停止监控(_M)";
			string text = (autoStart ? "已自动开始监控微信当前聊天" : "已开始监控微信当前聊天") + "。如果微信没有打开，或者微信正在重新启动，助手会先等待微信稳定，不会强行扫描微信窗口。第一次读取到的当前可见旧消息会被临时忽略，后续只朗读新文字消息。按 Alt 加 M 可停止监控。";
			SetStatus(text);
			AppendNotificationLog(text);
		}
	}

	private async Task PollCurrentWeChatChatAsync()
	{
		if (_isWeChatChatPolling)
		{
			return;
		}
		DateTime now = DateTime.Now;
		if (now < _nextAllowedWeChatScanAt)
		{
			return;
		}
		(bool, string, int) weChatScanReadiness = GetWeChatScanReadiness(now);
		if (!weChatScanReadiness.Item1)
		{
			_needSeedVisibleMessagesOnNextSuccessfulScan = true;
			_nextAllowedWeChatScanAt = now.AddSeconds(weChatScanReadiness.Item3);
			SetWaitingStatusThrottled(weChatScanReadiness.Item2);
			return;
		}
		_isWeChatChatPolling = true;
		try
		{
			(IReadOnlyList<WeChatChatMessage>, WeChatChatViewportState) tuple = await Task.Run(delegate
			{
				IReadOnlyList<WeChatChatMessage> item2 = _weChatWindowService.ReadCurrentChatMessages(50);
				WeChatChatViewportState currentChatViewportState = _weChatWindowService.GetCurrentChatViewportState();
				return (Messages: item2, ViewportState: currentChatViewportState);
			});
			IReadOnlyList<WeChatChatMessage> messages = tuple.Item1;
			WeChatChatViewportState item = tuple.Item2;
			_consecutiveWeChatScanFailures = 0;
			if (_needSeedVisibleMessagesOnNextSuccessfulScan)
			{
				_seenWeChatChatMessages.Clear();
				MarkVisibleWeChatMessagesAsSeen(messages);
				_needSeedVisibleMessagesOnNextSuccessfulScan = false;
				int value = messages.Count((WeChatChatMessage m) => !_weChatReadHistoryService.HasRead(m));
				string text = $"微信窗口已就绪。当前可见文字消息 {messages.Count} 条已临时视为旧消息，其中未写入永久已读历史 {value} 条。后续只朗读新出现的文字消息。";
				SetStatus(text);
				AppendNotificationLog(text);
				return;
			}
			if (item.Found && !item.IsNearBottom)
			{
				MarkVisibleWeChatMessagesAsSeen(messages);
				SetHistoryBrowsingStatusThrottled(item.Summary + " 已把当前可见消息临时视为旧消息，避免和屏幕阅读器朗读历史消息冲突。");
				return;
			}
			if (DateTime.Now < _suppressWeChatReadingUntil)
			{
				MarkVisibleWeChatMessagesAsSeen(messages);
				SetHistoryBrowsingStatusThrottled("检测到您刚才按了上光标、下光标、PageUp、PageDown、Home 或 End，判断您可能正在用屏幕阅读器查看聊天记录。本次可见消息不朗读，避免和读屏冲突。");
				return;
			}
			List<WeChatChatMessage> list = messages.Where((WeChatChatMessage m) => !string.IsNullOrWhiteSpace(m.StableKey) && !_seenWeChatChatMessages.Contains(m.StableKey) && !_weChatReadHistoryService.HasRead(m)).ToList();
			if (list.Count >= 4)
			{
				MarkVisibleWeChatMessagesAsSeen(messages);
				SetHistoryBrowsingStatusThrottled($"本次一次性出现 {list.Count} 条未见过的可见消息，判断可能是微信加载历史记录。已临时视为旧消息，不朗读，避免和屏幕阅读器冲突。");
				return;
			}
			foreach (WeChatChatMessage message in list)
			{
				string speakText = BuildWeChatSpeakText(message);
				if (string.IsNullOrWhiteSpace(speakText))
				{
					MarkWeChatMessageAsSeen(message);
					continue;
				}
				AppendNotificationLog($"微信聊天新文字消息：聊天：{message.ChatName}；内容：{message.Text}；朗读：{speakText}；标识：{message.StableKey}");
				try
				{
					ApplySpeechBackendFromUi(showStatus: false);
					await _speechService.SpeakAsync(speakText, (int)RateSlider.Value, (int)VolumeSlider.Value);
					MarkWeChatMessageAsSeen(message);
					_weChatReadHistoryService.MarkRead(new WeChatChatMessage[1] { message });
					AppendNotificationLog("\u5df2\u68c0\u6d4b\u5e76\u6717\u8bfb\u5fae\u4fe1\u804a\u5929\u65b0\u6587\u5b57\u6d88\u606f\u3002\u6717\u8bfb\uff1a" + speakText + "\uff1b\u5f53\u524d\u5b9e\u9645\u6717\u8bfb\u901a\u9053\uff1a" + _speechService.ActualChannelDescription);
				}
				catch (Exception ex)
				{
					AppendNotificationLog("朗读微信消息失败，但监控继续：" + ex.Message);
					SetStatus("检测到微信新消息，但朗读失败，监控继续。错误：" + ex.Message);
				}
			}
			TrimSeenWeChatMessageKeys(messages);
		}
		catch (Exception ex2)
		{
			_consecutiveWeChatScanFailures++;
			int num = Math.Min(30, 3 + _consecutiveWeChatScanFailures * 3);
			_nextAllowedWeChatScanAt = DateTime.Now.AddSeconds(num);
			_needSeedVisibleMessagesOnNextSuccessfulScan = true;
			string text2 = $"本次读取微信聊天失败，监控会在 {num} 秒后继续重试。为避免微信关闭或重开时卡死，暂时放慢扫描。错误：{ex2.Message}";
			SetStatus(text2);
			AppendNotificationLog(text2);
		}
		finally
		{
			_isWeChatChatPolling = false;
		}
	}

	private (bool CanScan, string Message, int RetryAfterSeconds) GetWeChatScanReadiness(DateTime now)
	{
		List<Process> list = (from p in Process.GetProcesses()
			where IsWeChatProcessName(p.ProcessName)
			select p).ToList();
		if (list.Count == 0)
		{
			return (CanScan: false, Message: "微信当前没有打开。朗读助手已自动监控，会等待您打开微信；微信打开并稳定后再读取，不会强行扫描。", RetryAfterSeconds: 5);
		}
		DateTime? dateTime = null;
		foreach (Process item in list)
		{
			try
			{
				DateTime startTime = item.StartTime;
				if (!dateTime.HasValue || startTime > dateTime.Value)
				{
					dateTime = startTime;
				}
			}
			catch
			{
			}
			finally
			{
				item.Dispose();
			}
		}
		if (dateTime.HasValue)
		{
			double totalSeconds = (now - dateTime.Value).TotalSeconds;
			if (totalSeconds >= 0.0 && totalSeconds < 20.0)
			{
				int num = Math.Max(3, (int)Math.Ceiling(20.0 - totalSeconds));
				return (CanScan: false, Message: $"检测到微信刚刚启动，先等待约 {num} 秒，避免在微信启动阶段扫描导致未响应。", RetryAfterSeconds: num);
			}
		}
		return (CanScan: true, Message: string.Empty, RetryAfterSeconds: 0);
	}

	private static bool IsWeChatProcessName(string processName)
	{
		if (!processName.Contains("WeChat", StringComparison.OrdinalIgnoreCase) && !processName.Contains("Weixin", StringComparison.OrdinalIgnoreCase))
		{
			return processName.Contains("WX", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private void SetWaitingStatusThrottled(string message)
	{
		DateTime now = DateTime.Now;
		if (!((now - _lastWeChatWaitStatusAt).TotalSeconds < 8.0))
		{
			_lastWeChatWaitStatusAt = now;
			SetStatus(message);
			AppendNotificationLog(message);
		}
	}

	private static string BuildWeChatSpeakText(WeChatChatMessage message)
	{
		return message.Text?.Trim() ?? string.Empty;
	}

	private void MarkVisibleWeChatMessagesAsSeen(IReadOnlyList<WeChatChatMessage> messages)
	{
		foreach (WeChatChatMessage message in messages)
		{
			MarkWeChatMessageAsSeen(message);
		}
		TrimSeenWeChatMessageKeys(messages);
	}

	private void MarkWeChatMessageAsSeen(WeChatChatMessage message)
	{
		if (!string.IsNullOrWhiteSpace(message.StableKey))
		{
			_seenWeChatChatMessages.Add(message.StableKey);
		}
	}

	private void SetHistoryBrowsingStatusThrottled(string message)
	{
		DateTime now = DateTime.Now;
		if (!((now - _lastWeChatHistoryBrowsingStatusAt).TotalSeconds < 8.0))
		{
			_lastWeChatHistoryBrowsingStatusAt = now;
			// Keep history-browsing protection quiet: do not update live status repeatedly.
			AppendNotificationLog(message);
		}
	}

	private void TrimSeenWeChatMessageKeys(IReadOnlyList<WeChatChatMessage> latestMessages)
	{
		if (_seenWeChatChatMessages.Count <= MaxSeenMessageKeys)
		{
			return;
		}
		HashSet<string> hashSet = (from m in latestMessages
			select m.StableKey into k
			where !string.IsNullOrWhiteSpace(k)
			select k).ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
		_seenWeChatChatMessages.Clear();
		foreach (string item in hashSet)
		{
			_seenWeChatChatMessages.Add(item);
		}
	}

	private void DetectWeChatWindow()
	{
		try
		{
			SetStatus("正在检测微信窗口。请稍等。建议先把电脑版微信主窗口打开，并切换到一个有消息的聊天窗口。 ");
			WeChatWindowScanResult weChatWindowScanResult = _weChatWindowService.ScanVisibleWeChatText();
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine("微信窗口检测结果：" + weChatWindowScanResult.Summary);
			if (weChatWindowScanResult.WindowDescriptions.Count > 0)
			{
				stringBuilder.AppendLine("窗口信息：");
				foreach (string windowDescription in weChatWindowScanResult.WindowDescriptions)
				{
					stringBuilder.AppendLine(windowDescription);
				}
			}
			if (weChatWindowScanResult.VisibleTexts.Count > 0)
			{
				stringBuilder.AppendLine("可见文本：");
				int num = 1;
				foreach (string visibleText in weChatWindowScanResult.VisibleTexts)
				{
					StringBuilder stringBuilder2 = stringBuilder;
					StringBuilder stringBuilder3 = stringBuilder2;
					StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(2, 2, stringBuilder2);
					handler.AppendFormatted(num);
					handler.AppendLiteral(". ");
					handler.AppendFormatted(visibleText);
					stringBuilder3.AppendLine(ref handler);
					num++;
				}
			}
			IReadOnlyList<WeChatChatMessage> readOnlyList = _weChatWindowService.ReadCurrentChatMessages(30);
			stringBuilder.AppendLine("当前聊天文字消息解析结果：共 " + readOnlyList.Count + " 条。");
			int num2 = 1;
			foreach (WeChatChatMessage item in readOnlyList)
			{
				StringBuilder stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder4 = stringBuilder2;
				StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(13, 4, stringBuilder2);
				handler.AppendFormatted(num2);
				handler.AppendLiteral(". 聊天：");
				handler.AppendFormatted(item.ChatName);
				handler.AppendLiteral("；类型：");
				handler.AppendFormatted(item.MessageType);
				handler.AppendLiteral("；内容：");
				handler.AppendFormatted(item.Text);
				handler.AppendLiteral("；标识：");
				handler.AppendFormatted(item.StableKey);
				stringBuilder4.AppendLine(ref handler);
				num2++;
			}
			string value = _weChatWindowService.BuildCurrentChatUiaMessageDiagnostics();
			if (!string.IsNullOrWhiteSpace(value))
			{
				stringBuilder.AppendLine();
				stringBuilder.AppendLine(value);
			}
			AppendNotificationLog(stringBuilder.ToString().Trim());
			SetStatus(weChatWindowScanResult.Summary + " 检测结果已写入通知记录。请复制当前聊天文字消息解析结果和 UIA 消息子控件诊断内容。");
		}
		catch (Exception ex)
		{
			SetStatus("检测微信窗口失败：" + ex.Message);
			AppendNotificationLog("检测微信窗口失败：" + ex.Message);
		}
	}

	private void DetectMsaaWeChatWindow()
	{
		try
		{
			SetStatus("正在执行 MSAA/IAccessible 微信诊断。请先打开目标电脑版微信聊天窗口。");
			MsaaScanResult msaaScanResult = _msaaScanService.ScanWeChatWindows();
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine("MSAA WeChat scan result: " + msaaScanResult.Summary);
			stringBuilder.AppendLine("说明：请重点搜索 Name、Value、Description、RoleText、Location，查看是否出现微信聊天文字消息正文。MSAA 只用于诊断，不参与正式朗读。");
			if (msaaScanResult.Highlights.Count > 0)
			{
				stringBuilder.AppendLine();
				stringBuilder.AppendLine("MSAA highlighted candidate lines, for quick review:");
				foreach (string highlight in msaaScanResult.Highlights)
				{
					stringBuilder.AppendLine(highlight);
				}
			}
			if (msaaScanResult.Lines.Count > 0)
			{
				stringBuilder.AppendLine();
				stringBuilder.AppendLine("MSAA full accessible object information:");
				foreach (string line in msaaScanResult.Lines)
				{
					stringBuilder.AppendLine(line);
				}
			}
			string text = stringBuilder.ToString().Trim();
			string text2 = SaveMsaaScanLog(text);
			AppendNotificationLog(text + Environment.NewLine + "MSAA 诊断完整文本已保存到：" + text2);
			SetStatus(msaaScanResult.Summary + " 结果已写入通知记录，并保存到文件：" + text2 + "。请复制通知记录或发送这个文件内容。快捷键：在通知记录框按 Ctrl+A，再按 Ctrl+C。");
		}
		catch (Exception ex)
		{
			SetStatus("MSAA 微信诊断失败：" + ex.Message);
			AppendNotificationLog("MSAA 微信诊断失败：" + ex.Message);
		}
	}

	private string SaveMsaaScanLog(string text)
	{
		string text2 = Path.Combine(FindProjectRoot(), "logs");
		Directory.CreateDirectory(text2);
		string text3 = Path.Combine(text2, "msaa-wechat-scan-latest.txt");
		File.WriteAllText(text3, text, Encoding.UTF8);
		File.WriteAllText(Path.Combine(text2, "msaa-wechat-scan-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt"), text, Encoding.UTF8);
		return text3;
	}

	private void LoadSettingsToUi()
	{
		AppSettings appSettings = _settingsService.Load();
		RateSlider.Value = appSettings.SpeechRate;
		VolumeSlider.Value = appSettings.SpeechVolume;
		SelectSpeechBackend(appSettings.SpeechBackend);
		SelectGlobalMonitorHotkey(appSettings.GlobalMonitorHotkey);
		OnlyWeChatCheckBox.IsChecked = appSettings.OnlyWeChatNotifications;
		AutoReadWeChatCheckBox.IsChecked = appSettings.AutoReadWeChatNotifications;
		MonitorIntervalSlider.Value = appSettings.WeChatMonitorIntervalMilliseconds;
		ApplyMonitorIntervalFromUi();
		UpdateMonitorIntervalText();
		ApplyGlobalMonitorHotkeyFromUi();
		ApplySpeechBackendFromUi(showStatus: false);
	}

	private void SaveSettingsFromUi()
	{
		SaveSettingsFromUi(showStatus: true);
	}

	private void SaveSettingsFromUi(bool showStatus)
	{
		try
		{
			AppSettings settings = new AppSettings
			{
				SpeechRate = (int)RateSlider.Value,
				SpeechVolume = (int)VolumeSlider.Value,
				SpeechBackend = GetSelectedSpeechBackend(),
				GlobalMonitorHotkey = GetSelectedGlobalMonitorHotkey(),
				OnlyWeChatNotifications = (OnlyWeChatCheckBox.IsChecked == true),
				AutoReadWeChatNotifications = (AutoReadWeChatCheckBox.IsChecked == true),
				WeChatMonitorIntervalMilliseconds = (int)MonitorIntervalSlider.Value
			};
			_settingsService.Save(settings);
			ApplyMonitorIntervalFromUi();
			ApplyGlobalMonitorHotkeyFromUi();
			ApplySpeechBackendFromUi(showStatus: false);
			if (showStatus)
			{
				string message = "设置已保存。全局快捷键：" + GetGlobalMonitorHotkeyDisplayName(settings.GlobalMonitorHotkey)
					+ "。当前朗读方式：" + _speechService.SelectedBackendDisplayName
					+ "。设置文件：" + _settingsService.SettingsPath;
				SetStatus(message);
				AppendNotificationLog(message);
				_ = SpeakShortUiFeedbackAsync("设置已保存");
			}
		}
		catch (Exception ex)
		{
			if (showStatus)
			{
				SetStatus("保存设置失败：" + ex.Message);
			}
		}
	}

	private string GetSelectedSpeechBackend()
	{
		if (SpeechBackendComboBox.SelectedItem is ComboBoxItem { Tag: string tag })
		{
			if (tag.Equals("AUTO", StringComparison.OrdinalIgnoreCase))
			{
				return "AUTO";
			}

			if (tag.Equals("ZDSRAPI", StringComparison.OrdinalIgnoreCase))
			{
				return "ZDSRAPI";
			}

			if (tag.Equals("BAOYI", StringComparison.OrdinalIgnoreCase))
			{
				return "BAOYI";
			}

			if (tag.Equals("NVDA", StringComparison.OrdinalIgnoreCase))
			{
				return "NVDA";
			}
		}

		return "SAPI";
	}

	private string GetSelectedGlobalMonitorHotkey()
	{
		if (GlobalMonitorHotkeyComboBox.SelectedItem is ComboBoxItem { Tag: string tag })
		{
			if (tag.Equals("Off", StringComparison.OrdinalIgnoreCase))
			{
				return "Off";
			}

			if (tag.Equals("Ctrl+Shift+M", StringComparison.OrdinalIgnoreCase))
			{
				return "Ctrl+Shift+M";
			}

			if (tag.Equals("Alt+Shift+M", StringComparison.OrdinalIgnoreCase))
			{
				return "Alt+Shift+M";
			}

			if (tag.Equals("Ctrl+Alt+W", StringComparison.OrdinalIgnoreCase))
			{
				return "Ctrl+Alt+W";
			}
		}

		return "Ctrl+Alt+M";
	}

	private static string GetGlobalMonitorHotkeyDisplayName(string hotkey)
	{
		if (hotkey.Equals("Off", StringComparison.OrdinalIgnoreCase))
		{
			return "关闭";
		}

		return hotkey.Replace("+", " 加 ");
	}

	private void SelectSpeechBackend(string backend)
	{
		string value = string.Equals(backend, "AUTO", StringComparison.OrdinalIgnoreCase) ? "AUTO" : (string.Equals(backend, "ZDSRAPI", StringComparison.OrdinalIgnoreCase) ? "ZDSRAPI" : (string.Equals(backend, "BAOYI", StringComparison.OrdinalIgnoreCase) ? "BAOYI" : (string.Equals(backend, "NVDA", StringComparison.OrdinalIgnoreCase) ? "NVDA" : "SAPI")));
		foreach (ComboBoxItem item in SpeechBackendComboBox.Items.OfType<ComboBoxItem>())
		{
			if (item.Tag is string text && text.Equals(value, StringComparison.OrdinalIgnoreCase))
			{
				SpeechBackendComboBox.SelectedItem = item;
				return;
			}
		}
		SpeechBackendComboBox.SelectedIndex = 0;
	}

	private void SelectGlobalMonitorHotkey(string hotkey)
	{
		string value = hotkey.Equals("Off", StringComparison.OrdinalIgnoreCase) ? "Off" : (hotkey.Equals("Ctrl+Shift+M", StringComparison.OrdinalIgnoreCase) ? "Ctrl+Shift+M" : (hotkey.Equals("Alt+Shift+M", StringComparison.OrdinalIgnoreCase) ? "Alt+Shift+M" : (hotkey.Equals("Ctrl+Alt+W", StringComparison.OrdinalIgnoreCase) ? "Ctrl+Alt+W" : "Ctrl+Alt+M")));
		foreach (ComboBoxItem item in GlobalMonitorHotkeyComboBox.Items.OfType<ComboBoxItem>())
		{
			if (item.Tag is string text && text.Equals(value, StringComparison.OrdinalIgnoreCase))
			{
				GlobalMonitorHotkeyComboBox.SelectedItem = item;
				return;
			}
		}
		GlobalMonitorHotkeyComboBox.SelectedIndex = 0;
	}

	private void ApplyGlobalMonitorHotkeyFromUi()
	{
		_globalKeyboardStopService.ConfigureGlobalMonitorHotkey(GetSelectedGlobalMonitorHotkey());
	}

	private void ApplySpeechBackendFromUi(bool showStatus)
	{
		if (_speechService == null)
		{
			return;
		}
		string selectedSpeechBackend = GetSelectedSpeechBackend();
		_speechService.ConfigureBackend(selectedSpeechBackend);
		if (showStatus)
		{
			if (selectedSpeechBackend.Equals("AUTO", StringComparison.OrdinalIgnoreCase))
			{
				SetStatus("\u5df2\u9009\u62e9\u81ea\u52a8\u68c0\u6d4b\u5f53\u524d\u8bfb\u5c4f\u3002\u6bcf\u6b21\u6717\u8bfb\u524d\u4f1a\u6309\u4e89\u6e21\u3001\u4fdd\u76ca\u3001NVDA \u7684\u987a\u5e8f\u68c0\u6d4b\u53ef\u7528\u63a5\u53e3\uff0c\u90fd\u4e0d\u53ef\u7528\u65f6\u56de\u9000 Windows SAPI \u7cfb\u7edf\u8bed\u97f3\u5e93\u4fdd\u5e95\u3002\u53ef\u4ee5\u6309 Alt \u52a0 R \u6d4b\u8bd5\uff0c\u6309 Alt \u52a0 E \u67e5\u770b\u8bca\u65ad\u3002");
			}
			else if (selectedSpeechBackend.Equals("ZDSRAPI", StringComparison.OrdinalIgnoreCase))
			{
				SetStatus("\u5df2\u9009\u62e9\u4e89\u6e21\u5b98\u65b9\u63a5\u53e3\u3002\u5982\u679c\u4e89\u6e21\u63a5\u53e3\u4e0d\u53ef\u7528\uff0c\u4f1a\u81ea\u52a8\u56de\u9000 Windows SAPI \u672c\u5730\u8bed\u97f3\u5e93\u4fdd\u5e95\uff0c\u907f\u514d\u5fae\u4fe1\u6d88\u606f\u65e0\u58f0\u3002\u53ef\u4ee5\u6309 Alt \u52a0 R \u6d4b\u8bd5\uff0c\u6309 Alt \u52a0 E \u67e5\u770b\u8bca\u65ad\u3002");
			}
			else if (selectedSpeechBackend.Equals("BAOYI", StringComparison.OrdinalIgnoreCase))
			{
				SetStatus("\u5df2\u9009\u62e9\u4fdd\u76ca\u4e09\u65b9\u6717\u8bfb\u670d\u52a1\u3002\u4f18\u5148\u4f7f\u7528\u65b0\u7248 byctrl-x64.dll\uff0c\u627e\u4e0d\u5230\u65f6\u517c\u5bb9\u65e7\u7248 BoyCtrl-x64.dll\u3002\u5982\u679c\u4fdd\u76ca\u8bfb\u5c4f\u6ca1\u6709\u8fd0\u884c\u6216\u63a5\u53e3\u4e0d\u53ef\u7528\uff0c\u4f1a\u81ea\u52a8\u56de\u9000 Windows SAPI \u672c\u5730\u8bed\u97f3\u5e93\u4fdd\u5e95\u3002\u53ef\u4ee5\u6309 Alt \u52a0 R \u6d4b\u8bd5\uff0c\u6309 Alt \u52a0 E \u67e5\u770b\u8bca\u65ad\u3002");
			}
			else if (selectedSpeechBackend.Equals("NVDA", StringComparison.OrdinalIgnoreCase))
			{
				SetStatus("\u5df2\u9009\u62e9 NVDA \u5b98\u65b9\u63a7\u5236\u63a5\u53e3\u3002\u5982\u679c NVDA \u6ca1\u6709\u8fd0\u884c\u6216\u63a5\u53e3 DLL \u4e0d\u53ef\u7528\uff0c\u4f1a\u81ea\u52a8\u56de\u9000 Windows SAPI \u672c\u5730\u8bed\u97f3\u5e93\u4fdd\u5e95\u3002\u53ef\u4ee5\u6309 Alt \u52a0 R \u6d4b\u8bd5\uff0c\u6309 Alt \u52a0 E \u67e5\u770b\u8bca\u65ad\u3002");
			}
			else
			{
				SetStatus("\u5df2\u9009\u62e9 Windows SAPI \u7cfb\u7edf\u8bed\u97f3\u5e93\uff0c\u4f5c\u4e3a\u7a33\u5b9a\u4fdd\u5e95\u3002\u53ef\u4ee5\u6309 Alt \u52a0 R \u6d4b\u8bd5\u6717\u8bfb\u3002");
			}
		}
	}

	private void ApplyMonitorIntervalFromUi()
	{
		int num = Math.Clamp((int)MonitorIntervalSlider.Value, 300, 1000);
		_weChatChatMonitorTimer.Interval = TimeSpan.FromMilliseconds(num);
	}

	private void UpdateMonitorIntervalText()
	{
		int value = Math.Clamp((int)MonitorIntervalSlider.Value, 300, 1000);
		MonitorIntervalValueText.Text = $"当前 {value} 毫秒";
		AutomationProperties.SetName(MonitorIntervalValueText, $"当前微信检测间隔 {value} 毫秒");
		AutomationProperties.SetName(MonitorIntervalSlider, $"微信检测间隔滑块，当前 {value} 毫秒");
		AutomationProperties.SetHelpText(MonitorIntervalSlider, "调整微信聊天扫描间隔。建议 500 毫秒；想更快可用 350 毫秒；CPU 偏高或消息重复时可用 700 到 1000 毫秒。");
	}

	private static string FormatNotificationLog(NotificationMessage message)
	{
		string value = (message.IsVoiceMessage ? "voice" : "text-or-other");
		string value2 = (message.IsProbablyWeChat ? "probably WeChat" : "not WeChat or unknown");
		return $"Time: {message.CreatedAt:HH:mm:ss}; App: {message.AppName}; Title: {message.Title}; Body: {message.Body}; Judgment: {value2}, {value}; SpeakText: {message.SpeakText}";
	}

	private TroubleshootingSnapshot CreateTroubleshootingSnapshot()
	{
		return new TroubleshootingSnapshot(
			DateTime.Now,
			StatusTextBox.Text.Trim(),
			NotificationLogTextBox.Text.Trim(),
			(int)RateSlider.Value,
			(int)VolumeSlider.Value,
			GetSelectedSpeechBackend(),
			GetSelectedGlobalMonitorHotkey(),
			OnlyWeChatCheckBox.IsChecked == true,
			AutoReadWeChatCheckBox.IsChecked == true,
			(int)MonitorIntervalSlider.Value,
			_isListening,
			_notificationTimer.IsEnabled,
			_isWeChatChatMonitoring,
			_weChatChatMonitorTimer.IsEnabled,
			_isWeChatChatPolling,
			_needSeedVisibleMessagesOnNextSuccessfulScan,
			_nextAllowedWeChatScanAt,
			_suppressWeChatReadingUntil,
			_consecutiveWeChatScanFailures,
			_settingsService.SettingsPath,
			_weChatReadHistoryService.HistoryPath,
			_speechService.SelectedBackendDisplayName,
			_speechService.ActualChannelDescription,
			_speechService.EngineName,
			_speechService.LastEngineError);
	}

	private string BuildTroubleshootingReport(TroubleshootingSnapshot snapshot)
	{
		StringBuilder builder = new StringBuilder();
		builder.AppendLine("微信消息朗读助手排查信息");
		builder.AppendLine("说明：请把这段内容完整发送给开发者。内容可能包含当前微信窗口可见文字、通知记录、路径、错误信息和最近运行日志。");
		builder.AppendLine("生成时间：" + snapshot.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"));

		AppendBasicTroubleshootingInfo(builder, snapshot);
		AppendSettingsTroubleshootingInfo(builder, snapshot);
		AppendSpeechTroubleshootingInfo(builder, snapshot);
		AppendWeChatTroubleshootingInfo(builder);
		AppendFocusedElementTroubleshootingInfo(builder);
		AppendRelatedProcessTroubleshootingInfo(builder);
		AppendErrorReportTroubleshootingInfo(builder);
		AppendRuntimeLogTroubleshootingInfo(builder);
		AppendLimitedTextSection(builder, "当前状态文本", snapshot.CurrentStatus, MaxTroubleshootingSectionCharacters);
		AppendLimitedTextSection(builder, "通知记录最近内容", snapshot.NotificationLog, MaxTroubleshootingSectionCharacters);

		return LimitTroubleshootingReport(builder.ToString().Trim() + Environment.NewLine);
	}

	private static void AppendBasicTroubleshootingInfo(StringBuilder builder, TroubleshootingSnapshot snapshot)
	{
		AppendSectionTitle(builder, "一、程序和系统");
		builder.AppendLine("程序目录：" + AppContext.BaseDirectory);
		builder.AppendLine("程序路径：" + (Environment.ProcessPath ?? "未知"));
		builder.AppendLine("进程 ID：" + Environment.ProcessId);
		builder.AppendLine("当前工作目录：" + Environment.CurrentDirectory);
		builder.AppendLine("命令行：" + Environment.CommandLine);
		builder.AppendLine("程序版本：" + (typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "未知"));
		builder.AppendLine("文件版本：" + GetCurrentFileVersionText());
		builder.AppendLine("系统版本：" + Environment.OSVersion);
		builder.AppendLine(".NET 版本：" + Environment.Version);
		builder.AppendLine("64 位进程：" + Environment.Is64BitProcess);
		builder.AppendLine("64 位系统：" + Environment.Is64BitOperatingSystem);
		builder.AppendLine("机器名：" + Environment.MachineName);
		builder.AppendLine("用户名：" + Environment.UserName);
		builder.AppendLine("设置文件：" + snapshot.SettingsPath);
		builder.AppendLine("已朗读历史文件：" + snapshot.ReadHistoryPath);
		builder.AppendLine("通知监听开启：" + snapshot.IsNotificationListening);
		builder.AppendLine("通知监听计时器运行：" + snapshot.IsNotificationTimerEnabled);
		builder.AppendLine("微信聊天监控开启：" + snapshot.IsWeChatChatMonitoring);
		builder.AppendLine("微信聊天监控计时器运行：" + snapshot.IsWeChatChatMonitorTimerEnabled);
		builder.AppendLine("正在执行微信扫描：" + snapshot.IsWeChatChatPolling);
		builder.AppendLine("下一次允许微信扫描时间：" + FormatDateTime(snapshot.NextAllowedWeChatScanAt));
		builder.AppendLine("临时抑制微信朗读到：" + FormatDateTime(snapshot.SuppressWeChatReadingUntil));
		builder.AppendLine("下一次成功扫描是否先把可见消息视为旧消息：" + snapshot.NeedSeedVisibleMessagesOnNextSuccessfulScan);
		builder.AppendLine("连续微信扫描失败次数：" + snapshot.ConsecutiveWeChatScanFailures);
	}

	private static void AppendSettingsTroubleshootingInfo(StringBuilder builder, TroubleshootingSnapshot snapshot)
	{
		AppendSectionTitle(builder, "二、当前设置");
		builder.AppendLine("界面语速：" + snapshot.SpeechRate);
		builder.AppendLine("界面音量：" + snapshot.SpeechVolume);
		builder.AppendLine("界面朗读方式键：" + snapshot.SelectedSpeechBackendKey);
		builder.AppendLine("全局暂停和恢复快捷键：" + snapshot.GlobalMonitorHotkey);
		builder.AppendLine("界面朗读方式：" + snapshot.SelectedBackendDisplayName);
		builder.AppendLine("当前实际朗读通道：" + snapshot.ActualChannelDescription);
		builder.AppendLine("当前引擎名称：" + snapshot.EngineName);
		builder.AppendLine("最近一次朗读引擎错误：" + (string.IsNullOrWhiteSpace(snapshot.LastEngineError) ? "无" : snapshot.LastEngineError));
		builder.AppendLine("只处理疑似微信通知：" + snapshot.OnlyWeChatNotifications);
		builder.AppendLine("自动朗读微信通知：" + snapshot.AutoReadWeChatNotifications);
		builder.AppendLine("微信监控间隔毫秒：" + snapshot.WeChatMonitorIntervalMilliseconds);

		AppendPathStatus(builder, "设置文件", snapshot.SettingsPath);
		if (File.Exists(snapshot.SettingsPath))
		{
			AppendLimitedTextSection(builder, "settings.json 内容", ReadTextFileTail(snapshot.SettingsPath, 12000), 12000);
		}

		AppendPathStatus(builder, "已朗读历史文件", snapshot.ReadHistoryPath);
	}

	private void AppendSpeechTroubleshootingInfo(StringBuilder builder, TroubleshootingSnapshot snapshot)
	{
		AppendSectionTitle(builder, "三、朗读引擎诊断");
		try
		{
			string diagnosticText = _speechService.GetDiagnosticText(snapshot.SettingsPath);
			builder.AppendLine(LimitTroubleshootingText(diagnosticText, MaxTroubleshootingSectionCharacters));
		}
		catch (Exception ex)
		{
			builder.AppendLine("朗读引擎诊断失败：" + ex);
		}

		string[] nativeFiles =
		{
			"ZDSRAPI_x64.dll",
			"ZDSRAPI.ini",
			"byctrl-x64.dll",
			"byctrl.conf",
			"BoyCtrl-x64.dll",
			"BoyCtrl.conf",
			"nvdaControllerClient64.dll"
		};

		builder.AppendLine();
		builder.AppendLine("读屏接口文件检查：");
		foreach (string fileName in nativeFiles)
		{
			AppendPathStatus(builder, fileName, Path.Combine(AppContext.BaseDirectory, fileName));
		}
	}

	private async Task SpeakShortUiFeedbackAsync(string text)
	{
		try
		{
			await _speechService.SpeakAsync(text, (int)RateSlider.Value, (int)VolumeSlider.Value);
		}
		catch (Exception ex)
		{
			AppendNotificationLog("朗读界面提示失败：" + ex.Message);
		}
	}

	private void AppendWeChatTroubleshootingInfo(StringBuilder builder)
	{
		AppendSectionTitle(builder, "四、微信窗口和 UIA 诊断");
		try
		{
			(bool canScan, string message, int retryAfterSeconds) = GetWeChatScanReadiness(DateTime.Now);
			builder.AppendLine("微信扫描准备状态：CanScan=" + canScan + "；RetryAfterSeconds=" + retryAfterSeconds + "；Message=" + message);
		}
		catch (Exception ex)
		{
			builder.AppendLine("微信扫描准备状态读取失败：" + ex.Message);
		}

		try
		{
			WeChatWindowScanResult scanResult = _weChatWindowService.ScanVisibleWeChatText(80);
			builder.AppendLine("微信窗口检测结果：" + scanResult.Summary);
			builder.AppendLine("窗口信息数量：" + scanResult.WindowDescriptions.Count);
			foreach (string description in scanResult.WindowDescriptions.Take(40))
			{
				builder.AppendLine(description);
			}

			builder.AppendLine("可见文本数量：" + scanResult.VisibleTexts.Count);
			int textIndex = 1;
			foreach (string visibleText in scanResult.VisibleTexts.Take(80))
			{
				builder.AppendLine(textIndex + ". " + visibleText);
				textIndex++;
			}
		}
		catch (Exception ex)
		{
			builder.AppendLine("微信窗口检测失败：" + ex);
		}

		try
		{
			WeChatChatViewportState viewportState = _weChatWindowService.GetCurrentChatViewportState();
			builder.AppendLine("聊天列表滚动状态：Found=" + viewportState.Found + "；IsNearBottom=" + viewportState.IsNearBottom + "；Summary=" + viewportState.Summary);
		}
		catch (Exception ex)
		{
			builder.AppendLine("聊天列表滚动状态读取失败：" + ex.Message);
		}

		try
		{
			IReadOnlyList<WeChatChatMessage> messages = _weChatWindowService.ReadCurrentChatMessages(30);
			builder.AppendLine("当前聊天文字消息解析结果：共 " + messages.Count + " 条。");
			int messageIndex = 1;
			foreach (WeChatChatMessage item in messages)
			{
				builder.AppendLine(messageIndex + ". 聊天：" + item.ChatName + "；类型：" + item.MessageType + "；内容：" + item.Text + "；标识：" + item.StableKey);
				messageIndex++;
			}
		}
		catch (Exception ex)
		{
			builder.AppendLine("当前聊天文字消息解析失败：" + ex);
		}

		try
		{
			string uiaDiagnostics = _weChatWindowService.BuildCurrentChatUiaMessageDiagnostics(maxItems: 8, maxSiblingsPerItem: 8);
			AppendLimitedTextSection(builder, "微信 UIA 消息子控件诊断", uiaDiagnostics, MaxTroubleshootingSectionCharacters);
		}
		catch (Exception ex)
		{
			builder.AppendLine("微信 UIA 消息子控件诊断失败：" + ex);
		}
	}

	private static void AppendFocusedElementTroubleshootingInfo(StringBuilder builder)
	{
		AppendSectionTitle(builder, "五、当前焦点控件");
		try
		{
			AutomationElement? focused = AutomationElement.FocusedElement;
			if (focused == null)
			{
				builder.AppendLine("当前没有焦点控件。");
				return;
			}

			builder.AppendLine("Name：" + SafeRead(() => focused.Current.Name));
			builder.AppendLine("AutomationId：" + SafeRead(() => focused.Current.AutomationId));
			builder.AppendLine("ClassName：" + SafeRead(() => focused.Current.ClassName));
			builder.AppendLine("ControlType：" + SafeRead(() => focused.Current.ControlType.ProgrammaticName));
			builder.AppendLine("ProcessId：" + SafeRead(() => focused.Current.ProcessId.ToString()));
			builder.AppendLine("NativeWindowHandle：" + SafeRead(() => focused.Current.NativeWindowHandle.ToString()));
			builder.AppendLine("BoundingRectangle：" + SafeRead(() => FormatAutomationRect(focused.Current.BoundingRectangle)));
		}
		catch (Exception ex)
		{
			builder.AppendLine("读取当前焦点控件失败：" + ex);
		}
	}

	private static void AppendRelatedProcessTroubleshootingInfo(StringBuilder builder)
	{
		AppendSectionTitle(builder, "六、相关进程");
		List<string> lines = new List<string>();
		foreach (Process process in Process.GetProcesses())
		{
			try
			{
				if (!IsRelevantProcessName(process.ProcessName))
				{
					continue;
				}

				lines.Add(DescribeProcess(process));
			}
			catch (Exception ex)
			{
				lines.Add("进程读取失败：" + ex.Message);
			}
			finally
			{
				process.Dispose();
			}
		}

		if (lines.Count == 0)
		{
			builder.AppendLine("未检测到微信、读屏或助手相关进程。");
			return;
		}

		foreach (string line in lines.OrderBy(line => line, StringComparer.OrdinalIgnoreCase))
		{
			builder.AppendLine(line);
		}
	}

	private static void AppendErrorReportTroubleshootingInfo(StringBuilder builder)
	{
		AppendSectionTitle(builder, "七、错误报告");
		string errorReportPath = Path.Combine(AppContext.BaseDirectory, "错误报告.txt");
		AppendPathStatus(builder, "错误报告", errorReportPath);
		if (File.Exists(errorReportPath))
		{
			AppendLimitedTextSection(builder, "错误报告最近内容", ReadTextFileTail(errorReportPath, MaxTroubleshootingSectionCharacters), MaxTroubleshootingSectionCharacters);
		}
	}

	private static void AppendRuntimeLogTroubleshootingInfo(StringBuilder builder)
	{
		AppendSectionTitle(builder, "八、最近运行日志");
		string runtimeLogDirectory = Path.Combine(AppContext.BaseDirectory, "runtime_logs");
		AppendDirectoryStatus(builder, "运行日志目录", runtimeLogDirectory);
		if (!Directory.Exists(runtimeLogDirectory))
		{
			return;
		}

		FileInfo[] files;
		try
		{
			files = Directory.EnumerateFiles(runtimeLogDirectory, "*", SearchOption.TopDirectoryOnly)
				.Select(path => new FileInfo(path))
				.OrderByDescending(file => file.LastWriteTime)
				.Take(5)
				.ToArray();
		}
		catch (Exception ex)
		{
			builder.AppendLine("枚举运行日志失败：" + ex.Message);
			return;
		}

		if (files.Length == 0)
		{
			builder.AppendLine("运行日志目录为空。");
			return;
		}

		foreach (FileInfo file in files)
		{
			AppendPathStatus(builder, "运行日志", file.FullName);
			AppendLimitedTextSection(builder, "运行日志内容：" + file.Name, ReadTextFileTail(file.FullName, 12000), 12000);
		}
	}

	private static void AppendPathStatus(StringBuilder builder, string label, string path)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(path))
			{
				builder.AppendLine(label + "：路径为空。");
				return;
			}

			FileInfo fileInfo = new FileInfo(path);
			if (fileInfo.Exists)
			{
				builder.AppendLine(label + "：存在；路径：" + fileInfo.FullName + "；大小：" + fileInfo.Length + " 字节；修改时间：" + fileInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"));
				return;
			}

			DirectoryInfo directoryInfo = new DirectoryInfo(path);
			if (directoryInfo.Exists)
			{
				builder.AppendLine(label + "：目录存在；路径：" + directoryInfo.FullName + "；修改时间：" + directoryInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"));
				return;
			}

			builder.AppendLine(label + "：不存在；路径：" + path);
		}
		catch (Exception ex)
		{
			builder.AppendLine(label + "：检查失败；路径：" + path + "；错误：" + ex.Message);
		}
	}

	private static void AppendDirectoryStatus(StringBuilder builder, string label, string path)
	{
		try
		{
			DirectoryInfo directoryInfo = new DirectoryInfo(path);
			if (directoryInfo.Exists)
			{
				builder.AppendLine(label + "：存在；路径：" + directoryInfo.FullName + "；修改时间：" + directoryInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"));
			}
			else
			{
				builder.AppendLine(label + "：不存在；路径：" + path);
			}
		}
		catch (Exception ex)
		{
			builder.AppendLine(label + "：检查失败；路径：" + path + "；错误：" + ex.Message);
		}
	}

	private static void AppendLimitedTextSection(StringBuilder builder, string title, string text, int maxCharacters)
	{
		AppendSectionTitle(builder, title);
		if (string.IsNullOrWhiteSpace(text))
		{
			builder.AppendLine("（空）");
			return;
		}

		builder.AppendLine(LimitTroubleshootingText(text, maxCharacters));
	}

	private static void AppendSectionTitle(StringBuilder builder, string title)
	{
		builder.AppendLine();
		builder.AppendLine("========== " + title + " ==========");
	}

	private static string ReadTextFileTail(string path, int maxCharacters)
	{
		try
		{
			using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			using StreamReader reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
			string text = reader.ReadToEnd();
			return LimitTroubleshootingText(text, maxCharacters);
		}
		catch (Exception ex)
		{
			return "读取文件失败：" + ex.Message;
		}
	}

	private static string LimitTroubleshootingText(string text, int maxCharacters)
	{
		if (string.IsNullOrEmpty(text) || text.Length <= maxCharacters)
		{
			return text;
		}

		int headLength = Math.Max(1000, maxCharacters / 3);
		int tailLength = Math.Max(1000, maxCharacters - headLength);
		if (headLength + tailLength >= text.Length)
		{
			return text;
		}

		return text.Substring(0, headLength)
			+ Environment.NewLine
			+ "……中间内容较长，已省略 "
			+ (text.Length - headLength - tailLength)
			+ " 个字符……"
			+ Environment.NewLine
			+ text.Substring(text.Length - tailLength);
	}

	private static string LimitTroubleshootingReport(string report)
	{
		return LimitTroubleshootingText(report, MaxTroubleshootingReportCharacters);
	}

	private static string SaveTroubleshootingReportFile(string report)
	{
		string reportPath = Path.Combine(AppContext.BaseDirectory, TroubleshootingReportFileName);
		File.WriteAllText(reportPath, report, Encoding.UTF8);
		return reportPath;
	}

	private static async Task<bool> SetClipboardTextWithRetryAsync(string text)
	{
		int[] delays =
		{
			100,
			150,
			200,
			250,
			300,
			400,
			500,
			650,
			800,
			1000,
			1200,
			1500
		};

		for (int i = 0; i < delays.Length; i++)
		{
			if (TrySetClipboardText(text))
			{
				return true;
			}

			await Task.Delay(delays[i]);
		}

		return TrySetClipboardText(text);
	}

	private static bool TrySetClipboardText(string text)
	{
		try
		{
			System.Windows.Clipboard.SetDataObject(text, copy: true);
			return true;
		}
		catch
		{
		}

		try
		{
			System.Windows.Forms.Clipboard.SetText(text);
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static string GetCurrentFileVersionText()
	{
		try
		{
			string? processPath = Environment.ProcessPath;
			if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
			{
				return "未知";
			}

			FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(processPath);
			return "FileVersion=" + versionInfo.FileVersion + "；ProductVersion=" + versionInfo.ProductVersion;
		}
		catch (Exception ex)
		{
			return "读取失败：" + ex.Message;
		}
	}

	private static string FormatDateTime(DateTime value)
	{
		if (value == DateTime.MinValue)
		{
			return "未设置";
		}

		return value.ToString("yyyy-MM-dd HH:mm:ss");
	}

	private static string FormatAutomationRect(System.Windows.Rect rect)
	{
		if (rect.IsEmpty)
		{
			return "未知";
		}

		return $"{rect.Left:0},{rect.Top:0},{rect.Right:0},{rect.Bottom:0}";
	}

	private static string SafeRead(Func<string?> read)
	{
		try
		{
			string? value = read();
			return string.IsNullOrWhiteSpace(value) ? "空" : value;
		}
		catch (Exception ex)
		{
			return "读取失败：" + ex.GetType().Name + "：" + ex.Message;
		}
	}

	private static bool IsRelevantProcessName(string processName)
	{
		return processName.Equals("WeChatMessageReaderAssistant", StringComparison.OrdinalIgnoreCase)
			|| IsWeChatProcessName(processName)
			|| processName.Equals("BoyPcReader", StringComparison.OrdinalIgnoreCase)
			|| processName.Equals("nvda", StringComparison.OrdinalIgnoreCase)
			|| processName.Equals("ZDSRMain", StringComparison.OrdinalIgnoreCase)
			|| processName.Equals("ZDSRDaemon", StringComparison.OrdinalIgnoreCase)
			|| processName.Equals("ZDSRMain_x64", StringComparison.OrdinalIgnoreCase)
			|| processName.Equals("ZDSRService", StringComparison.OrdinalIgnoreCase)
			|| processName.Contains("BoyCtrl", StringComparison.OrdinalIgnoreCase)
			|| processName.Contains("byctrl", StringComparison.OrdinalIgnoreCase);
	}

	private static string DescribeProcess(Process process)
	{
		return "进程：Name=" + SafeRead(() => process.ProcessName)
			+ "；Id=" + SafeRead(() => process.Id.ToString())
			+ "；StartTime=" + SafeRead(() => process.StartTime.ToString("yyyy-MM-dd HH:mm:ss"))
			+ "；MainWindowTitle=" + SafeRead(() => process.MainWindowTitle)
			+ "；Path=" + SafeRead(() => process.MainModule?.FileName);
	}

	private sealed record TroubleshootingSnapshot(
		DateTime CreatedAt,
		string CurrentStatus,
		string NotificationLog,
		int SpeechRate,
		int SpeechVolume,
		string SelectedSpeechBackendKey,
		string GlobalMonitorHotkey,
		bool OnlyWeChatNotifications,
		bool AutoReadWeChatNotifications,
		int WeChatMonitorIntervalMilliseconds,
		bool IsNotificationListening,
		bool IsNotificationTimerEnabled,
		bool IsWeChatChatMonitoring,
		bool IsWeChatChatMonitorTimerEnabled,
		bool IsWeChatChatPolling,
		bool NeedSeedVisibleMessagesOnNextSuccessfulScan,
		DateTime NextAllowedWeChatScanAt,
		DateTime SuppressWeChatReadingUntil,
		int ConsecutiveWeChatScanFailures,
		string SettingsPath,
		string ReadHistoryPath,
		string SelectedBackendDisplayName,
		string ActualChannelDescription,
		string EngineName,
		string LastEngineError);

	private void AppendNotificationLog(string message)
	{
		StringBuilder stringBuilder = new StringBuilder();
		if (!string.IsNullOrWhiteSpace(NotificationLogTextBox.Text) && NotificationLogTextBox.Text != "尚未捕获通知。")
		{
			stringBuilder.AppendLine(NotificationLogTextBox.Text.Trim());
		}
		stringBuilder.AppendLine(message);
		NotificationLogTextBox.Text = TrimNotificationLogText(stringBuilder.ToString());
		NotificationLogTextBox.ScrollToEnd();
		AutomationProperties.SetName(NotificationLogTextBox, "通知记录，最新内容：" + message);
	}

	private static string TrimNotificationLogText(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return string.Empty;
		}
		if (text.Length > 30000)
		{
			string text2 = text;
			int length = text2.Length;
			int num = length - 30000;
			text = text2.Substring(num, length - num);
		}
		string[] array = text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
		if (array.Length <= 200)
		{
			return string.Join(Environment.NewLine, array) + Environment.NewLine;
		}
		return string.Join(Environment.NewLine, array.Skip(array.Length - 200)) + Environment.NewLine;
	}

	private void RunMaintenanceCleanup()
	{
		DateTime now = DateTime.Now;
		if ((now - _lastMaintenanceAt).TotalMinutes < 10.0)
		{
			return;
		}
		_lastMaintenanceAt = now;
		try
		{
			CleanupRuntimeFiles();
		}
		catch
		{
		}
	}

	private static void CleanupRuntimeFiles()
	{
		string path = FindProjectRoot();
		foreach (string item in new string[4]
		{
			Path.Combine(path, "temp"),
			Path.Combine(path, "runtime_logs"),
			Path.Combine(AppContext.BaseDirectory, "temp"),
			Path.Combine(AppContext.BaseDirectory, "runtime_logs")
		}.Distinct<string>(StringComparer.OrdinalIgnoreCase))
		{
			CleanupOldFilesInFolder(item, TimeSpan.FromDays(7.0));
		}
	}

	private static string FindProjectRoot()
	{
		for (DirectoryInfo? directoryInfo = new DirectoryInfo(AppContext.BaseDirectory); directoryInfo != null; directoryInfo = directoryInfo.Parent)
		{
			if (directoryInfo.Name.Equals("WeChatMessageReaderAssistant", StringComparison.OrdinalIgnoreCase))
			{
				return directoryInfo.FullName;
			}
		}
		return "D:\\WeChatMessageReaderAssistant";
	}

	private static void CleanupOldFilesInFolder(string folder, TimeSpan maxAge)
	{
		if (!Directory.Exists(folder))
		{
			return;
		}
		DateTime dateTime = DateTime.Now - maxAge;
		foreach (string item in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
		{
			try
			{
				FileInfo fileInfo = new FileInfo(item);
				if (fileInfo.LastWriteTime < dateTime)
				{
					fileInfo.Delete();
				}
			}
			catch
			{
			}
		}
	}

	private void SetStatus(string message)
	{
		StatusTextBox.Text = message;
		AutomationProperties.SetName(StatusTextBox, "当前状态：" + message);
		(UIElementAutomationPeer.FromElement(StatusTextBox) ?? UIElementAutomationPeer.CreatePeerForElement(StatusTextBox))?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
	}

	private void AppendNotificationLogFromAnyThread(string message)
	{
		if (base.Dispatcher.CheckAccess())
		{
			AppendNotificationLog(message);
			return;
		}
		base.Dispatcher.BeginInvoke((Action)delegate
		{
			AppendNotificationLog(message);
		});
	}
}



