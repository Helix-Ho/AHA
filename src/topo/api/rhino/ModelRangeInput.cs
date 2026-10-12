using System.Globalization;
using Eto.Drawing;
using Eto.Forms;

namespace Aha.Topo;

// Only model extents snap to 100m on directional input; typed extents stay exact.
internal sealed class ModelRangeInput : Panel
{
    private readonly TextBox text = new() { Width = 88 };
    private readonly Stepper stepper = new();
    private bool setting;
    internal event EventHandler<EventArgs>? ValueChanged;
    internal double Value
    {
        get => Parse();
        set
        {
            Check(value);
            setting = true;
            try { text.Text = value.ToString("F1", CultureInfo.CurrentCulture); }
            finally { setting = false; }
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal ModelRangeInput(double value)
    {
        Content = new TableLayout { Spacing = new Size(3, 0), Rows = { new TableRow(new TableCell(text, true), stepper) } };
        Value = value;
        text.TextChanged += (_, _) => { if (!setting) ValueChanged?.Invoke(this, EventArgs.Empty); };
        text.LostFocus += (_, _) => TryCommit();
        text.KeyDown += (_, e) =>
        {
            if (e.Key is Keys.Up or Keys.Down) { e.Handled = true; Step(e.Key == Keys.Up); }
            else if (e.Key == Keys.Enter) { e.Handled = true; TryCommit(); }
        };
        stepper.Step += (_, e) => Step(e.Direction == StepperDirection.Up);
    }

    internal void Commit() => Value = Parse();
    internal void Step(bool up)
    {
        try
        {
            double value = Parse();
            Value = Math.Clamp(100 * (up ? Math.Floor(value / 100) + 1 : Math.Ceiling(value / 100) - 1), 50, 3000);
        }
        catch (ArgumentException) { ValueChanged?.Invoke(this, EventArgs.Empty); }
    }
    private void TryCommit()
    {
        try { Commit(); }
        catch (ArgumentException) { ValueChanged?.Invoke(this, EventArgs.Empty); }
    }
    private double Parse()
    {
        if (!double.TryParse(text.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value))
            throw new ArgumentException("모델 범위 입력 오류 · 50–3,000 m");
        Check(value);
        return Math.Round(value, 1, MidpointRounding.AwayFromZero);
    }
    private static void Check(double value)
    {
        if (!double.IsFinite(value) || value < 50 || value > 3000)
            throw new ArgumentException("모델 범위 입력 오류 · 50–3,000 m");
    }
}
