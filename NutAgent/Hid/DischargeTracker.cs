namespace NutAgent.Hid;

// Tracks battery discharge rate using a circular buffer of charge readings.
// Phase 2: values are published into Variables for monitoring.
// Phase 4 (Auto mode): MinutesToEmpty drives the dynamic shutdown threshold.
public sealed class DischargeTracker
{
    private const int BufferSize = 20;

    private readonly (double Charge, DateTime Timestamp)[] _buffer =
        new (double, DateTime)[BufferSize];
    private int  _head;
    private int  _count;
    private readonly object _lock = new();

    // Call each poll cycle regardless of power state.
    public void Record(double charge, DateTime timestamp)
    {
        lock (_lock)
        {
            _buffer[_head] = (charge, timestamp);
            _head  = (_head + 1) % BufferSize;
            if (_count < BufferSize) _count++;
        }
    }

    // Call when power is restored so stale on-battery readings don't skew the next discharge.
    public void Reset()
    {
        lock (_lock)
        {
            _head  = 0;
            _count = 0;
        }
    }

    // Percent per minute; positive = discharging, 0 = stable or insufficient data.
    public double DischargeRatePerMinute => Snapshot().Rate;

    // Estimated minutes until battery empty at current rate; double.MaxValue when not discharging.
    public double MinutesToEmpty => Snapshot().Minutes;

    private (double Rate, double Minutes) Snapshot()
    {
        lock (_lock)
        {
            if (_count < 2) return (0, double.MaxValue);

            int oldestIdx = _count < BufferSize ? 0 : _head;
            int newestIdx = (_head - 1 + BufferSize) % BufferSize;

            var oldest  = _buffer[oldestIdx];
            var newest  = _buffer[newestIdx];
            double elapsed = (newest.Timestamp - oldest.Timestamp).TotalMinutes;
            if (elapsed <= 0) return (0, double.MaxValue);

            double rate    = (oldest.Charge - newest.Charge) / elapsed;
            double minutes = rate > 0 ? newest.Charge / rate : double.MaxValue;
            return (rate, minutes);
        }
    }
}
