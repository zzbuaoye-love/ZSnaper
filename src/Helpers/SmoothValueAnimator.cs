using ZSnaper.Services;

namespace ZSnaper.Helpers;

/// <summary>
/// 120Hz critically-damped spring value animator backed by <see cref="UiAnimationTimer"/>.
/// Preserves position and velocity when targets change mid-flight for interruption-proof motion.
/// </summary>
internal sealed class SmoothValueAnimator : IDisposable
{
    private readonly Control _owner;
    private readonly Action _onUpdated;
    private readonly UiAnimationTimer _timer;
    private readonly float _settleTolerance;
    private readonly int _defaultDurationMs;
    private int _activeDurationMs;
    private float _value;
    private float _target;
    private float _velocity;

    public float Value => _value;
    public float Target => _target;
    public bool IsAnimating => _timer.Enabled;

    public SmoothValueAnimator(
        Control owner,
        float initialValue,
        Action onUpdated,
        int defaultDurationMs = 175,
        float settleTolerance = 0.0015f)
    {
        _owner = owner;
        _value = initialValue;
        _target = initialValue;
        _onUpdated = onUpdated;
        _defaultDurationMs = Math.Max(40, defaultDurationMs);
        _activeDurationMs = _defaultDurationMs;
        _settleTolerance = Math.Max(0.0001f, settleTolerance);
        _timer = new UiAnimationTimer(owner, Step);
    }

    public void SetTarget(float target, int? overrideDurationMs = null)
    {
        if (Math.Abs(_target - target) <= _settleTolerance * 0.25f && !_timer.Enabled)
        {
            return;
        }

        _target = target;
        _activeDurationMs = Math.Max(40, overrideDurationMs ?? _defaultDurationMs);

        if (!_owner.IsHandleCreated || _owner.IsDisposed)
        {
            _timer.Stop();
            _value = target;
            _velocity = 0f;
            _onUpdated();
            return;
        }

        if (Math.Abs(_value - _target) <= _settleTolerance && Math.Abs(_velocity) <= _settleTolerance * 8f)
        {
            _timer.Stop();
            _value = target;
            _velocity = 0f;
            _onUpdated();
            return;
        }

        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        _velocity = 0f;
    }

    public void SnapTo(float value)
    {
        _timer.Stop();
        _value = value;
        _target = value;
        _velocity = 0f;
        _onUpdated();
    }

    public void Complete()
    {
        if (!_timer.Enabled && Math.Abs(_value - _target) <= _settleTolerance)
        {
            return;
        }

        SnapTo(_target);
    }

    private void Step(double elapsedSeconds)
    {
        int durationMs = Math.Max(40, ConfigService.GetAnimationDuration(_activeDurationMs));
        float omega = 5.2f * 1000f / durationMs;
        float dt = (float)elapsedSeconds;
        float displacement = _value - _target;
        float exp = MathF.Exp(-omega * dt);
        float temp = (_velocity + omega * displacement) * dt;

        _value = _target + (displacement + temp) * exp;
        _velocity = (_velocity - temp * omega) * exp;

        if (Math.Abs(_value - _target) <= _settleTolerance &&
            Math.Abs(_velocity) <= _settleTolerance * 8f)
        {
            _value = _target;
            _velocity = 0f;
            _timer.Stop();
        }

        _onUpdated();
    }

    public void Dispose() => _timer.Dispose();
}
