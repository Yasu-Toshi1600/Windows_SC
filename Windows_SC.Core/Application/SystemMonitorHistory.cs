using System;
using System.Collections.Generic;

namespace Windows_SC.Services;

internal readonly record struct MonitorGraphPoint(double X, double Y);

internal sealed class SystemMonitorHistory(int capacity = 60)
{
    private readonly Queue<double> _values = new();
    private readonly int _capacity = capacity > 0
        ? capacity
        : throw new ArgumentOutOfRangeException(nameof(capacity));

    public void Add(double? value)
    {
        if (value is null)
        {
            _values.Clear();
            return;
        }

        _values.Enqueue(Math.Clamp(value.Value, 0, 100));
        while (_values.Count > _capacity)
        {
            _values.Dequeue();
        }
    }

    public void Clear() => _values.Clear();

    public IReadOnlyList<MonitorGraphPoint> CreateGraphPoints()
    {
        List<MonitorGraphPoint> points = [];
        if (_values.Count == 0)
        {
            return points;
        }

        double xStep = _values.Count == 1 ? 0 : 100d / (_values.Count - 1);
        int index = 0;
        foreach (double value in _values)
        {
            points.Add(new MonitorGraphPoint(index * xStep, 40d - (value * 0.4d)));
            index++;
        }

        return points;
    }
}
