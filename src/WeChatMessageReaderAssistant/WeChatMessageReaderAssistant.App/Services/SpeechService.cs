using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace WeChatMessageReaderAssistant.App.Services;

/// <summary>
/// Speech output service. Default build detects the running screen reader and falls back to Windows SAPI.
/// Prism/Tolk code is kept only as old diagnostic/compatibility code and is not selected by settings.
/// </summary>
public sealed class SpeechService : IDisposable
{
    private readonly object _lock = new();
    private ISpeechEngine? _engine;
    private string _preferredBackendKey;
    private string _lastEngineError = string.Empty;

    public SpeechService(string preferredBackendKey = "AUTO")
    {
        _preferredBackendKey = NormalizeBackendKey(preferredBackendKey);
        _engine = CreateEngine(_preferredBackendKey);
    }

    public string EngineName => _engine?.EngineName ?? "None";
    public string PreferredBackendKey => _preferredBackendKey;
    public string LastEngineError => _lastEngineError;
    public bool IsActualSapi => _engine is SapiSpeechEngine;
    public bool IsRequestedBackendUnavailable => _engine is UnavailableSpeechEngine;

    public string SelectedBackendDisplayName => GetBackendDisplayName(_preferredBackendKey);
    public string ActualChannelDescription => _engine?.ChannelDescription ?? "没有可用朗读通道";

    public void ConfigureBackend(string preferredBackendKey)
    {
        preferredBackendKey = NormalizeBackendKey(preferredBackendKey);
        lock (_lock)
        {
            if (string.Equals(_preferredBackendKey, preferredBackendKey, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _preferredBackendKey = preferredBackendKey;
            _engine?.Dispose();
            _engine = CreateEngine(_preferredBackendKey);
        }
    }

    public Task SpeakAsync(string text, int rate, int volume, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.CompletedTask;
        }

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_lock)
            {
                if (ShouldRefreshEngineForEachSpeak(_preferredBackendKey, _engine))
                {
                    _engine?.Dispose();
                    _engine = CreateEngine(_preferredBackendKey);
                }

                var engine = _engine ?? CreateEngine(_preferredBackendKey);
                _engine = engine;

                try
                {
                    engine.Speak(text, rate, volume);
                    return;
                }
                catch (Exception ex)
                {
                    _lastEngineError = ex.Message;

                    try
                    {
                        engine.Dispose();
                    }
                    catch
                    {
                        // Best effort cleanup only.
                    }

                    try
                    {
                        _engine = CreateRuntimeFallbackEngine(_preferredBackendKey, ex.Message);
                        _engine.Speak(text, rate, volume);
                        return;
                    }
                    catch (Exception fallbackEx) when (!_preferredBackendKey.Equals("SAPI", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            _engine?.Dispose();
                        }
                        catch
                        {
                            // Best effort cleanup only.
                        }

                        var joinedFailure = ex.Message + "?" + fallbackEx.Message;
                        _lastEngineError = joinedFailure;
                        _engine = CreateSapiFallback(requestedBackendKey: _preferredBackendKey, lastPrismFailure: joinedFailure);
                        _engine.Speak(text, rate, volume);
                        return;
                    }
                }
            }
        }, cancellationToken);
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_engine == null)
            {
                return;
            }

            try
            {
                _engine.Stop();
            }
            catch
            {
                // Some speech backends, especially Windows SAPI COM voice, may occasionally
                // throw while stopping an utterance. Stopping speech is a best-effort action:
                // it must not surface as an application error when the user presses Control.
                try
                {
                    _engine.Dispose();
                }
                catch
                {
                    // Ignore cleanup failures; the next Speak call will create a fresh engine.
                }

                _engine = null;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _engine?.Dispose();
            _engine = null;
        }
    }

    private ISpeechEngine CreateEngine(string backendKey)
    {
        backendKey = NormalizeBackendKey(backendKey);
        _lastEngineError = string.Empty;

        if (backendKey.Equals("AUTO", StringComparison.OrdinalIgnoreCase))
        {
            return CreateAutoDetectedScreenReaderEngine();
        }

        if (backendKey.Equals("ZDSRAPI", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return CreateDirectScreenReaderEngine("ZDSRAPI");
            }
            catch (Exception ex)
            {
                _lastEngineError = "争渡官方接口初始化失败：" + ex.Message;
                return CreateSapiFallback(requestedBackendKey: "ZDSRAPI", lastPrismFailure: _lastEngineError);
            }
        }

        if (backendKey.Equals("BAOYI", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return CreateDirectScreenReaderEngine("BAOYI");
            }
            catch (Exception ex)
            {
                _lastEngineError = "\u4fdd\u76ca\u4e09\u65b9\u6717\u8bfb\u670d\u52a1\u521d\u59cb\u5316\u5931\u8d25\uff1a" + ex.Message;
                return CreateSapiFallback(requestedBackendKey: "BAOYI", lastPrismFailure: _lastEngineError);
            }
        }

        if (backendKey.Equals("NVDA", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return CreateDirectScreenReaderEngine("NVDA");
            }
            catch (Exception ex)
            {
                _lastEngineError = "NVDA \u5b98\u65b9\u63a7\u5236\u63a5\u53e3\u521d\u59cb\u5316\u5931\u8d25\uff1a" + ex.Message;
                return CreateSapiFallback(requestedBackendKey: "NVDA", lastPrismFailure: _lastEngineError);
            }
        }

        if (backendKey.Equals("SAPI", StringComparison.OrdinalIgnoreCase))
        {
            return CreateSapiFallback(requestedBackendKey: "SAPI", lastPrismFailure: string.Empty);
        }

        try
        {
            return new PrismSpeechEngine(backendKey);
        }
        catch (Exception ex)
        {
            return CreateRuntimeFallbackEngine(backendKey, ex.Message);
        }
    }

    private ISpeechEngine CreateAutoDetectedScreenReaderEngine()
    {
        var failures = new List<string>();
        var detections = GetDetectedScreenReaderBackends();
        if (detections.Count == 0)
        {
            const string noReader = "自动检测没有发现正在运行的争渡、保益或 NVDA 主程序。";
            _lastEngineError = noReader;
            return CreateSapiFallback(requestedBackendKey: "AUTO", lastPrismFailure: noReader);
        }

        foreach (var detection in detections)
        {
            try
            {
                return CreateDirectScreenReaderEngine(detection.BackendKey);
            }
            catch (Exception ex)
            {
                failures.Add(GetBackendDisplayName(detection.BackendKey) + "?" + ex.Message);
            }
        }

        var joinedFailures = string.Join("；", failures);
        _lastEngineError = joinedFailures;
        return CreateSapiFallback(requestedBackendKey: "AUTO", lastPrismFailure: joinedFailures);
    }

    private static ISpeechEngine CreateDirectScreenReaderEngine(string backendKey)
    {
        backendKey = NormalizeBackendKey(backendKey);
        return backendKey switch
        {
            "ZDSRAPI" => new ZdsrOfficialSpeechEngine(),
            "BAOYI" => new BaoyiBoyCtrlSpeechEngine(),
            "NVDA" => new NvdaControllerSpeechEngine(),
            _ => throw new InvalidOperationException("Unsupported screen reader backend: " + backendKey)
        };
    }

    private ISpeechEngine CreateRuntimeFallbackEngine(string requestedBackendKey, string firstFailure)
    {
        var failures = new List<string>();
        if (!string.IsNullOrWhiteSpace(firstFailure))
        {
            failures.Add(firstFailure);
        }

        if (requestedBackendKey.Equals("ZDSRAPI", StringComparison.OrdinalIgnoreCase)
            || requestedBackendKey.Equals("BAOYI", StringComparison.OrdinalIgnoreCase)
            || requestedBackendKey.Equals("NVDA", StringComparison.OrdinalIgnoreCase)
            || requestedBackendKey.Equals("AUTO", StringComparison.OrdinalIgnoreCase))
        {
            var directApiFailure = string.Join("?", failures);
            _lastEngineError = directApiFailure;
            return CreateSapiFallback(requestedBackendKey: requestedBackendKey, lastPrismFailure: directApiFailure);
        }

        if (!requestedBackendKey.Equals("SAPI", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var tolk = new TolkSpeechEngine(requestedBackendKey, string.Join("?", failures));
                _lastEngineError = string.Join("?", failures);
                return tolk;
            }
            catch (Exception tolkEx)
            {
                failures.Add("Tolk fallback failed: " + tolkEx.Message);
            }
        }

        var joinedFailures = string.Join("?", failures);
        _lastEngineError = joinedFailures;
        return CreateSapiFallback(requestedBackendKey: requestedBackendKey, lastPrismFailure: joinedFailures);
    }

    private static SapiSpeechEngine CreateSapiFallback(string requestedBackendKey, string lastPrismFailure)
    {
        // Do not cache SAPI fallback objects. A previous fallback may have been disposed
        // when switching Prism backends; reusing it causes ObjectDisposedException and
        // makes WeChat monitoring detect messages but fail to speak them.
        return new SapiSpeechEngine
        {
            RequestedBackendKey = requestedBackendKey,
            LastPrismFailure = lastPrismFailure
        };
    }

    private static UnavailableSpeechEngine CreateUnavailableEngine(string backendKey, string reason)
    {
        return new UnavailableSpeechEngine(GetBackendDisplayName(backendKey), reason);
    }

    private static string NormalizeBackendKey(string? key)
    {
        if (string.Equals(key, "ZDSRAPI", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "ZDSROfficial", StringComparison.OrdinalIgnoreCase))
        {
            return "ZDSRAPI";
        }

        if (string.Equals(key, "BAOYI", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "BoySynth", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "Baoyi", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "BoyCtrl", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "byctrl", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "BaoyiBoyCtrl", StringComparison.OrdinalIgnoreCase))
        {
            return "BAOYI";
        }

        if (string.Equals(key, "NVDA", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "NVDAAPI", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "NVDAController", StringComparison.OrdinalIgnoreCase))
        {
            return "NVDA";
        }

        if (string.Equals(key, "AUTO", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "AutoDetect", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "ScreenReaderAuto", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            return "AUTO";
        }

        // 默认自动检测当前正在运行的读屏；Windows SAPI 只作为最终保底。
        if (string.IsNullOrWhiteSpace(key))
        {
            return "AUTO";
        }

        return "SAPI";
    }

    private static string GetBackendDisplayName(string backendKey)
    {
        backendKey = NormalizeBackendKey(backendKey);
        return backendKey switch
        {
            "AUTO" => "自动检测当前读屏",
            "ZDSRAPI" => "争渡官方接口",
            "BAOYI" => "\u4fdd\u76ca\u4e09\u65b9\u6717\u8bfb\u670d\u52a1",
            "NVDA" => "NVDA 官方控制接口",
            "SAPI" => "Windows SAPI 本地语音库",
            _ => "Windows SAPI 本地语音库"
        };
    }

    public string GetDiagnosticText(string settingsPath)
    {
        lock (_lock)
        {
            if (ShouldRefreshEngineForEachSpeak(_preferredBackendKey, _engine))
            {
                _engine?.Dispose();
                _engine = CreateEngine(_preferredBackendKey);
            }

            var baseDirectory = AppContext.BaseDirectory;
            var lines = new List<string>
            {
                "\u6717\u8BFB\u5F15\u64CE\u8BCA\u65AD",
                "\u65F6\u95F4\uFF1A" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                "\u8BBE\u7F6E\u6587\u4EF6\uFF1A" + settingsPath,
                "\u7A0B\u5E8F\u76EE\u5F55\uFF1A" + baseDirectory,
                "\u7528\u6237\u9009\u62E9\u7684\u6717\u8BFB\u65B9\u5F0F\uFF1A" + SelectedBackendDisplayName,
                "\u5F53\u524D\u5B9E\u9645\u6717\u8BFB\u901A\u9053\uFF1A" + ActualChannelDescription,
                "\u5F53\u524D\u5F15\u64CE\u540D\u79F0\uFF1A" + EngineName,
                "\u6700\u8FD1\u4E00\u6B21\u5F15\u64CE\u9519\u8BEF\uFF1A" + (string.IsNullOrWhiteSpace(_lastEngineError) ? "\u65E0" : _lastEngineError),
                "\u8bf4\u660e\uff1a\u9009\u62e9\u81ea\u52a8\u68c0\u6d4b\u65f6\uff0c\u6bcf\u6b21\u6717\u8bfb\u524d\u4f1a\u6309\u4e89\u6e21\u3001\u4fdd\u76ca\u3001NVDA \u7684\u987a\u5e8f\u68c0\u6d4b\u5f53\u524d\u53ef\u7528\u7684\u8bfb\u5c4f\u63a5\u53e3\uff1b\u90fd\u4e0d\u53ef\u7528\u65f6\u81ea\u52a8\u56de\u9000 Windows SAPI\uff0c\u907f\u514d\u5fae\u4fe1\u6d88\u606f\u5b8c\u5168\u65e0\u58f0\u3002\u4e89\u6e21\u3001\u4fdd\u76ca\u3001NVDA \u7684\u5b9e\u9645\u8bed\u901f\u901a\u5e38\u8bf7\u5728\u8bfb\u5c4f\u8f6f\u4ef6\u672c\u8eab\u8c03\u6574\u3002",
                "Windows SAPI \u672C\u5730\u8BED\u97F3\u5E93\uFF1A" + (Type.GetTypeFromProgID("SAPI.SpVoice") == null ? "\u4E0D\u53EF\u7528" : "\u53EF\u7528"),
                "\u81EA\u52A8\u68C0\u6D4B\u8BFB\u5C4F\u8FDB\u7A0B\uFF1A" + DescribeAutoScreenReaderDetection(),
                "\u4E89\u6E21\u5B98\u65B9API\u6587\u4EF6 ZDSRAPI_x64.dll\uFF1A" + (File.Exists(Path.Combine(baseDirectory, "ZDSRAPI_x64.dll")) ? "\u5DF2\u627E\u5230" : "\u672A\u627E\u5230"),
                "\u4E89\u6E21\u5B98\u65B9API\u63A2\u6D4B\uFF1A" + ProbeZdsrOfficialApi(),
                "\u4fdd\u76ca\u4e09\u65b9\u6717\u8bfb\u670d\u52a1\u63a2\u6d4b\uff1a" + ProbeBaoyiBoyCtrl(),
                "\u4fdd\u76ca\u8bed\u97f3\u63d2\u4ef6\u63a2\u6d4b\uff1a" + ProbeBaoyiSpeechPlugin(),
                "NVDA \u5b98\u65b9\u63a7\u5236\u63a5\u53e3\u63a2\u6d4b\uff1a" + ProbeNvdaControllerApi()
            };

            return string.Join(Environment.NewLine, lines);
        }
    }

    private static IReadOnlyList<DetectedScreenReaderBackend> GetDetectedScreenReaderBackends()
    {
        var builders = new Dictionary<string, DetectedScreenReaderBackendBuilder>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var backendKey = GetScreenReaderBackendFromProcessName(process.ProcessName);
                if (string.IsNullOrWhiteSpace(backendKey))
                {
                    continue;
                }

                if (!builders.TryGetValue(backendKey, out var builder))
                {
                    builder = new DetectedScreenReaderBackendBuilder(backendKey);
                    builders[backendKey] = builder;
                }

                builder.Add(process);
            }
            catch
            {
                // A process may exit while enumerating. Ignore it and continue.
            }
            finally
            {
                process.Dispose();
            }
        }

        return builders.Values
            .Select(builder => builder.Build())
            .OrderByDescending(detection => detection.NewestStartTime)
            .ThenBy(detection => GetAutoDetectionTieBreaker(detection.BackendKey))
            .ToArray();
    }

    private static string? GetScreenReaderBackendFromProcessName(string processName)
    {
        if (string.Equals(processName, "BoyPcReader", StringComparison.OrdinalIgnoreCase))
        {
            return "BAOYI";
        }

        if (string.Equals(processName, "nvda", StringComparison.OrdinalIgnoreCase))
        {
            return "NVDA";
        }

        if (string.Equals(processName, "ZDSRMain", StringComparison.OrdinalIgnoreCase)
            || string.Equals(processName, "ZDSRDaemon", StringComparison.OrdinalIgnoreCase)
            || string.Equals(processName, "ZDSRMain_x64", StringComparison.OrdinalIgnoreCase))
        {
            return "ZDSRAPI";
        }

        return null;
    }

    private static int GetAutoDetectionTieBreaker(string backendKey)
    {
        backendKey = NormalizeBackendKey(backendKey);
        return backendKey switch
        {
            "BAOYI" => 0,
            "NVDA" => 1,
            "ZDSRAPI" => 2,
            _ => 10
        };
    }

    private static string DescribeAutoScreenReaderDetection()
    {
        var detections = GetDetectedScreenReaderBackends();
        if (detections.Count == 0)
        {
            return "\u672a\u68c0\u6d4b\u5230\u4e89\u6e21\u3001\u4fdd\u76ca\u6216 NVDA \u7684\u4e3b\u7a0b\u5e8f\u3002\u6ce8\u610f\uff1a\u5355\u72ec\u5b58\u5728 ZDSRService.exe \u4e0d\u4f1a\u88ab\u5f53\u4f5c\u6b63\u5728\u4f7f\u7528\u4e89\u6e21\u3002";
        }

        return string.Join("\uff1b", detections.Select(detection =>
            GetBackendDisplayName(detection.BackendKey) + "\uff1a" + detection.ProcessSummary));
    }

    private sealed class DetectedScreenReaderBackend
    {
        public DetectedScreenReaderBackend(string backendKey, DateTime newestStartTime, string processSummary)
        {
            BackendKey = backendKey;
            NewestStartTime = newestStartTime;
            ProcessSummary = processSummary;
        }

        public string BackendKey { get; }
        public DateTime NewestStartTime { get; }
        public string ProcessSummary { get; }
    }

    private sealed class DetectedScreenReaderBackendBuilder
    {
        private readonly HashSet<string> _processNames = new(StringComparer.OrdinalIgnoreCase);

        public DetectedScreenReaderBackendBuilder(string backendKey)
        {
            BackendKey = backendKey;
        }

        public string BackendKey { get; }
        public DateTime NewestStartTime { get; private set; } = DateTime.MinValue;

        public void Add(Process process)
        {
            _processNames.Add(process.ProcessName);
            try
            {
                var startTime = process.StartTime;
                if (startTime > NewestStartTime)
                {
                    NewestStartTime = startTime;
                }
            }
            catch
            {
                // Access to StartTime can fail for protected processes. ProcessName is enough.
            }
        }

        public DetectedScreenReaderBackend Build()
        {
            var startTimeText = NewestStartTime == DateTime.MinValue
                ? "\u542f\u52a8\u65f6\u95f4\u672a\u77e5"
                : "\u6700\u65b0\u542f\u52a8\u65f6\u95f4 " + NewestStartTime.ToString("yyyy-MM-dd HH:mm:ss");
            var processText = string.Join(",", _processNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
            return new DetectedScreenReaderBackend(BackendKey, NewestStartTime, "\u8fdb\u7a0b " + processText + "\uff0c" + startTimeText);
        }
    }

    private static string ProbeZdsrOfficialApi()
    {
        try
        {
            var init = ZdsrOfficialNative.InitTTS(1, "\u5fae\u4fe1\u6d88\u606f\u6717\u8bfb\u52a9\u624b\u8bca\u65ad", true);
            if (init != 0)
            {
                return "\u4e0d\u53ef\u7528\uff0cInitTTS=" + init + "\uff0c" + DescribeZdsrResult(init);
            }

            var state = ZdsrOfficialNative.GetSpeakState();
            return "\u53ef\u8c03\u7528\uff0cGetSpeakState=" + state + "\uff0c" + DescribeZdsrResult(state);
        }
        catch (Exception ex)
        {
            return "\u4e0d\u53ef\u7528\uff0c" + ex.Message;
        }
    }

    private static string DescribeZdsrResult(int result)
    {
        return result switch
        {
            0 => "\u6210\u529f",
            1 => "\u7248\u672c\u4e0d\u5339\u914d",
            2 => "\u4e89\u6e21\u6ca1\u6709\u8fd0\u884c\u6216\u6ca1\u6709\u6388\u6743",
            3 => "\u6b63\u5728\u6717\u8bfb",
            4 => "\u6ca1\u6709\u6717\u8bfb",
            _ => "\u672a\u77e5\u8fd4\u56de\u503c"
        };
    }

    private static string ProbeTolk()
    {
        try
        {
            TolkNative.Tolk_TrySAPI(false);
            TolkNative.Tolk_PreferSAPI(false);
            var loaded = TolkNative.Tolk_Load();
            var detectedPointer = TolkNative.Tolk_DetectScreenReader();
            var detected = Marshal.PtrToStringUni(detectedPointer);
            var hasSpeech = TolkNative.Tolk_HasSpeech();
            return "已加载=" + loaded + "，检测到读屏=" + (string.IsNullOrWhiteSpace(detected) ? "未检测到" : detected) + "，支持语音=" + hasSpeech;
        }
        catch (Exception ex)
        {
            return "不可用，" + ex.Message;
        }
    }

    private static string ProbePrismBackend(string displayName, string backendKey)
    {
        try
        {
            var config = PrismNative.prism_config_init();
            var context = PrismNative.prism_init(ref config);
            if (context == IntPtr.Zero)
            {
                return displayName + "：不可用，Prism 初始化失败";
            }

            try
            {
                var backend = PrismSpeechEngine.AcquireBackendForDiagnostics(context, backendKey);
                if (backend == IntPtr.Zero)
                {
                    return displayName + "：不可用，后端没有连接成功";
                }

                try
                {
                    var namePointer = PrismNative.prism_backend_name(backend);
                    var name = Marshal.PtrToStringUTF8(namePointer);
                    var features = PrismNative.prism_backend_get_features(backend);
                    return displayName + "：可用，后端名称=" + (string.IsNullOrWhiteSpace(name) ? backendKey : name) + "，功能=0x" + features.ToString("X");
                }
                finally
                {
                    PrismNative.prism_backend_free(backend);
                }
            }
            finally
            {
                PrismNative.prism_shutdown(context);
            }
        }
        catch (Exception ex)
        {
            return displayName + "：不可用，" + ex.Message;
        }
    }

    private static bool ShouldRefreshEngineForEachSpeak(string backendKey, ISpeechEngine? engine)
    {
        if (backendKey.Equals("AUTO", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!backendKey.Equals("SAPI", StringComparison.OrdinalIgnoreCase) && engine is SapiSpeechEngine)
        {
            return true;
        }

        // Old Prism diagnostic backends can occasionally become silent after the reader restarts or changes focus.
        return engine is PrismSpeechEngine
               && (backendKey.Equals("NVDA", StringComparison.OrdinalIgnoreCase)
                   || backendKey.Equals("ZDSR", StringComparison.OrdinalIgnoreCase));
    }

    private interface ISpeechEngine : IDisposable
    {
        string EngineName { get; }
        string ChannelDescription { get; }
        void Speak(string text, int rate, int volume);
        void Stop();
    }

    private sealed class UnavailableSpeechEngine : ISpeechEngine
    {
        private readonly string _backendDisplayName;
        private readonly string _reason;

        public UnavailableSpeechEngine(string backendDisplayName, string reason)
        {
            _backendDisplayName = backendDisplayName;
            _reason = string.IsNullOrWhiteSpace(reason) ? "未知原因" : reason;
        }

        public string EngineName => $"{_backendDisplayName}接口不可用";
        public string ChannelDescription => $"{_backendDisplayName}读屏接口连接失败，未回退到 Windows SAPI。原因：{_reason}";

        public void Speak(string text, int rate, int volume)
        {
            // Strict mode: when the user explicitly chooses NVDA/Zhengdu/OneCore,
            // do not secretly use SAPI. Keep silent and expose the failure in status/diagnostics.
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class PrismSpeechEngine : ISpeechEngine
    {
        private const ulong PrismBackendNvda = 0x89CC19C5C4AC1A56UL;
        private const ulong PrismBackendZdsr = 0x3D93C56C9E7F2A2EUL;
        private const ulong PrismBackendSapi = 0x1D6DF72422CEEE66UL;
        private const ulong PrismBackendOneCore = 0x6797D32F0D994CB4UL;

        private const ulong FeatureSupportsSpeak = 1UL << 2;
        private const ulong FeatureSupportsStop = 1UL << 7;
        private const ulong FeatureSupportsSetVolume = 1UL << 10;
        private const ulong FeatureSupportsSetRate = 1UL << 12;

        private readonly IntPtr _context;
        private IntPtr _backend;
        private readonly ulong _features;

        public PrismSpeechEngine(string preferredBackendKey)
        {
            var config = PrismNative.prism_config_init();
            _context = PrismNative.prism_init(ref config);
            if (_context == IntPtr.Zero)
            {
                throw new InvalidOperationException("Prism init failed.");
            }

            _backend = AcquireBackend(_context, preferredBackendKey);
            if (_backend == IntPtr.Zero)
            {
                PrismNative.prism_shutdown(_context);
                throw new InvalidOperationException($"Prism backend is not available: {preferredBackendKey}.");
            }

            _features = PrismNative.prism_backend_get_features(_backend);
            if ((_features & FeatureSupportsSpeak) == 0)
            {
                Dispose();
                throw new InvalidOperationException("Selected Prism backend does not support speak.");
            }

            var namePointer = PrismNative.prism_backend_name(_backend);
            var name = Marshal.PtrToStringUTF8(namePointer);
            EngineName = string.IsNullOrWhiteSpace(name) ? $"Prism {preferredBackendKey}" : "Prism " + name;
        }

        public string EngineName { get; }
        public string ChannelDescription => EngineName + " 读屏接口";

        public void Speak(string text, int rate, int volume)
        {
            if (_backend == IntPtr.Zero)
            {
                throw new ObjectDisposedException(nameof(PrismSpeechEngine));
            }

            if ((_features & FeatureSupportsSetRate) != 0)
            {
                _ = PrismNative.prism_backend_set_rate(_backend, MapRate(rate));
            }

            if ((_features & FeatureSupportsSetVolume) != 0)
            {
                _ = PrismNative.prism_backend_set_volume(_backend, MapBoostedVolume(volume));
            }

            var error = PrismNative.prism_backend_speak(_backend, text, true);
            if (error != PrismError.PRISM_OK)
            {
                throw new InvalidOperationException("Prism speak failed: " + GetErrorString(error));
            }
        }

        public void Stop()
        {
            if (_backend == IntPtr.Zero)
            {
                return;
            }

            if ((_features & FeatureSupportsStop) != 0)
            {
                _ = PrismNative.prism_backend_stop(_backend);
            }
            else
            {
                _ = PrismNative.prism_backend_speak(_backend, string.Empty, true);
            }
        }

        public void Dispose()
        {
            if (_backend != IntPtr.Zero)
            {
                PrismNative.prism_backend_free(_backend);
                _backend = IntPtr.Zero;
            }

            if (_context != IntPtr.Zero)
            {
                PrismNative.prism_shutdown(_context);
            }
        }

        internal static IntPtr AcquireBackendForDiagnostics(IntPtr context, string preferredBackendKey) => AcquireBackend(context, preferredBackendKey);

        private static IntPtr AcquireBackend(IntPtr context, string preferredBackendKey)
        {
            return preferredBackendKey switch
            {
                "NVDA" => PrismNative.prism_registry_acquire(context, PrismBackendNvda),
                "ZDSR" => PrismNative.prism_registry_acquire(context, PrismBackendZdsr),
                "SAPI" => PrismNative.prism_registry_acquire(context, PrismBackendSapi),
                "OneCore" => PrismNative.prism_registry_acquire(context, PrismBackendOneCore),
                _ => PrismNative.prism_registry_acquire_best(context),
            };
        }

        private static float MapRate(int rate)
        {
            // UI range is SAPI-like -10..10. Prism uses normalized 0..1 with 0.5 as neutral.
            return Math.Clamp((rate + 10) / 20f, 0f, 1f);
        }

        private static float MapBoostedVolume(int volume)
        {
            // Prism backends, including NVDA and Zhengdu, normally accept a normalized 0..1
            // volume. Apply a safe 1.5x curve before clamping. When the backend is already
            // at its own maximum, it may still cap the effective volume at 100%.
            var normalized = Math.Clamp(volume, 0, 100) / 100f;
            return Math.Clamp(normalized * 1.5f, 0f, 1f);
        }

        private static string GetErrorString(PrismError error)
        {
            var pointer = PrismNative.prism_error_string(error);
            return Marshal.PtrToStringUTF8(pointer) ?? error.ToString();
        }
    }

    private sealed class TolkSpeechEngine : ISpeechEngine
    {
        private readonly string _requestedBackendKey;
        private readonly string _previousFailure;
        private bool _disposed;

        public TolkSpeechEngine(string requestedBackendKey, string previousFailure)
        {
            _requestedBackendKey = NormalizeBackendKey(requestedBackendKey);
            _previousFailure = previousFailure;
            TolkNative.Tolk_TrySAPI(false);
            TolkNative.Tolk_PreferSAPI(false);
            var loaded = TolkNative.Tolk_Load();
            if (!loaded && !TolkNative.Tolk_IsLoaded())
            {
                throw new InvalidOperationException("Tolk_Load returned false.");
            }

            var detectedPointer = TolkNative.Tolk_DetectScreenReader();
            var detected = Marshal.PtrToStringUni(detectedPointer);
            DetectedScreenReader = string.IsNullOrWhiteSpace(detected) ? "未检测到具体读屏" : detected;

            if (!TolkNative.Tolk_HasSpeech())
            {
                throw new InvalidOperationException("Tolk did not detect a speech-capable screen reader. Detected=" + DetectedScreenReader);
            }
        }

        public string DetectedScreenReader { get; }
        public string EngineName => "Tolk screen reader bridge, detected " + DetectedScreenReader;
        public string ChannelDescription
        {
            get
            {
                var text = "Tolk 读屏接口（用户选择 " + GetBackendDisplayName(_requestedBackendKey) + "，实际检测到 " + DetectedScreenReader + "）";
                if (!string.IsNullOrWhiteSpace(_previousFailure))
                {
                    text += "；Prism 失败后启用";
                }

                return text;
            }
        }

        public void Speak(string text, int rate, int volume)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(TolkSpeechEngine));
            }

            TolkNative.Tolk_TrySAPI(false);
            TolkNative.Tolk_PreferSAPI(false);
            var ok = TolkNative.Tolk_Output(text, true);
            if (!ok)
            {
                ok = TolkNative.Tolk_Speak(text, true);
            }

            if (!ok)
            {
                throw new InvalidOperationException("Tolk speak/output returned false.");
            }
        }

        public void Stop()
        {
            if (!_disposed)
            {
                TolkNative.Tolk_Silence();
            }
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }


    private sealed class ZdsrOfficialSpeechEngine : ISpeechEngine
    {
        private bool _initialized;

        public ZdsrOfficialSpeechEngine()
        {
            var result = ZdsrOfficialNative.InitTTS(1, "\u5fae\u4fe1\u6d88\u606f\u6717\u8bfb\u52a9\u624b", true);
            if (result != 0)
            {
                throw new InvalidOperationException("InitTTS returned " + result + " (" + DescribeZdsrResult(result) + ")");
            }

            _initialized = true;
        }

        public string EngineName => "ZDSR official API";
        public string ChannelDescription => "\u4e89\u6e21\u5b98\u65b9API\u63a5\u53e3";

        public void Speak(string text, int rate, int volume)
        {
            if (!_initialized)
            {
                throw new ObjectDisposedException(nameof(ZdsrOfficialSpeechEngine));
            }

            // Do not write ZDSR voice scheme files here.
            // The official ZDSR API does not expose a per-message speech-rate function.
            // Writing scheme rate 0-100 can change the ZDSR screen reader's own speed at startup.

            var result = ZdsrOfficialNative.Speak(text, true);
            if (result != 0)
            {
                throw new InvalidOperationException("ZDSRAPI Speak returned " + result + " (" + DescribeZdsrResult(result) + ")");
            }
        }

        public void Stop()
        {
            if (_initialized)
            {
                ZdsrOfficialNative.StopSpeak();
            }
        }

        public void Dispose()
        {
            _initialized = false;
        }
    }

    // Important: keep this assistant read-only toward ZDSR voice scheme files.
    // Do not modify currentscheme.json or voiceschemes.json; doing so can change
    // the ZDSR screen reader's own speech speed when both programs start together.




    private sealed class NvdaControllerSpeechEngine : ISpeechEngine
    {
        private const int Success = 0;
        private bool _available;

        public NvdaControllerSpeechEngine()
        {
            EnsureNvdaDllExists();
            var result = NvdaControllerNative.TestIfRunning();
            if (result != Success)
            {
                throw new InvalidOperationException("NVDA \u6ca1\u6709\u8fd0\u884c\uff0c\u6216\u8005 NVDA Controller Client \u65e0\u6cd5\u8fde\u63a5\u3002\u8fd4\u56de\u503c\uff1a" + result);
            }

            _available = true;
        }

        public string EngineName => "NVDA \u5b98\u65b9\u63a7\u5236\u63a5\u53e3";

        public string ChannelDescription => "NVDA \u5b98\u65b9\u63a7\u5236\u63a5\u53e3";

        public void Speak(string text, int rate, int volume)
        {
            if (!_available)
            {
                throw new ObjectDisposedException(nameof(NvdaControllerSpeechEngine));
            }

            _ = NvdaControllerNative.CancelSpeech();
            var result = NvdaControllerNative.SpeakText(text);
            if (result != Success)
            {
                throw new InvalidOperationException("nvdaController_speakText failed: " + result);
            }
        }

        public void Stop()
        {
            if (_available)
            {
                _ = NvdaControllerNative.CancelSpeech();
            }
        }

        public void Dispose()
        {
            _available = false;
        }
    }

    private static class NvdaControllerNative
    {
        [DllImport("nvdaControllerClient64.dll", EntryPoint = "nvdaController_testIfRunning", CallingConvention = CallingConvention.StdCall)]
        public static extern int TestIfRunning();

        [DllImport("nvdaControllerClient64.dll", EntryPoint = "nvdaController_speakText", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall)]
        public static extern int SpeakText(string text);

        [DllImport("nvdaControllerClient64.dll", EntryPoint = "nvdaController_cancelSpeech", CallingConvention = CallingConvention.StdCall)]
        public static extern int CancelSpeech();
    }

    private static void EnsureNvdaDllExists()
    {
        var dllPath = Path.Combine(AppContext.BaseDirectory, "nvdaControllerClient64.dll");
        if (!File.Exists(dllPath))
        {
            throw new FileNotFoundException("Missing nvdaControllerClient64.dll for NVDA Controller Client API.", dllPath);
        }
    }

    private static string ProbeNvdaControllerApi()
    {
        var dllPath = Path.Combine(AppContext.BaseDirectory, "nvdaControllerClient64.dll");
        if (!File.Exists(dllPath))
        {
            return "\u672a\u627e\u5230 nvdaControllerClient64.dll\uff0c\u9009\u62e9 NVDA \u65f6\u4f1a\u56de\u9000 Windows SAPI\u3002";
        }

        try
        {
            var result = NvdaControllerNative.TestIfRunning();
            return result == 0
                ? "\u5df2\u627e\u5230 nvdaControllerClient64.dll\uff0cNVDA \u6b63\u5728\u8fd0\u884c\uff0c\u53ef\u8c03\u7528\u3002"
                : "\u5df2\u627e\u5230 nvdaControllerClient64.dll\uff0c\u4f46\u5f53\u524d\u6ca1\u6709\u8fde\u63a5\u5230\u6b63\u5728\u8fd0\u884c\u7684 NVDA\u3002\u8fd4\u56de\u503c\uff1a" + result + "\u3002\u9009\u62e9 NVDA \u65f6\u4f1a\u56de\u9000 Windows SAPI\u3002";
        }
        catch (Exception ex)
        {
            return "NVDA \u5b98\u65b9\u63a7\u5236\u63a5\u53e3\u4e0d\u53ef\u7528\uff1a" + ex.Message;
        }
    }

    private sealed class BaoyiBoyCtrlSpeechEngine : ISpeechEngine
    {
        private const int Success = 0;
        private const bool UseIndependentChannel = true;
        private const bool DoNotAppend = false;
        private const bool AllowBreak = true;
        private const string IndependentChannelName = "\u5fae\u4fe1\u6d88\u606f\u6717\u8bfb\u52a9\u624b";

        private readonly IBaoyiBoyCtrlNative _native;
        private readonly string _dllDisplayName;
        private bool _initialized;

        public BaoyiBoyCtrlSpeechEngine()
        {
            _native = CreateBaoyiBoyCtrlNative();
            _dllDisplayName = _native.DllDisplayName;

            var logPath = Path.Combine(AppContext.BaseDirectory, "runtime_logs", _native.LogFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            var result = _native.Initialize(logPath);
            if (result != Success)
            {
                throw new InvalidOperationException(_dllDisplayName + " BoyCtrlInitialize failed: " + DescribeBoyCtrlError(result));
            }

            _initialized = true;

            if (!_native.IsReaderRunning())
            {
                try
                {
                    _native.Uninitialize();
                }
                catch
                {
                }

                _initialized = false;
                throw new InvalidOperationException("Baoyi reader is not running or BoyCtrl service is unavailable. " + DescribeBoyCtrlReaderState(_native));
            }
        }

        public string EngineName => "\u4fdd\u76ca\u4e09\u65b9\u6717\u8bfb\u670d\u52a1 " + _dllDisplayName;

        public string ChannelDescription => "\u4fdd\u76ca\u4e09\u65b9\u6717\u8bfb\u670d\u52a1\uff08" + _dllDisplayName + "\uff09";

        public void Speak(string text, int rate, int volume)
        {
            if (!_initialized)
            {
                throw new ObjectDisposedException(nameof(BaoyiBoyCtrlSpeechEngine));
            }

            var result = _native.Speak(text, UseIndependentChannel, IndependentChannelName, DoNotAppend, AllowBreak, null);
            if (result != Success)
            {
                throw new InvalidOperationException(_dllDisplayName + " speak failed: " + DescribeBoyCtrlError(result));
            }
        }

        public void Stop()
        {
            if (_initialized)
            {
                _ = _native.StopSpeaking(UseIndependentChannel, IndependentChannelName);
            }
        }

        public void Dispose()
        {
            if (_initialized)
            {
                try
                {
                    _native.Uninitialize();
                }
                catch
                {
                }

                _initialized = false;
            }
        }
    }

    private interface IBaoyiBoyCtrlNative
    {
        string DllDisplayName { get; }
        string LogFileName { get; }
        int Initialize(string? logPath);
        void Uninitialize();
        int Speak(string text, bool useIndependentChannel, string independentChannelName, bool append, bool allowBreak, BoyCtrlSpeakCompleteFunc? onCompletion);
        int StopSpeaking(bool useIndependentChannel, string independentChannelName);
        bool IsReaderRunning();
        int GetReaderState();
    }

    private sealed class BaoyiByctrlNative : IBaoyiBoyCtrlNative
    {
        public string DllDisplayName => "byctrl-x64.dll";
        public string LogFileName => "byctrl.log";

        public int Initialize(string? logPath) => ByctrlNative.Initialize(logPath);

        public void Uninitialize() => ByctrlNative.Uninitialize();

        public int Speak(string text, bool useIndependentChannel, string independentChannelName, bool append, bool allowBreak, BoyCtrlSpeakCompleteFunc? onCompletion)
        {
            return ByctrlNative.Speak2(text, useIndependentChannel, independentChannelName, append, allowBreak, onCompletion);
        }

        public int StopSpeaking(bool useIndependentChannel, string independentChannelName)
        {
            return ByctrlNative.StopSpeaking2(useIndependentChannel, independentChannelName);
        }

        public bool IsReaderRunning() => ByctrlNative.IsReaderRunning();

        public int GetReaderState() => ByctrlNative.GetReaderState();
    }

    private sealed class BaoyiLegacyBoyCtrlNative : IBaoyiBoyCtrlNative
    {
        private const int FlagWithSlave = 1;
        private const int FlagAllowBreak = 4;

        public string DllDisplayName => "BoyCtrl-x64.dll";
        public string LogFileName => "BoyCtrl.log";

        public int Initialize(string? logPath) => LegacyBoyCtrlNative.Initialize(logPath);

        public void Uninitialize() => LegacyBoyCtrlNative.Uninitialize();

        public int Speak(string text, bool useIndependentChannel, string independentChannelName, bool append, bool allowBreak, BoyCtrlSpeakCompleteFunc? onCompletion)
        {
            var flags = 0;
            if (useIndependentChannel)
            {
                flags |= FlagWithSlave;
            }

            if (append)
            {
                flags |= 2;
            }

            if (allowBreak)
            {
                flags |= FlagAllowBreak;
            }

            return LegacyBoyCtrlNative.SpeakEx(text, flags, onCompletion);
        }

        public int StopSpeaking(bool useIndependentChannel, string independentChannelName)
        {
            return LegacyBoyCtrlNative.StopSpeakingEx(useIndependentChannel ? FlagWithSlave : 0);
        }

        public bool IsReaderRunning() => LegacyBoyCtrlNative.IsReaderRunning();

        public int GetReaderState() => LegacyBoyCtrlNative.GetReaderState();
    }

    private static IBaoyiBoyCtrlNative CreateBaoyiBoyCtrlNative()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var newDllPath = Path.Combine(baseDirectory, "byctrl-x64.dll");
        if (File.Exists(newDllPath))
        {
            return new BaoyiByctrlNative();
        }

        var legacyDllPath = Path.Combine(baseDirectory, "BoyCtrl-x64.dll");
        if (File.Exists(legacyDllPath))
        {
            return new BaoyiLegacyBoyCtrlNative();
        }

        throw new FileNotFoundException("Missing byctrl-x64.dll or BoyCtrl-x64.dll for Baoyi third-party speech service.", newDllPath);
    }

    private static class ByctrlNative
    {
        [DllImport("byctrl-x64.dll", EntryPoint = "BoyCtrlInitialize", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall)]
        public static extern int Initialize(string? logPath);

        [DllImport("byctrl-x64.dll", EntryPoint = "BoyCtrlUninitialize", CallingConvention = CallingConvention.StdCall)]
        public static extern void Uninitialize();

        [DllImport("byctrl-x64.dll", EntryPoint = "BoyCtrlSpeak", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall)]
        public static extern int Speak(string text, [MarshalAs(UnmanagedType.I1)] bool append, BoyCtrlSpeakCompleteFunc? onCompletion);

        [DllImport("byctrl-x64.dll", EntryPoint = "BoyCtrlSpeak2", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall)]
        public static extern int Speak2(
            string text,
            [MarshalAs(UnmanagedType.I1)] bool useSlave,
            string slaveName,
            [MarshalAs(UnmanagedType.I1)] bool append,
            [MarshalAs(UnmanagedType.I1)] bool allowBreak,
            BoyCtrlSpeakCompleteFunc? onCompletion);

        [DllImport("byctrl-x64.dll", EntryPoint = "BoyCtrlStopSpeaking", CallingConvention = CallingConvention.StdCall)]
        public static extern int StopSpeaking();

        [DllImport("byctrl-x64.dll", EntryPoint = "BoyCtrlStopSpeaking2", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall)]
        public static extern int StopSpeaking2([MarshalAs(UnmanagedType.I1)] bool useSlave, string slaveName);

        [DllImport("byctrl-x64.dll", EntryPoint = "BoyCtrlIsReaderRunning", CallingConvention = CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool IsReaderRunning();

        [DllImport("byctrl-x64.dll", EntryPoint = "BoyCtrlGetReaderState", CallingConvention = CallingConvention.StdCall)]
        public static extern int GetReaderState();
    }

    private static class LegacyBoyCtrlNative
    {
        [DllImport("BoyCtrl-x64.dll", EntryPoint = "BoyCtrlInitialize", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall)]
        public static extern int Initialize(string? logPath);

        [DllImport("BoyCtrl-x64.dll", EntryPoint = "BoyCtrlUninitialize", CallingConvention = CallingConvention.StdCall)]
        public static extern void Uninitialize();

        [DllImport("BoyCtrl-x64.dll", EntryPoint = "BoyCtrlSpeakEx", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall)]
        public static extern int SpeakEx(string text, int flags, BoyCtrlSpeakCompleteFunc? onCompletion);

        [DllImport("BoyCtrl-x64.dll", EntryPoint = "BoyCtrlStopSpeakingEx", CallingConvention = CallingConvention.StdCall)]
        public static extern int StopSpeakingEx(int flags);

        [DllImport("BoyCtrl-x64.dll", EntryPoint = "BoyCtrlIsReaderRunning", CallingConvention = CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool IsReaderRunning();

        [DllImport("BoyCtrl-x64.dll", EntryPoint = "BoyCtrlGetReaderState", CallingConvention = CallingConvention.StdCall)]
        public static extern int GetReaderState();
    }

    private delegate void BoyCtrlSpeakCompleteFunc(int reason);

    private static string ProbeBaoyiBoyCtrl()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var newDllPath = Path.Combine(baseDirectory, "byctrl-x64.dll");
        var legacyDllPath = Path.Combine(baseDirectory, "BoyCtrl-x64.dll");
        if (!File.Exists(newDllPath) && !File.Exists(legacyDllPath))
        {
            return "\u672a\u627e\u5230 byctrl-x64.dll \u6216 BoyCtrl-x64.dll\uff0c\u9009\u62e9\u4fdd\u76ca\u65f6\u4f1a\u56de\u9000 Windows SAPI\u3002";
        }

        try
        {
            var native = CreateBaoyiBoyCtrlNative();
            var logPath = Path.Combine(baseDirectory, "runtime_logs", native.DllDisplayName + "-probe.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            var init = native.Initialize(logPath);
            if (init != 0)
            {
                return native.DllDisplayName + " \u5df2\u627e\u5230\uff0c\u4f46\u521d\u59cb\u5316\u5931\u8d25\uff1a" + DescribeBoyCtrlError(init);
            }

            try
            {
                var running = native.IsReaderRunning();
                return running
                    ? native.DllDisplayName + " \u5df2\u627e\u5230\uff0c\u4fdd\u76ca\u8bfb\u5c4f\u6b63\u5728\u8fd0\u884c\u3002" + DescribeBoyCtrlReaderState(native)
                    : native.DllDisplayName + " \u5df2\u627e\u5230\uff0c\u4f46\u4fdd\u76ca\u8bfb\u5c4f\u672a\u8fd0\u884c\u6216\u670d\u52a1\u4e0d\u53ef\u7528\u3002" + DescribeBoyCtrlReaderState(native);
            }
            finally
            {
                native.Uninitialize();
            }
        }
        catch (Exception ex)
        {
            return "\u4fdd\u76ca\u4e09\u65b9\u6717\u8bfb\u670d\u52a1\u63a2\u6d4b\u5931\u8d25\uff1a" + ex.Message;
        }
    }

    private static string DescribeBoyCtrlReaderState(IBaoyiBoyCtrlNative native)
    {
        try
        {
            return native.GetReaderState() switch
            {
                0 => "\u8bfb\u5c4f\u72b6\u6001\uff1a\u672a\u8fd0\u884c",
                1 => "\u8bfb\u5c4f\u72b6\u6001\uff1a\u6b63\u5728\u8fd0\u884c\uff0c\u529f\u80fd\u53d7\u9650",
                2 => "\u8bfb\u5c4f\u72b6\u6001\uff1a\u6b63\u5728\u8fd0\u884c\uff0c\u529f\u80fd\u4e0d\u53d7\u9650",
                _ => "\u8bfb\u5c4f\u72b6\u6001\uff1a\u672a\u77e5"
            };
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string DescribeBoyCtrlError(int error) => error switch
    {
        0 => "success",
        1 => "operation failed",
        2 => "argument error",
        3 => "service unavailable; Baoyi reader may not be running",
        _ => "unknown error " + error
    };

    private sealed class BaoyiSpeechPluginEngine : ISpeechEngine
    {
        private const uint SoundSync = 0x0000;
        private const uint SoundFilename = 0x00020000;

        private readonly object _pluginLock = new();
        private readonly IntPtr _library;
        private readonly BoySynthInitializeDelegate _initialize;
        private readonly BoySynthUninitializeDelegate _uninitialize;
        private readonly BoySynthGetInfoDelegate _getInfo;
        private readonly BoySynthGetVoiceListDelegate _getVoiceList;
        private readonly BoySynthGetAudioFormatDelegate _getAudioFormat;
        private readonly BoySynthSetPlayCallbackDelegate _setPlayCallback;
        private readonly BoySynthSpeakDelegate _speak;
        private readonly BoySynthStopDelegate _stop;
        private readonly BoySynthPrepareVoiceDelegate? _prepareVoice;
        private readonly BoySynthSetRateDelegate? _setRate;
        private readonly BoySynthSetVolumeDelegate? _setVolume;
        private readonly BoySynthLogProc _logCallback;
        private readonly BoySynthPlayProc _playCallback;
        private readonly List<string> _temporaryWaveFiles = new();
        private readonly ManualResetEventSlim _speakCompleted = new(false);
        private MemoryStream? _pcmStream;
        private BoySynthAudioFormat _format;
        private string _voiceName = string.Empty;
        private string _pluginName = "\u4fdd\u76ca\u8bed\u97f3\u63d2\u4ef6";
        private bool _initialized;

        public BaoyiSpeechPluginEngine()
        {
            var pluginPath = FindCompatiblePluginPath(out var probeMessage);
            if (string.IsNullOrWhiteSpace(pluginPath))
            {
                throw new InvalidOperationException(probeMessage);
            }

            _library = NativeLibrary.Load(pluginPath);
            _initialize = GetRequiredDelegate<BoySynthInitializeDelegate>("BoySynthInitialize");
            _uninitialize = GetRequiredDelegate<BoySynthUninitializeDelegate>("BoySynthUninitialize");
            _getInfo = GetRequiredDelegate<BoySynthGetInfoDelegate>("BoySynthGetInfo");
            _getVoiceList = GetRequiredDelegate<BoySynthGetVoiceListDelegate>("BoySynthGetVoiceList");
            _getAudioFormat = GetRequiredDelegate<BoySynthGetAudioFormatDelegate>("BoySynthGetAudioFormat");
            _setPlayCallback = GetRequiredDelegate<BoySynthSetPlayCallbackDelegate>("BoySynthSetPlayCallback");
            _speak = GetRequiredDelegate<BoySynthSpeakDelegate>("BoySynthSpeak");
            _stop = GetRequiredDelegate<BoySynthStopDelegate>("BoySynthStop");
            _prepareVoice = GetOptionalDelegate<BoySynthPrepareVoiceDelegate>("BoySynthPrepareVoice");
            _setRate = GetOptionalDelegate<BoySynthSetRateDelegate>("BoySynthSetRate");
            _setVolume = GetOptionalDelegate<BoySynthSetVolumeDelegate>("BoySynthSetVolume");
            _logCallback = OnPluginLog;
            _playCallback = OnPluginPlay;

            if (!_initialize(_logCallback))
            {
                throw new InvalidOperationException("BoySynthInitialize \u8fd4\u56de\u5931\u8d25\u3002");
            }

            _initialized = true;

            if (_getInfo(out var info))
            {
                var name = info.NameAsString;
                var version = info.VersionAsString;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _pluginName = string.IsNullOrWhiteSpace(version) ? name : name + " " + version;
                }
            }

            if (!_getAudioFormat(out _format) || _format.BitsPerSample != 16 || _format.SampleRate <= 0 || _format.NumChannel <= 0)
            {
                throw new InvalidOperationException("\u4fdd\u76ca\u8bed\u97f3\u63d2\u4ef6\u6ca1\u6709\u8fd4\u56de\u6709\u6548\u7684 16-bit PCM \u97f3\u9891\u683c\u5f0f\u3002");
            }

            _setPlayCallback(_playCallback);
            _voiceName = GetFirstVoiceName() ?? string.Empty;
            if (_prepareVoice != null && !_prepareVoice(_voiceName))
            {
                throw new InvalidOperationException("BoySynthPrepareVoice \u8fd4\u56de\u5931\u8d25\uff0c\u89d2\u8272\uff1a" + _voiceName);
            }
        }

        public string EngineName => _pluginName;
        public string ChannelDescription => "\u4fdd\u76ca\u8bed\u97f3\u63d2\u4ef6";

        public void Speak(string text, int rate, int volume)
        {
            if (!_initialized)
            {
                throw new ObjectDisposedException(nameof(BaoyiSpeechPluginEngine));
            }

            lock (_pluginLock)
            {
                StopPlaybackNoLock();
                CleanupTemporaryFilesNoLock();
                _pcmStream = new MemoryStream();
                _speakCompleted.Reset();

                _setRate?.Invoke(_voiceName, MapUiRateToPercent(rate));
                _setVolume?.Invoke(_voiceName, Math.Clamp(volume, 0, 100));

                if (!_speak(_voiceName, text, Environment.TickCount))
                {
                    throw new InvalidOperationException("BoySynthSpeak \u8fd4\u56de\u5931\u8d25\u3002");
                }

                _speakCompleted.Wait(TimeSpan.FromSeconds(15));
                var pcm = _pcmStream.ToArray();
                _pcmStream.Dispose();
                _pcmStream = null;

                if (pcm.Length == 0)
                {
                    throw new InvalidOperationException("\u4fdd\u76ca\u8bed\u97f3\u63d2\u4ef6\u6ca1\u6709\u8fd4\u56de\u97f3\u9891\u6570\u636e\u3002");
                }

                var wavePath = Path.Combine(Path.GetTempPath(), "WeChatMessageReaderAssistant_Baoyi_" + Guid.NewGuid().ToString("N") + ".wav");
                WritePcm16WaveFile(wavePath, pcm, _format);
                _temporaryWaveFiles.Add(wavePath);
                PlaySound(wavePath, IntPtr.Zero, SoundFilename | SoundSync);
            }
        }

        public void Stop()
        {
            lock (_pluginLock)
            {
                StopPlaybackNoLock();
            }
        }

        public void Dispose()
        {
            lock (_pluginLock)
            {
                StopPlaybackNoLock();
                CleanupTemporaryFilesNoLock();
                if (_initialized)
                {
                    try { _uninitialize(); } catch { }
                    _initialized = false;
                }

                if (_library != IntPtr.Zero)
                {
                    try { NativeLibrary.Free(_library); } catch { }
                }
            }
        }

        private T GetRequiredDelegate<T>(string name) where T : Delegate
        {
            if (!NativeLibrary.TryGetExport(_library, name, out var address) || address == IntPtr.Zero)
            {
                throw new MissingMethodException("\u4fdd\u76ca\u8bed\u97f3\u63d2\u4ef6\u7f3a\u5c11\u63a5\u53e3\uff1a" + name);
            }

            return Marshal.GetDelegateForFunctionPointer<T>(address);
        }

        private T? GetOptionalDelegate<T>(string name) where T : Delegate
        {
            return NativeLibrary.TryGetExport(_library, name, out var address) && address != IntPtr.Zero
                ? Marshal.GetDelegateForFunctionPointer<T>(address)
                : null;
        }

        private string? GetFirstVoiceName()
        {
            UIntPtr len = UIntPtr.Zero;
            if (!_getVoiceList(IntPtr.Zero, ref len) || len == UIntPtr.Zero)
            {
                return string.Empty;
            }

            var countOrChars = checked((int)len.ToUInt64());
            var charCount = Math.Max(128, countOrChars * 128);
            var bytes = charCount * 2;
            var buffer = Marshal.AllocHGlobal(bytes);
            try
            {
                Span<byte> zero = new byte[bytes];
                Marshal.Copy(zero.ToArray(), 0, buffer, bytes);
                var secondLen = (UIntPtr)charCount;
                if (!_getVoiceList(buffer, ref secondLen))
                {
                    return string.Empty;
                }

                var text = Marshal.PtrToStringUni(buffer, charCount)?.TrimEnd('\0') ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text))
                {
                    return string.Empty;
                }

                return text.Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? text;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private void OnPluginLog(int level, string? text)
        {
            // The main diagnostics page reports initialization errors. Per-chunk plugin logs are intentionally quiet.
        }

        private void OnPluginPlay(string? voiceName, IntPtr data, UIntPtr len, long index)
        {
            if (data == IntPtr.Zero || len == UIntPtr.Zero)
            {
                _speakCompleted.Set();
                return;
            }

            var length = checked((int)len.ToUInt64());
            var buffer = new byte[length];
            Marshal.Copy(data, buffer, 0, length);
            _pcmStream?.Write(buffer, 0, buffer.Length);
        }

        private static int MapUiRateToPercent(int rate)
        {
            return Math.Clamp((int)Math.Round((Math.Clamp(rate, -10, 10) + 10) * 5.0), 0, 100);
        }

        private void StopPlaybackNoLock()
        {
            try { _stop(); } catch { }
            PlaySound(null, IntPtr.Zero, 0);
            _speakCompleted.Set();
        }

        private void CleanupTemporaryFilesNoLock()
        {
            for (var i = _temporaryWaveFiles.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (File.Exists(_temporaryWaveFiles[i]))
                    {
                        File.Delete(_temporaryWaveFiles[i]);
                    }
                    _temporaryWaveFiles.RemoveAt(i);
                }
                catch
                {
                    // Best effort cleanup only.
                }
            }
        }

        [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool PlaySound(string? pszSound, IntPtr hmod, uint fdwSound);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void BoySynthLogProc(int level, [MarshalAs(UnmanagedType.LPWStr)] string? text);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void BoySynthPlayProc([MarshalAs(UnmanagedType.LPWStr)] string? voiceName, IntPtr data, UIntPtr len, long index);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool BoySynthInitializeDelegate(BoySynthLogProc proc);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool BoySynthUninitializeDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool BoySynthGetInfoDelegate(out BoySynthInfo info);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool BoySynthGetVoiceListDelegate(IntPtr voiceList, ref UIntPtr len);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool BoySynthGetAudioFormatDelegate(out BoySynthAudioFormat format);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void BoySynthSetPlayCallbackDelegate(BoySynthPlayProc proc);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private delegate bool BoySynthSpeakDelegate([MarshalAs(UnmanagedType.LPWStr)] string voiceName, [MarshalAs(UnmanagedType.LPWStr)] string text, long index);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool BoySynthStopDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private delegate bool BoySynthPrepareVoiceDelegate([MarshalAs(UnmanagedType.LPWStr)] string voiceName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private delegate bool BoySynthSetRateDelegate([MarshalAs(UnmanagedType.LPWStr)] string voiceName, int percent);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private delegate bool BoySynthSetVolumeDelegate([MarshalAs(UnmanagedType.LPWStr)] string voiceName, int percent);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BoySynthInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Name;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string Version;

        public string NameAsString => Name?.TrimEnd('\0').Trim() ?? string.Empty;
        public string VersionAsString => Version?.TrimEnd('\0').Trim() ?? string.Empty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BoySynthAudioFormat
    {
        public int NumChannel;
        public int SampleRate;
        public int BitsPerSample;
    }

    private static string ProbeBaoyiSpeechPlugin()
    {
        var path = FindCompatiblePluginPath(out var message);
        return string.IsNullOrWhiteSpace(path) ? message : "\u5df2\u627e\u5230\u53ef\u52a0\u8f7d\u63d2\u4ef6\uff1a" + path;
    }

    private static string? FindCompatiblePluginPath(out string message)
    {
        var candidates = FindBaoyiPluginCandidates().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (candidates.Count == 0)
        {
            message = "\u6ca1\u6709\u627e\u5230 BoySynth*.dll\u3002\u8bf7\u786e\u8ba4\u5df2\u5b89\u88c5\u4fdd\u76ca\u8bfb\u5c4f\u6216\u4fdd\u76ca\u8bed\u97f3\u63d2\u4ef6\u3002";
            return null;
        }

        foreach (var path in candidates)
        {
            var machine = GetPeMachine(path);
            if (Environment.Is64BitProcess && machine == 0x8664)
            {
                message = "\u5df2\u627e\u5230 64 \u4f4d\u4fdd\u76ca\u8bed\u97f3\u63d2\u4ef6\u3002";
                return path;
            }

            if (!Environment.Is64BitProcess && machine == 0x014c)
            {
                message = "\u5df2\u627e\u5230 32 \u4f4d\u4fdd\u76ca\u8bed\u97f3\u63d2\u4ef6\u3002";
                return path;
            }
        }

        var found = string.Join("\uff1b", candidates.Select(p => p + "\uff08" + DescribePeMachine(GetPeMachine(p)) + "\uff09"));
        message = Environment.Is64BitProcess
            ? "\u627e\u5230\u4fdd\u76ca\u8bed\u97f3\u63d2\u4ef6\uff0c\u4f46\u5f53\u524d\u52a9\u624b\u662f 64 \u4f4d\uff0c\u4e0d\u80fd\u52a0\u8f7d 32 \u4f4d\u63d2\u4ef6\u3002\u5df2\u627e\u5230\uff1a" + found + "\u3002\u5982\u679c\u4fdd\u76ca\u63d0\u4f9b 64 \u4f4d BoySynth \u63d2\u4ef6\uff0c\u653e\u5230\u7a0b\u5e8f\u76ee\u5f55\u6216\u4fdd\u76ca\u63d2\u4ef6\u76ee\u5f55\u540e\u5373\u53ef\u4f7f\u7528\uff1b\u5f53\u524d\u4f1a\u81ea\u52a8\u56de\u9000 Windows SAPI\u3002"
            : "\u627e\u5230\u4fdd\u76ca\u8bed\u97f3\u63d2\u4ef6\uff0c\u4f46\u5f53\u524d\u52a9\u624b\u662f 32 \u4f4d\uff0c\u4e0d\u80fd\u52a0\u8f7d 64 \u4f4d\u63d2\u4ef6\u3002\u5df2\u627e\u5230\uff1a" + found + "\u3002\u5f53\u524d\u4f1a\u81ea\u52a8\u56de\u9000 Windows SAPI\u3002";
        return null;
    }

    private static IEnumerable<string> FindBaoyiPluginCandidates()
    {
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "BoySynth*.dll", SearchOption.TopDirectoryOnly))
        {
            yield return file;
        }

        foreach (var root in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "BoyPcReader", "plugins"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BoyPcReader", "plugins")
        })
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(root, "BoySynth*.dll", SearchOption.AllDirectories))
            {
                yield return file;
            }
        }
    }

    private static ushort GetPeMachine(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> buffer = stackalloc byte[4];
            stream.Position = 0x3c;
            if (stream.Read(buffer) != 4)
            {
                return 0;
            }

            var peOffset = BitConverter.ToInt32(buffer);
            stream.Position = peOffset + 4;
            if (stream.Read(buffer[..2]) != 2)
            {
                return 0;
            }

            return BitConverter.ToUInt16(buffer[..2]);
        }
        catch
        {
            return 0;
        }
    }

    private static string DescribePeMachine(ushort machine)
    {
        return machine switch
        {
            0x014c => "32 ?",
            0x8664 => "64 ?",
            _ => "\u672a\u77e5\u4f4d\u6570"
        };
    }

    private static void WritePcm16WaveFile(string path, byte[] pcm, BoySynthAudioFormat format)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        var blockAlign = format.NumChannel * format.BitsPerSample / 8;
        var byteRate = format.SampleRate * blockAlign;
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + pcm.Length);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)format.NumChannel);
        writer.Write(format.SampleRate);
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write((short)format.BitsPerSample);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write(pcm.Length);
        writer.Write(pcm);
    }

    private sealed class SapiSpeechEngine : ISpeechEngine
    {
        private const double MaxAmplificationFactor = 1.5;
        private const uint SoundAsync = 0x0001;
        private const uint SoundFilename = 0x00020000;

        private readonly object _sapiLock = new();
        private readonly List<string> _temporaryWaveFiles = new();
        private dynamic? _voice;
        private string? _currentWaveFile;

        public SapiSpeechEngine()
        {
            var type = Type.GetTypeFromProgID("SAPI.SpVoice");
            if (type == null)
            {
                throw new InvalidOperationException("SAPI.SpVoice was not found.");
            }

            _voice = Activator.CreateInstance(type);
        }

        public string? RequestedBackendKey { get; set; }
        public string? LastPrismFailure { get; set; }

        public string EngineName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(RequestedBackendKey) && !string.Equals(RequestedBackendKey, "SAPI", StringComparison.OrdinalIgnoreCase))
                {
                    return $"Windows SAPI enhanced volume";
                }

                return "Windows SAPI enhanced volume";
            }
        }

        public string ChannelDescription
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(RequestedBackendKey)
                    && !string.Equals(RequestedBackendKey, "SAPI", StringComparison.OrdinalIgnoreCase))
                {
                    return "Windows SAPI 本地语音库（由 " + GetBackendDisplayName(RequestedBackendKey) + " 回退而来）";
                }

                return "Windows SAPI 本地语音库";
            }
        }

        public void Speak(string text, int rate, int volume)
        {
            lock (_sapiLock)
            {
                if (_voice == null)
                {
                    throw new ObjectDisposedException(nameof(SapiSpeechEngine));
                }

                StopPlaybackNoLock();
                CleanupTemporaryFilesNoLock(deleteCurrent: false);

                // When the UI volume is high, direct SAPI is already limited to 100.
                // Generate a temporary WAV file and apply a gentle 1.5x limiter so the
                // assistant can be louder without obvious clipping. If anything fails,
                // fall back to the original direct SAPI path.
                if (volume >= 80 && TrySpeakWithAmplifiedWave(text, rate, volume))
                {
                    return;
                }

                SpeakDirectNoLock(text, rate, volume);
            }
        }

        public void Stop()
        {
            lock (_sapiLock)
            {
                StopPlaybackNoLock();
                _voice?.Speak(string.Empty, 2);
                CleanupTemporaryFilesNoLock(deleteCurrent: true);
            }
        }

        public void Dispose()
        {
            lock (_sapiLock)
            {
                StopPlaybackNoLock();
                CleanupTemporaryFilesNoLock(deleteCurrent: true);
                _voice = null;
            }
        }

        private bool TrySpeakWithAmplifiedWave(string text, int rate, int volume)
        {
            var wavePath = Path.Combine(Path.GetTempPath(), "WeChatMessageReaderAssistant_" + Guid.NewGuid().ToString("N") + ".wav");

            try
            {
                SaveSpeechToWaveNoLock(text, rate, volume, wavePath);
                AmplifyPcm16WaveInPlace(wavePath, GetAmplificationFactor(volume));

                _currentWaveFile = wavePath;
                _temporaryWaveFiles.Add(wavePath);

                if (!PlaySound(wavePath, IntPtr.Zero, SoundAsync | SoundFilename))
                {
                    return false;
                }

                return true;
            }
            catch
            {
                TryDeleteFile(wavePath);
                return false;
            }
        }

        private void SaveSpeechToWaveNoLock(string text, int rate, int volume, string wavePath)
        {
            var streamType = Type.GetTypeFromProgID("SAPI.SpFileStream");
            if (streamType == null)
            {
                throw new InvalidOperationException("SAPI.SpFileStream was not found.");
            }

            dynamic? fileStream = null;
            try
            {
                fileStream = Activator.CreateInstance(streamType);
                if (fileStream == null)
                {
                    throw new InvalidOperationException("SAPI.SpFileStream could not be created.");
                }

                fileStream.Open(wavePath, 3, false); // SSFMCreateForWrite
                _voice!.AudioOutputStream = fileStream;
                _voice.Rate = Math.Clamp(rate, -10, 10);
                _voice.Volume = Math.Clamp(volume, 0, 100);
                _voice.Speak(text, 0); // synchronous write to file
            }
            finally
            {
                try
                {
                    if (_voice != null)
                    {
                        _voice.AudioOutputStream = null;
                    }
                }
                catch
                {
                    // Best effort restore only.
                }

                try
                {
                    fileStream?.Close();
                }
                catch
                {
                    // Best effort cleanup only.
                }
            }
        }

        private void SpeakDirectNoLock(string text, int rate, int volume)
        {
            _voice!.Rate = Math.Clamp(rate, -10, 10);
            _voice.Volume = Math.Clamp((int)Math.Round(volume * 1.5), 0, 100);
            _voice.Speak(string.Empty, 2);
            _voice.Speak(text, 1);
        }

        private static double GetAmplificationFactor(int volume)
        {
            var normalized = Math.Clamp(volume, 0, 100) / 100.0;
            return 1.0 + ((MaxAmplificationFactor - 1.0) * normalized);
        }

        private static void AmplifyPcm16WaveInPlace(string wavePath, double factor)
        {
            var bytes = File.ReadAllBytes(wavePath);
            if (bytes.Length < 44 || bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F')
            {
                return;
            }

            var fmtOffset = FindChunk(bytes, "fmt ");
            var dataOffset = FindChunk(bytes, "data");
            if (fmtOffset < 0 || dataOffset < 0)
            {
                return;
            }

            var fmtSize = BitConverter.ToInt32(bytes, fmtOffset + 4);
            if (fmtSize < 16)
            {
                return;
            }

            var audioFormat = BitConverter.ToInt16(bytes, fmtOffset + 8);
            var bitsPerSample = BitConverter.ToInt16(bytes, fmtOffset + 22);
            if (audioFormat != 1 || bitsPerSample != 16)
            {
                return;
            }

            var dataSize = BitConverter.ToInt32(bytes, dataOffset + 4);
            var sampleStart = dataOffset + 8;
            var sampleEnd = Math.Min(bytes.Length, sampleStart + dataSize);
            for (var i = sampleStart; i + 1 < sampleEnd; i += 2)
            {
                var sample = BitConverter.ToInt16(bytes, i);
                var amplified = ApplySoftLimiter(sample / 32768.0, factor);
                var output = (short)Math.Clamp((int)Math.Round(amplified * 32767.0), short.MinValue, short.MaxValue);
                bytes[i] = (byte)(output & 0xff);
                bytes[i + 1] = (byte)((output >> 8) & 0xff);
            }

            File.WriteAllBytes(wavePath, bytes);
        }

        private static double ApplySoftLimiter(double sample, double factor)
        {
            var value = sample * factor;
            var sign = Math.Sign(value);
            var absolute = Math.Abs(value);
            const double threshold = 0.88;
            if (absolute <= threshold)
            {
                return value;
            }

            var compressed = threshold + ((1.0 - threshold) * (1.0 - Math.Exp(-(absolute - threshold) / (1.0 - threshold))));
            return sign * Math.Min(compressed, 0.98);
        }

        private static int FindChunk(byte[] bytes, string chunkId)
        {
            var id = System.Text.Encoding.ASCII.GetBytes(chunkId);
            for (var i = 12; i + 8 <= bytes.Length;)
            {
                if (bytes[i] == id[0] && bytes[i + 1] == id[1] && bytes[i + 2] == id[2] && bytes[i + 3] == id[3])
                {
                    return i;
                }

                var size = BitConverter.ToInt32(bytes, i + 4);
                i += 8 + Math.Max(size, 0);
                if ((i & 1) == 1)
                {
                    i++;
                }
            }

            return -1;
        }

        private void StopPlaybackNoLock()
        {
            PlaySound(null, IntPtr.Zero, 0);
            _currentWaveFile = null;
        }

        private void CleanupTemporaryFilesNoLock(bool deleteCurrent)
        {
            for (var i = _temporaryWaveFiles.Count - 1; i >= 0; i--)
            {
                var file = _temporaryWaveFiles[i];
                if (!deleteCurrent && string.Equals(file, _currentWaveFile, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (TryDeleteFile(file))
                {
                    _temporaryWaveFiles.RemoveAt(i);
                }
            }
        }

        private static bool TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool PlaySound(string? pszSound, IntPtr hmod, uint fdwSound);
    }


    private static partial class ZdsrOfficialNative
    {
        private const string DllName = "ZDSRAPI_x64.dll";

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        internal static extern int InitTTS(int type, [MarshalAs(UnmanagedType.LPWStr)] string channelName, [MarshalAs(UnmanagedType.Bool)] bool bKeyDownInterrupt);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        internal static extern int Speak([MarshalAs(UnmanagedType.LPWStr)] string text, [MarshalAs(UnmanagedType.Bool)] bool bInterrupt);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        internal static extern int GetSpeakState();

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        internal static extern void StopSpeak();
    }

    private static partial class TolkNative
    {
        private const string DllName = "tolk.dll";

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool Tolk_Load();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Tolk_Unload();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool Tolk_IsLoaded();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool Tolk_HasSpeech();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr Tolk_DetectScreenReader();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Tolk_TrySAPI([MarshalAs(UnmanagedType.I1)] bool trySapi);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Tolk_PreferSAPI([MarshalAs(UnmanagedType.I1)] bool preferSapi);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool Tolk_Output([MarshalAs(UnmanagedType.LPWStr)] string text, [MarshalAs(UnmanagedType.I1)] bool interrupt);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool Tolk_Speak([MarshalAs(UnmanagedType.LPWStr)] string text, [MarshalAs(UnmanagedType.I1)] bool interrupt);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Tolk_Silence();
    }

    private enum PrismError
    {
        PRISM_OK = 0,
        PRISM_ERROR_NOT_INITIALIZED,
        PRISM_ERROR_INVALID_PARAM,
        PRISM_ERROR_NOT_IMPLEMENTED,
        PRISM_ERROR_NO_VOICES,
        PRISM_ERROR_VOICE_NOT_FOUND,
        PRISM_ERROR_SPEAK_FAILURE,
        PRISM_ERROR_MEMORY_FAILURE,
        PRISM_ERROR_RANGE_OUT_OF_BOUNDS,
        PRISM_ERROR_INTERNAL,
        PRISM_ERROR_NOT_SPEAKING,
        PRISM_ERROR_NOT_PAUSED,
        PRISM_ERROR_ALREADY_PAUSED,
        PRISM_ERROR_INVALID_UTF8,
        PRISM_ERROR_INVALID_OPERATION,
        PRISM_ERROR_ALREADY_INITIALIZED,
        PRISM_ERROR_BACKEND_NOT_AVAILABLE,
        PRISM_ERROR_UNKNOWN,
        PRISM_ERROR_INVALID_AUDIO_FORMAT,
        PRISM_ERROR_INTERNAL_BACKEND_LIMIT_EXCEEDED,
        PRISM_ERROR_BACKEND_ENTERED_UNDEFINED_STATE,
        PRISM_ERROR_COUNT
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PrismConfig
    {
        public byte Version;
    }

    private static partial class PrismNative
    {
        private const string DllName = "prism.dll";

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern PrismConfig prism_config_init();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr prism_init(ref PrismConfig config);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void prism_shutdown(IntPtr context);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr prism_registry_acquire_best(IntPtr context);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr prism_registry_acquire(IntPtr context, ulong backendId);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void prism_backend_free(IntPtr backend);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr prism_backend_name(IntPtr backend);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern ulong prism_backend_get_features(IntPtr backend);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern PrismError prism_backend_set_volume(IntPtr backend, float volume);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern PrismError prism_backend_set_rate(IntPtr backend, float rate);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern PrismError prism_backend_stop(IntPtr backend);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern PrismError prism_backend_speak(
            IntPtr backend,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string text,
            [MarshalAs(UnmanagedType.I1)] bool interrupt);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr prism_error_string(PrismError error);
    }
}







