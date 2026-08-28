using Spectre.Console;

namespace Axplayer.UI;

/// <summary>
/// Real-time ASCII/Unicode spectrum analyzer. Streams don't expose decoded PCM
/// to a console app (LibVLC plays internally), so this uses a "dummy simulation
/// mode": a smooth procedural music engine (layered sines + noise + occasional
/// beats) drives the bars. The result reads as a live spectrum, reacts to
/// play/pause, and scales with the configured sensitivity.
///
/// Rendering: 3-row chunky bars (one column per frequency band), color-coded
/// low=red / mid=yellow / high=green-cyan, with per-bar peak-hold caps and a
/// VU meter + peak indicator on the right edge.
/// </summary>
public sealed class Visualizer
{
    private readonly int _barCount;
    private readonly double _sensitivity; // 0..1
    private readonly Random _rng = new();
    private readonly double[] _level;
    private readonly double[] _target;
    private readonly double[] _peak;
    private readonly int[] _peakHold;
    private readonly double[] _phase;

    private double _vuLevel;
    private double _vuPeak;
    private long _frame;
    private bool _active;

    public Visualizer(int barCount, int sensitivity)
    {
        _barCount = Math.Clamp(barCount, 8, 80);
        _sensitivity = Math.Clamp(sensitivity, 0, 100) / 100.0;

        _level = new double[_barCount];
        _target = new double[_barCount];
        _peak = new double[_barCount];
        _peakHold = new int[_barCount];
        _phase = new double[_barCount];
        for (int i = 0; i < _barCount; i++)
            _phase[i] = _rng.NextDouble() * Math.PI * 2;
    }

    /// <summary>Advance the simulated spectrum by one frame.</summary>
    public void Update(double dtSeconds, bool active)
    {
        _active = active;
        _frame++;
        double energy = active ? 1.0 : 0.0;
        double time = _frame * 0.9;

        for (int i = 0; i < _barCount; i++)
        {
            double freq = 0.5 + (i / (double)_barCount) * 3.0;
            double wave =
                Math.Sin(time * freq + _phase[i]) * 0.35 +
                Math.Sin(time * freq * 2.13 + _phase[i] * 1.7) * 0.25 +
                Math.Sin(time * freq * 0.37 + _phase[i] * 0.4) * 0.22 +
                (_rng.NextDouble() - 0.5) * 0.16;

            double target = Math.Clamp((wave * 0.5 + 0.5) * energy * (0.3 + 0.7 * _sensitivity), 0, 1);

            // Occasional "beat" spikes.
            if (active && _rng.NextDouble() < 0.005)
                target = 1.0;

            _target[i] = target;

            // Fast attack, slower release.
            double rate = _level[i] < _target[i] ? 14 : 4.5;
            _level[i] += (_target[i] - _level[i]) * Math.Min(1, rate * dtSeconds);

            // Peak hold.
            if (_level[i] >= _peak[i])
            {
                _peak[i] = _level[i];
                _peakHold[i] = 24;
            }
            else if (_peakHold[i] > 0)
            {
                _peakHold[i]--;
            }
            else
            {
                _peak[i] = Math.Max(0, _peak[i] - 0.015);
            }
        }

        // Overall VU level (average energy).
        double overall = 0;
        for (int i = 0; i < _barCount; i++) overall += _level[i];
        overall = overall / _barCount * 1.6 * (active ? 1 : 0);

        _vuLevel += (Math.Min(1, overall) - _vuLevel) * Math.Min(1, 10 * dtSeconds);
        if (_vuLevel >= _vuPeak) _vuPeak = _vuLevel;
        else _vuPeak = Math.Max(0, _vuPeak - 0.006);
    }

    /// <summary>
    /// Render the visualizer as 3 markup strings (top/middle/bottom rows).
    /// The bars auto-fit the available width.
    /// </summary>
    /// <summary>
    /// Render one frame as 3 markup strings (top/middle/bottom rows). Each bar
    /// occupies exactly 2 visible columns: a gap column (which can hold a peak
    /// cap) plus the bar cell itself.
    /// </summary>
    public string[] Render(int availableWidth)
    {
        int maxBars = Math.Max(1, Math.Min(_barCount, Math.Max(2, availableWidth) / 2));
        var rows = new string[3];
        for (int r = 0; r < 3; r++)
        {
            var line = new System.Text.StringBuilder();
            for (int i = 0; i < maxBars; i++)
            {
                int height = (int)Math.Round(_level[i] * 6);
                var color = BandColor(i, maxBars);
                line.Append('[').Append(color).Append(']');

                // Gap column: a peak cap "░" appears here above a falling bar.
                int peakHeight = (int)Math.Round(_peak[i] * 6);
                if (_peakHold[i] > 0 && peakHeight > height && RowIndex(peakHeight) == r)
                    line.Append("[white]░[/]");
                else
                    line.Append(' ');

                line.Append(BarCell(height, r)).Append("[/]");
            }
            rows[r] = line.ToString();
        }
        return rows;
    }

    /// <summary>
    /// VU meter (vertical, 3 rows) with a peak indicator column.
    /// Rendered as two characters per row: level cell + peak cell.
    /// </summary>
    public string[] RenderVuMeter()
    {
        int level = (int)Math.Round(_vuLevel * 8);
        int peak = (int)Math.Round(_vuPeak * 8);

        var rows = new string[3];
        for (int r = 0; r < 3; r++)
        {
            string levelChar;
            string color;
            if (r == 2)
            {
                color = "green";
                levelChar = level switch { >= 3 => "█", >= 1 => "▄", _ => " " };
            }
            else if (r == 1)
            {
                color = "yellow";
                levelChar = level switch { >= 6 => "█", >= 4 => "▄", _ => " " };
            }
            else
            {
                color = "red";
                levelChar = level switch { >= 8 => "█", >= 7 => "▄", _ => " " };
            }

            // Peak column: dot shown on the row where the held peak currently sits.
            int peakRow = peak switch { >= 7 => 0, >= 4 => 1, >= 1 => 2, _ => -1 };
            string peakChar = peakRow == r ? "[white].[/]" : " ";

            rows[r] = $"[{color}]{levelChar}[/]{peakChar}";
        }
        return rows;
    }

    private static int RowIndex(int height) => height switch
    {
        >= 6 => 0,
        >= 4 => 1,
        >= 2 => 2,
        _ => -1,
    };

    private static string BarCell(int height, int row) => height switch
    {
        _ when row == 0 => height >= 6 ? "█" : height == 5 ? "▀" : " ",
        _ when row == 1 => height >= 4 ? "█" : height == 3 ? "▄" : " ",
        _ => height >= 2 ? "█" : height == 1 ? "▄" : " ",
    };

    /// <summary>Frequency-band color: low=red, mid=yellow, high=green→cyan.</summary>
    private static string BandColor(int index, int count)
    {
        double pos = count <= 1 ? 0 : index / (double)(count - 1);
        if (pos < 0.4) return "red";
        if (pos < 0.7) return "yellow";
        return pos < 0.85 ? "green" : "cyan";
    }
}
