using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SharpHook;
using SharpHook.Data;
using ZSnaper.Interop;
using ZSnaper.Models;

namespace ZSnaper.Services;

public class HotkeyService : NativeWindow, IDisposable
{
    private sealed class HotkeyState(HotkeyCommand command, int registrationId)
    {
        public HotkeyCommand Command { get; } = command;
        public HotkeyGesture? Gesture { get; set; }
        public bool Registered { get; set; }
        public bool ForceBinding { get; set; }
        public int RegistrationError { get; set; }
        public int RegistrationId { get; set; } = registrationId;
        public long SuppressUntil { get; set; }
        public long LastTriggeredAt { get; set; } = long.MinValue;
    }

    private readonly record struct HotkeySnapshot(
        HotkeyGesture? Gesture,
        bool Registered,
        bool ForceBinding,
        int RegistrationId);

    public const int HOTKEY_CAPTURE = 1;
    public const int HOTKEY_OCR = 2;
    private const int WM_APP_FORCE_TRIGGER = 0x8003;
    private const int WM_APP_RECOVER = 0x8004;
    private bool _disposed;

    public event Action? CaptureTriggered;
    public event Action? OcrTriggered;
    public event Action<HotkeyCommand>? CommandTriggered;
    public event Action? StatusChanged;

    private readonly Dictionary<HotkeyCommand, HotkeyState> _states;
    private int _nextRegistrationId = 100;
    private SimpleGlobalHook? _keyboardHook;
    private EventHandler<KeyboardHookEventArgs>? _captureKeyPressed;
    private EventHandler<KeyboardHookEventArgs>? _captureKeyReleased;
    private readonly HashSet<KeyCode> _pressedKeys = [];
    private readonly HashSet<KeyCode> _suppressedKeys = [];
    private HotkeyCommand? _recordingCommand;
    private Dictionary<HotkeyCommand, HotkeySnapshot>? _recordingSnapshots;
    private readonly record struct HookBinding(HotkeyCommand Command, HotkeyGesture Gesture, bool Intercept);
    private HookBinding[] _hookBindings = [];

    public bool IsCaptureRegistered => GetState(HotkeyCommand.Capture).Registered;
    public bool IsOcrRegistered => GetState(HotkeyCommand.Ocr).Registered;
    public bool IsCaptureForceBinding => GetState(HotkeyCommand.Capture).ForceBinding;
    public bool IsOcrForceBinding => GetState(HotkeyCommand.Ocr).ForceBinding;
    public HotkeyGesture CaptureGesture => GetGesture(HotkeyCommand.Capture) ?? new HotkeyGesture(Keys.Q, Keys.Alt);
    public HotkeyGesture OcrGesture => GetGesture(HotkeyCommand.Ocr) ?? new HotkeyGesture(Keys.X, Keys.Alt);

    public HotkeyService()
    {
        _states = HotkeyCommandCatalog.Definitions
            .Select((definition, index) => new HotkeyState(definition.Command, index + 1))
            .ToDictionary(state => state.Command);
        CreateHandle(new CreateParams());
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public HotkeyGesture? GetGesture(HotkeyCommand command) => GetState(command).Gesture;
    public bool IsForceBinding(HotkeyCommand command) => GetState(command).ForceBinding;

    public string GetStatusText(HotkeyCommand command)
    {
        HotkeyState state = GetState(command);
        if (state.Gesture is null) return "未设置";
        if (_recordingCommand is not null) return "录制期间暂停";
        bool hook = _keyboardHook is { IsRunning: true } && _hookBindings.Any(binding => binding.Command == command);
        if (state.ForceBinding) return hook ? "拦截绑定已启用" : "键盘监听不可用";
        if (state.Registered) return hook ? "已注册 · 游戏兼容监听已启用" : "已注册 · 兼容监听不可用";
        return state.RegistrationError == NativeMethods.ERROR_HOTKEY_ALREADY_REGISTERED
            ? "被其他程序占用，点击更换" : $"注册失败（{state.RegistrationError}），点击更换";
    }

    public void RegisterConfiguredHotkeys(out bool captureOk, out bool ocrOk)
    {
        foreach (HotkeyState registered in _states.Values.Where(state => state.Registered))
            NativeMethods.UnregisterHotKey(Handle, registered.RegistrationId);
        foreach (HotkeyCommandDefinition definition in HotkeyCommandCatalog.Definitions)
        {
            HotkeyCommand command = definition.Command;
            HotkeyState state = GetState(command);
            bool forceBinding = HotkeyCommandCatalog.GetForceBinding(ConfigService.Current, command);
            string configured = HotkeyCommandCatalog.GetConfigText(ConfigService.Current, command);
            state.Gesture = HotkeyGesture.TryParse(configured, out HotkeyGesture gesture, forceBinding)
                ? gesture
                : null;
            state.ForceBinding = state.Gesture is not null && forceBinding;
            state.Registered = false;
            ActivateConfiguredHotkey(command);
        }

        captureOk = IsActive(HotkeyCommand.Capture);
        ocrOk = IsActive(HotkeyCommand.Ocr);
        RefreshHookBindings();
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon)
            QueueRecovery();
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) QueueRecovery();
    }

    private void QueueRecovery()
    {
        if (!_disposed) NativeMethods.PostMessage(Handle, WM_APP_RECOVER, 0, 0);
    }

    internal void RecoverConfiguredHotkeys()
    {
        if (_disposed || _recordingCommand is not null) return;
        DisposeKeyboardHook();
        foreach (HotkeyState state in _states.Values)
        {
            if (state.Gesture is null || state.ForceBinding) continue;
            if (state.Registered) NativeMethods.UnregisterHotKey(Handle, state.RegistrationId);
            state.Registered = Register(state.RegistrationId, state.Gesture.Value);
            state.RegistrationError = state.Registered ? 0 : Marshal.GetLastPInvokeError();
            state.SuppressUntil = Environment.TickCount64 + 150;
            if (!state.Registered)
                AppDiagnostics.LogMessage("Hotkey.Recover", $"{state.Command}: registration failed ({Marshal.GetLastPInvokeError()})",
                    Serilog.Events.LogEventLevel.Warning);
        }
        RefreshHookBindings();
        if (_captureKeyPressed is not null) EnsureKeyboardHook(out _);
    }

    public IReadOnlyList<HotkeyCommand> GetInactiveConfiguredCommands() =>
        _states.Values
            .Where(state => state.Gesture is not null && !IsActive(state.Command))
            .Select(state => state.Command)
            .ToList();

    public HotkeyChangeResult TryUpdateHotkey(
        HotkeyCommand command,
        HotkeyGesture gesture,
        HotkeyBindingMode mode = HotkeyBindingMode.Standard)
    {
        HotkeyState state = GetState(command);
        bool forceBinding = mode == HotkeyBindingMode.Intercept;
        bool gestureIsValid = forceBinding ? gesture.IsValidForForceBinding : gesture.IsValid;
        if (!gestureIsValid)
        {
            return CreateInvalidGestureResult(gesture, forceBinding);
        }

        if (_states.Values.Any(other => other.Command != command && other.Gesture == gesture))
        {
            return new HotkeyChangeResult(false, "该组合键已用于另一项功能", HotkeyChangeFailure.Duplicate);
        }

        HotkeySnapshot previousState = Snapshot(state);
        if (state.Gesture == gesture && state.ForceBinding == forceBinding)
        {
            if (!IsActive(command) && !ActivateConfiguredHotkey(command))
                return CreateRegistrationFailureResult(Marshal.GetLastPInvokeError(), gesture);
            RefreshHookBindings();
            return new HotkeyChangeResult(true, $"当前快捷键已是 {gesture.DisplayText}");
        }

        if (forceBinding)
        {
            if (!EnsureKeyboardHook(out int hookError))
            {
                return hookError == NativeMethods.ERROR_ACCESS_DENIED
                    ? RequestElevatedRestart(command, gesture)
                    : new HotkeyChangeResult(false, "这个按键暂时无法绑定，请稍后重试", HotkeyChangeFailure.HookUnavailable);
            }

            if (state.Registered && !NativeMethods.UnregisterHotKey(Handle, state.RegistrationId))
            {
                ReleaseKeyboardHookIfUnused();
                return new HotkeyChangeResult(false, "无法释放原快捷键，旧快捷键仍保持不变", HotkeyChangeFailure.Registration);
            }

            SetHotkeyState(state, gesture, registered: false, forceBinding: true, state.RegistrationId);
            if (!ConfigService.Save())
            {
                return CreatePersistenceFailure(RollbackHotkey(state, previousState));
            }

            return new HotkeyChangeResult(true, $"已绑定 {gesture.DisplayText}");
        }

        int candidateId = AllocateRegistrationId();
        if (!Register(candidateId, gesture))
        {
            return CreateRegistrationFailureResult(Marshal.GetLastPInvokeError(), gesture);
        }

        if (state.Registered && !NativeMethods.UnregisterHotKey(Handle, state.RegistrationId))
        {
            NativeMethods.UnregisterHotKey(Handle, candidateId);
            return new HotkeyChangeResult(false, "无法释放原快捷键，旧快捷键仍保持不变", HotkeyChangeFailure.Registration);
        }

        SetHotkeyState(state, gesture, registered: true, forceBinding: false, candidateId);
        if (!ConfigService.Save())
        {
            return CreatePersistenceFailure(RollbackHotkey(state, previousState));
        }

        return new HotkeyChangeResult(true, $"已更新为 {gesture.DisplayText}");
    }

    public HotkeyChangeResult TryClearHotkey(HotkeyCommand command)
    {
        HotkeyState state = GetState(command);
        if (state.Gesture is null)
        {
            return new HotkeyChangeResult(true, "该功能未设置快捷键");
        }

        HotkeySnapshot previousState = Snapshot(state);
        if (state.Registered && !NativeMethods.UnregisterHotKey(Handle, state.RegistrationId))
        {
            return new HotkeyChangeResult(false, "无法释放原快捷键，旧快捷键仍保持不变", HotkeyChangeFailure.Registration);
        }

        state.Gesture = null;
        state.Registered = false;
        state.ForceBinding = false;
        state.SuppressUntil = 0;
        HotkeyCommandCatalog.SetConfig(ConfigService.Current, state.Command, string.Empty, forceBinding: false);
        ReleaseKeyboardHookIfUnused();

        if (!ConfigService.Save())
        {
            return CreatePersistenceFailure(RollbackHotkey(state, previousState));
        }

        return new HotkeyChangeResult(true, "已清除快捷键");
    }

    public HotkeyChangeResult BeginRecording(HotkeyCommand command)
    {
        if (_recordingCommand is not null)
        {
            return new HotkeyChangeResult(false, "请先完成或取消另一个快捷键的录制", HotkeyChangeFailure.Busy);
        }

        _recordingSnapshots = _states.ToDictionary(pair => pair.Key, pair => Snapshot(pair.Value));
        if (!SuspendRegisteredHotkeys())
        {
            _recordingSnapshots = null;
            return new HotkeyChangeResult(false, "无法暂停当前快捷键，录制没有开始", HotkeyChangeFailure.Registration);
        }

        _recordingCommand = command;
        RefreshHookBindings();
        return new HotkeyChangeResult(true, string.Empty);
    }

    public HotkeyChangeResult EndRecording()
    {
        if (_recordingCommand is null)
        {
            return new HotkeyChangeResult(true, string.Empty);
        }

        _recordingCommand = null;
        List<string> errors = [];
        if (_recordingSnapshots is not null)
        {
            foreach ((HotkeyCommand command, HotkeySnapshot snapshot) in _recordingSnapshots)
            {
                RestoreSuspendedHotkey(GetState(command), snapshot, errors);
            }
        }

        _recordingSnapshots = null;
        ReleaseKeyboardHookIfUnused();
        return errors.Count == 0
            ? new HotkeyChangeResult(true, string.Empty)
            : new HotkeyChangeResult(false, "另一个快捷键恢复失败，请重新启动应用", HotkeyChangeFailure.Registration);
    }

    private HotkeyState GetState(HotkeyCommand command) =>
        _states.TryGetValue(command, out HotkeyState? state)
            ? state
            : throw new ArgumentOutOfRangeException(nameof(command));

    private bool IsActive(HotkeyCommand command)
    {
        HotkeyState state = GetState(command);
        return state.Gesture is null || state.Registered || (state.ForceBinding && _keyboardHook is { IsRunning: true });
    }

    private bool Register(int id, HotkeyGesture gesture) => NativeMethods.RegisterHotKey(
        Handle,
        id,
        ToNativeModifiers(gesture.Modifiers) | NativeMethods.MOD_NOREPEAT,
        (uint)(gesture.KeyCode & Keys.KeyCode));

    private static uint ToNativeModifiers(Keys modifiers)
    {
        uint nativeModifiers = NativeMethods.MOD_NONE;
        if (modifiers.HasFlag(Keys.Alt)) nativeModifiers |= NativeMethods.MOD_ALT;
        if (modifiers.HasFlag(Keys.Control)) nativeModifiers |= NativeMethods.MOD_CONTROL;
        if (modifiers.HasFlag(Keys.Shift)) nativeModifiers |= NativeMethods.MOD_SHIFT;
        return nativeModifiers;
    }

    private bool ActivateConfiguredHotkey(HotkeyCommand command)
    {
        HotkeyState state = GetState(command);
        if (state.Gesture is not HotkeyGesture gesture) return true;
        if (state.ForceBinding) return EnsureKeyboardHook(out _);
        state.Registered = Register(state.RegistrationId, gesture);
        state.RegistrationError = state.Registered ? 0 : Marshal.GetLastPInvokeError();
        state.SuppressUntil = Environment.TickCount64 + 750;
        return state.Registered;
    }

    private bool SuspendRegisteredHotkeys()
    {
        List<HotkeyState> suspended = [];
        foreach (HotkeyState state in _states.Values.Where(candidate => candidate.Registered))
        {
            if (!NativeMethods.UnregisterHotKey(Handle, state.RegistrationId))
            {
                foreach (HotkeyState previous in suspended)
                {
                    previous.Registered = previous.Gesture is HotkeyGesture gesture && Register(previous.RegistrationId, gesture);
                }
                return false;
            }

            state.Registered = false;
            suspended.Add(state);
        }
        return true;
    }

    private void RestoreSuspendedHotkey(HotkeyState state, HotkeySnapshot snapshot, List<string> errors)
    {
        if (!snapshot.Registered || state.ForceBinding || state.Registered || state.Gesture is not HotkeyGesture gesture) return;
        state.RegistrationId = snapshot.RegistrationId;
        state.Registered = Register(state.RegistrationId, gesture);
        if (!state.Registered) errors.Add(state.Command.ToString());
    }

    private void SetHotkeyState(HotkeyState state, HotkeyGesture gesture, bool registered, bool forceBinding, int id)
    {
        state.Gesture = gesture;
        state.Registered = registered;
        state.ForceBinding = forceBinding;
        state.RegistrationId = id;
        state.SuppressUntil = Environment.TickCount64 + 750;
        HotkeyCommandCatalog.SetConfig(ConfigService.Current, state.Command, gesture.ConfigText, forceBinding);
        ReleaseKeyboardHookIfUnused();
    }

    private bool RollbackHotkey(HotkeyState state, HotkeySnapshot previous)
    {
        if (state.Registered && !NativeMethods.UnregisterHotKey(Handle, state.RegistrationId)) return false;
        state.Gesture = previous.Gesture;
        state.Registered = false;
        state.ForceBinding = previous.ForceBinding;
        state.RegistrationId = previous.RegistrationId;
        state.SuppressUntil = Environment.TickCount64 + 750;
        HotkeyCommandCatalog.SetConfig(
            ConfigService.Current,
            state.Command,
            previous.Gesture?.ConfigText ?? string.Empty,
            previous.ForceBinding && previous.Gesture is not null);

        if (previous.Gesture is null)
        {
            ReleaseKeyboardHookIfUnused();
            return true;
        }
        if (previous.ForceBinding)
        {
            RefreshHookBindings();
            return EnsureKeyboardHook(out _);
        }
        if (!previous.Registered)
        {
            ReleaseKeyboardHookIfUnused();
            return true;
        }

        state.Registered = Register(previous.RegistrationId, previous.Gesture.Value);
        ReleaseKeyboardHookIfUnused();
        return state.Registered;
    }

    private static HotkeySnapshot Snapshot(HotkeyState state) =>
        new(state.Gesture, state.Registered, state.ForceBinding, state.RegistrationId);

    private int AllocateRegistrationId() => _nextRegistrationId++;

    private static HotkeyChangeResult CreatePersistenceFailure(bool rolledBack) => new(
        false,
        rolledBack ? "配置保存失败，已恢复原快捷键；请检查配置文件权限" : "配置保存失败且原快捷键恢复失败，请重新启动应用",
        HotkeyChangeFailure.Persistence);

    private static HotkeyChangeResult CreateRegistrationFailureResult(int errorCode, HotkeyGesture gesture)
    {
        if (errorCode == NativeMethods.ERROR_HOTKEY_ALREADY_REGISTERED)
            return new HotkeyChangeResult(false, $"{gesture.DisplayText} 正被其他程序使用", HotkeyChangeFailure.Occupied);
        return new HotkeyChangeResult(false, $"快捷键注册失败（错误码 {errorCode}），原快捷键保持不变", HotkeyChangeFailure.Registration);
    }

    private void ReleaseKeyboardHookIfUnused()
    {
        RefreshHookBindings();
        if (_keyboardHook is null || _captureKeyPressed is not null ||
            _states.Values.Any(state => state.Gesture is not null)) return;
        DisposeKeyboardHook();
    }

    private void RefreshHookBindings()
    {
        Volatile.Write(ref _hookBindings, _recordingCommand is not null ? [] :
            _states.Values.Where(state => state.Gesture is not null && (state.Registered || state.ForceBinding))
                .Select(state => new HookBinding(state.Command, state.Gesture!.Value, state.ForceBinding)).ToArray());
        // Native registration remains the primary path; the hook is a pass-through
        // fallback for applications that consume ordinary hotkey messages.
        if (_hookBindings.Length > 0) EnsureKeyboardHook(out _);
        StatusChanged?.Invoke();
    }

    public bool BeginCaptureKeyboardRouting(
        EventHandler<KeyboardHookEventArgs> onPressed,
        EventHandler<KeyboardHookEventArgs> onReleased)
    {
        if (!EnsureKeyboardHook(out int errorCode))
        {
            AppDiagnostics.LogException("HotkeyService.CaptureKeyboardRouting",
                new InvalidOperationException($"无法启动截图键盘监听（错误码 {errorCode}）"));
            return false;
        }
        Volatile.Write(ref _captureKeyReleased, onReleased);
        Volatile.Write(ref _captureKeyPressed, onPressed);
        return true;
    }

    public void EndCaptureKeyboardRouting()
    {
        Volatile.Write(ref _captureKeyPressed, null);
        Volatile.Write(ref _captureKeyReleased, null);
        ReleaseKeyboardHookIfUnused();
    }

    private bool EnsureKeyboardHook(out int errorCode)
    {
        if (_keyboardHook is { IsRunning: true })
        {
            errorCode = 0;
            return true;
        }

        DisposeKeyboardHook();
        SimpleGlobalHook hook = new(GlobalHookType.Keyboard, runAsyncOnBackgroundThread: true);
        hook.KeyPressed += OnKeyboardKeyPressed;
        hook.KeyReleased += OnKeyboardKeyReleased;
        using ManualResetEventSlim hookStarted = new(false);
        EventHandler<HookEventArgs> onHookEnabled = (_, _) => hookStarted.Set();
        hook.HookEnabled += onHookEnabled;
        try
        {
            Task hookTask = hook.RunAsync();
            if (!hookStarted.Wait(TimeSpan.FromSeconds(2)) || hookTask.IsFaulted)
            {
                Exception failure = hookTask.Exception?.GetBaseException() ?? new InvalidOperationException("SharpHook 未能启动全局键盘 Hook");
                errorCode = GetKeyboardHookErrorCode(failure);
                DisposeHook(hook);
                return false;
            }
            _keyboardHook = hook;
            errorCode = 0;
            return true;
        }
        catch (Exception exception)
        {
            errorCode = GetKeyboardHookErrorCode(exception);
            DisposeHook(hook);
            return false;
        }
        finally
        {
            hook.HookEnabled -= onHookEnabled;
        }
    }

    private void DisposeKeyboardHook()
    {
        SimpleGlobalHook? hook = _keyboardHook;
        _keyboardHook = null;
        if (hook is not null) DisposeHook(hook);
        _pressedKeys.Clear();
        _suppressedKeys.Clear();
    }

    private static void DisposeHook(SimpleGlobalHook hook)
    {
        try { if (hook.IsRunning) hook.Stop(); } catch { }
        try { hook.Dispose(); } catch { }
    }

    private static int GetKeyboardHookErrorCode(Exception exception) =>
        exception is UnauthorizedAccessException ||
        exception is Win32Exception { NativeErrorCode: NativeMethods.ERROR_ACCESS_DENIED } ||
        exception.Message.Contains("access denied", StringComparison.OrdinalIgnoreCase)
            ? NativeMethods.ERROR_ACCESS_DENIED : 0;

    private void OnKeyboardKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        if (e.IsEventSimulated || e.Data.KeyCode == KeyCode.VcUndefined) return;
        KeyCode keyCode = e.Data.KeyCode;
        bool wasSuppressed = _suppressedKeys.Contains(keyCode);
        bool firstPress = _pressedKeys.Add(keyCode);
        if (wasSuppressed)
        {
            e.SuppressEvent = true;
            return;
        }
        EventHandler<KeyboardHookEventArgs>? captureHandler = Volatile.Read(ref _captureKeyPressed);
        if (captureHandler is not null)
        {
            captureHandler(this, e);
            if (e.SuppressEvent) _suppressedKeys.Add(keyCode);
            return;
        }
        if (!firstPress) return;
        HotkeyGesture gesture = CreateGestureFromPressedKeys(keyCode);
        foreach (HookBinding binding in Volatile.Read(ref _hookBindings))
        {
            if (binding.Gesture != gesture) continue;
            if (binding.Intercept)
            {
                _suppressedKeys.Add(keyCode);
                e.SuppressEvent = true;
            }
            NativeMethods.PostMessage(Handle, WM_APP_FORCE_TRIGGER, (nint)binding.Command,
                (nint)(gesture.KeyCode | gesture.Modifiers));
            break;
        }
    }

    private void OnKeyboardKeyReleased(object? sender, KeyboardHookEventArgs e)
    {
        if (e.IsEventSimulated || e.Data.KeyCode == KeyCode.VcUndefined) return;
        KeyCode keyCode = e.Data.KeyCode;
        bool suppress = _suppressedKeys.Remove(keyCode);
        _pressedKeys.Remove(keyCode);
        if (suppress) e.SuppressEvent = true;
        Volatile.Read(ref _captureKeyReleased)?.Invoke(this, e);
    }

    private HotkeyGesture CreateGestureFromPressedKeys(KeyCode keyCode)
    {
        Keys modifiers = Keys.None;
        // Read current modifier state instead of trusting key-up events across
        // lock/unlock, hook restarts and foreground changes.
        if ((NativeMethods.GetAsyncKeyState((int)Keys.ControlKey) & 0x8000) != 0) modifiers |= Keys.Control;
        if ((NativeMethods.GetAsyncKeyState((int)Keys.Menu) & 0x8000) != 0) modifiers |= Keys.Alt;
        if ((NativeMethods.GetAsyncKeyState((int)Keys.ShiftKey) & 0x8000) != 0) modifiers |= Keys.Shift;
        return new HotkeyGesture(ToWinFormsKey(keyCode), modifiers);
    }

    private static Keys ToWinFormsKey(KeyCode keyCode)
    {
        int value = (int)keyCode;
        if (value >= (int)KeyCode.VcF1 && value <= (int)KeyCode.VcF12) return (Keys)((int)Keys.F1 + value - (int)KeyCode.VcF1);
        if (value >= (int)KeyCode.VcF13 && value <= (int)KeyCode.VcF24) return (Keys)((int)Keys.F13 + value - (int)KeyCode.VcF13);
        if (value >= (int)KeyCode.Vc0 && value <= (int)KeyCode.Vc9) return (Keys)((int)Keys.D0 + value - (int)KeyCode.Vc0);
        if (value >= (int)KeyCode.VcA && value <= (int)KeyCode.VcZ) return (Keys)((int)Keys.A + value - (int)KeyCode.VcA);
        if (value >= (int)KeyCode.VcNumPad0 && value <= (int)KeyCode.VcNumPad9) return (Keys)((int)Keys.NumPad0 + value - (int)KeyCode.VcNumPad0);
        return keyCode switch
        {
            KeyCode.VcEscape => Keys.Escape, KeyCode.VcBackQuote => Keys.Oemtilde, KeyCode.VcMinus => Keys.OemMinus,
            KeyCode.VcEquals => Keys.Oemplus, KeyCode.VcBackspace => Keys.Back, KeyCode.VcTab => Keys.Tab,
            KeyCode.VcCapsLock => Keys.CapsLock, KeyCode.VcOpenBracket => Keys.OemOpenBrackets,
            KeyCode.VcCloseBracket => Keys.OemCloseBrackets, KeyCode.VcBackslash => Keys.OemPipe,
            KeyCode.VcSemicolon => Keys.OemSemicolon, KeyCode.VcQuote => Keys.OemQuotes,
            KeyCode.VcEnter or KeyCode.VcNumPadEnter => Keys.Enter, KeyCode.VcComma => Keys.Oemcomma,
            KeyCode.VcPeriod => Keys.OemPeriod, KeyCode.VcSlash => Keys.OemQuestion, KeyCode.VcSpace => Keys.Space,
            KeyCode.Vc102 => Keys.Oem102, KeyCode.VcPrintScreen => Keys.PrintScreen, KeyCode.VcScrollLock => Keys.Scroll,
            KeyCode.VcPause => Keys.Pause, KeyCode.VcCancel => Keys.Cancel, KeyCode.VcHelp => Keys.Help,
            KeyCode.VcInsert => Keys.Insert, KeyCode.VcDelete => Keys.Delete, KeyCode.VcHome => Keys.Home,
            KeyCode.VcEnd => Keys.End, KeyCode.VcPageUp => Keys.PageUp, KeyCode.VcPageDown => Keys.PageDown,
            KeyCode.VcUp => Keys.Up, KeyCode.VcLeft => Keys.Left, KeyCode.VcRight => Keys.Right, KeyCode.VcDown => Keys.Down,
            KeyCode.VcNumLock => Keys.NumLock, KeyCode.VcNumPadClear => Keys.Clear, KeyCode.VcNumPadDivide => Keys.Divide,
            KeyCode.VcNumPadMultiply => Keys.Multiply, KeyCode.VcNumPadSubtract => Keys.Subtract,
            KeyCode.VcNumPadEquals => Keys.Oemplus, KeyCode.VcNumPadAdd => Keys.Add,
            KeyCode.VcNumPadDecimal => Keys.Decimal, KeyCode.VcNumPadSeparator => Keys.Separator,
            KeyCode.VcLeftShift => Keys.LShiftKey, KeyCode.VcRightShift => Keys.RShiftKey,
            KeyCode.VcLeftControl => Keys.LControlKey, KeyCode.VcRightControl => Keys.RControlKey,
            KeyCode.VcLeftAlt => Keys.LMenu, KeyCode.VcRightAlt => Keys.RMenu,
            KeyCode.VcLeftMeta => Keys.LWin, KeyCode.VcRightMeta => Keys.RWin, KeyCode.VcContextMenu => Keys.Apps,
            KeyCode.VcVolumeMute => Keys.VolumeMute, KeyCode.VcVolumeDown => Keys.VolumeDown,
            KeyCode.VcVolumeUp => Keys.VolumeUp, KeyCode.VcMediaPlay => Keys.MediaPlayPause,
            KeyCode.VcMediaStop => Keys.MediaStop, KeyCode.VcMediaPrevious => Keys.MediaPreviousTrack,
            KeyCode.VcMediaNext => Keys.MediaNextTrack, KeyCode.VcBrowserSearch => Keys.BrowserSearch,
            KeyCode.VcBrowserHome => Keys.BrowserHome, KeyCode.VcBrowserBack => Keys.BrowserBack,
            KeyCode.VcBrowserForward => Keys.BrowserForward, KeyCode.VcBrowserStop => Keys.BrowserStop,
            KeyCode.VcBrowserRefresh => Keys.BrowserRefresh, KeyCode.VcBrowserFavorites => Keys.BrowserFavorites,
            _ => Keys.None
        };
    }

    private void TriggerCommand(HotkeyCommand command)
    {
        HotkeyState state = GetState(command);
        long now = Environment.TickCount64;
        if (_recordingCommand is not null || state.Gesture is null || now < state.SuppressUntil ||
            (state.LastTriggeredAt != long.MinValue && now - state.LastTriggeredAt < 200)) return;
        state.LastTriggeredAt = now;
        if (command == HotkeyCommand.Capture) CaptureTriggered?.Invoke();
        if (command == HotkeyCommand.Ocr) OcrTriggered?.Invoke();
        CommandTriggered?.Invoke(command);
    }

    private HotkeyChangeResult RequestElevatedRestart(HotkeyCommand command, HotkeyGesture gesture)
    {
        string previousHotkey = HotkeyCommandCatalog.GetConfigText(ConfigService.Current, command);
        bool previousForce = HotkeyCommandCatalog.GetForceBinding(ConfigService.Current, command);
        HotkeyCommandCatalog.SetConfig(ConfigService.Current, command, gesture.ConfigText, forceBinding: true);
        try
        {
            if (!ConfigService.Save())
            {
                RestorePendingHotkey(command, previousHotkey, previousForce);
                return new HotkeyChangeResult(false, "配置保存失败，原快捷键保持不变", HotkeyChangeFailure.Persistence);
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = Application.ExecutablePath,
                Arguments = $"--elevated-relaunch --wait-for-pid {Environment.ProcessId}",
                UseShellExecute = true,
                Verb = "runas"
            });
            Application.ExitThread();
            return new HotkeyChangeResult(false, "绑定这个按键需要管理员权限，正在请求 UAC；允许后应用会自动重启");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            RestorePendingHotkey(command, previousHotkey, previousForce);
            return new HotkeyChangeResult(false, "已取消管理员权限请求，原快捷键保持不变");
        }
        catch
        {
            RestorePendingHotkey(command, previousHotkey, previousForce);
            return new HotkeyChangeResult(false, "无法请求管理员权限，原快捷键保持不变");
        }
    }

    private static HotkeyChangeResult CreateInvalidGestureResult(HotkeyGesture gesture, bool forceBinding)
    {
        if (gesture.KeyCode == Keys.None) return new HotkeyChangeResult(false, "无法识别这个按键，请换一个按键重试");
        return new HotkeyChangeResult(
            false,
            forceBinding ? $"无法绑定 {gesture.DisplayText}；请按一个非修饰键，Esc 取消" : $"{gesture.DisplayText} 是单独按键",
            HotkeyChangeFailure.Invalid);
    }

    private static void RestorePendingHotkey(HotkeyCommand command, string hotkey, bool forceBinding)
    {
        HotkeyCommandCatalog.SetConfig(ConfigService.Current, command, hotkey, forceBinding);
        ConfigService.Save();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_APP_RECOVER)
        {
            RecoverConfiguredHotkeys();
        }
        else if (m.Msg == WM_APP_FORCE_TRIGGER)
        {
            HotkeyCommand command = (HotkeyCommand)m.WParam.ToInt32();
            if (_states.TryGetValue(command, out HotkeyState? state) &&
                state.Gesture is HotkeyGesture gesture && (int)(gesture.KeyCode | gesture.Modifiers) == m.LParam.ToInt32())
                TriggerCommand(command);
        }
        else if (m.Msg == NativeMethods.WM_HOTKEY)
        {
            int hotkeyId = m.WParam.ToInt32();
            HotkeyState? state = _states.Values.FirstOrDefault(candidate => candidate.Registered && candidate.RegistrationId == hotkeyId);
            if (state is not null) TriggerCommand(state.Command);
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        Volatile.Write(ref _hookBindings, []);
        foreach (HotkeyState state in _states.Values.Where(candidate => candidate.Registered))
        {
            NativeMethods.UnregisterHotKey(Handle, state.RegistrationId);
            state.Registered = false;
        }
        DisposeKeyboardHook();
        DestroyHandle();
        GC.SuppressFinalize(this);
    }
}
