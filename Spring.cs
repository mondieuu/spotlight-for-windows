using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace Odak;

/// <summary>
/// Apple'ın "Designing Fluid Interfaces" yaklaşımıyla yay animasyonu: süre yerine
/// <b>tepki (response, saniye)</b> ve <b>sönüm oranı (damping)</b> ile tanımlanır.
/// Her zaman ekrandaki anlık değerden ve hızdan devam eder, bu yüzden yarıda kesilip
/// yön değiştirilebilir (hedef değişince hareket kopmadan yeni hedefe akar).
/// </summary>
public sealed class Spring
{
    private static readonly List<Spring> Active = new();
    private static TimeSpan _last;
    private static bool _hooked;

    private readonly Action<double> _apply;
    private double _response, _damping;
    private Action? _settled;

    public double Value { get; private set; }
    public double Velocity { get; private set; }
    public double Target { get; private set; }
    public bool IsMoving => Active.Contains(this);

    public Spring(double initial, Action<double> apply, double response = 0.35, double damping = 1.0)
    {
        Value = Target = initial;
        _apply = apply;
        _response = response;
        _damping = damping;
    }

    /// <summary>Yeni hedefe yönel (anlık değer ve hız korunur).</summary>
    public void To(double target, double? response = null, double? damping = null, Action? settled = null)
    {
        Target = target;
        if (response is { } r) _response = r;
        if (damping is { } d) _damping = d;
        _settled = settled;
        if (!Active.Contains(this)) Active.Add(this);
        Hook();
    }

    /// <summary>Animasyonsuz anında yerleş (ör. "Hareketi azalt" açıkken).</summary>
    public void Snap(double value)
    {
        Active.Remove(this);
        Value = Target = value;
        Velocity = 0;
        _apply(value);
        var s = _settled;
        _settled = null;
        s?.Invoke();
    }

    private static void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        _last = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private static void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        // Aynı karede iki kez çağrılabilir; ilk karede ve uzun duraklamalarda adımı sınırla.
        double dt = _last == TimeSpan.Zero ? 1 / 60.0 : (now - _last).TotalSeconds;
        if (dt <= 0) return;
        _last = now;
        dt = Math.Min(dt, 1 / 30.0);

        foreach (var s in Active.ToArray()) s.Step(dt);
        if (Active.Count == 0)
        {
            CompositionTarget.Rendering -= OnFrame;
            _hooked = false;
        }
    }

    private void Step(double dt)
    {
        // Tepki ve sönüm → yay sabiti ve sürtünme (birim kütle).
        double omega = 2 * Math.PI / _response;
        double k = omega * omega, c = 2 * _damping * omega;
        const int Sub = 4;
        double h = dt / Sub;
        for (int i = 0; i < Sub; i++)
        {
            double a = -k * (Value - Target) - c * Velocity;
            Velocity += a * h;
            Value += Velocity * h;
        }
        double span = Math.Max(1e-3, Math.Abs(Target) + 1);
        if (Math.Abs(Value - Target) < 0.0005 * span && Math.Abs(Velocity) < 0.005 * span)
        {
            Value = Target;
            Velocity = 0;
            Active.Remove(this);
            _apply(Value);
            var s = _settled;
            _settled = null;
            s?.Invoke();
            return;
        }
        _apply(Value);
    }
}
