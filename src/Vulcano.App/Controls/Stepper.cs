using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Vulcano.App.Controls;

/// <summary>
/// The "– 195 +" control the design uses wherever a number is adjusted: target temperature, hold
/// minutes, history length, push threshold. Deliberately not NumericUpDown - that brings a text box
/// and a spinner, and every one of these values is a coarse dial the user nudges rather than types.
///
/// The value is clamped to <see cref="Minimum"/>/<see cref="Maximum"/> on every change, so a caller
/// binding a raw device value cannot push the display out of range.
/// </summary>
public class Stepper : TemplatedControl
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<Stepper, double>(
            nameof(Value), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay, coerce: CoerceValue);

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<Stepper, double>(nameof(Minimum), 0);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<Stepper, double>(nameof(Maximum), 100);

    public static readonly StyledProperty<double> StepProperty =
        AvaloniaProperty.Register<Stepper, double>(nameof(Step), 1);

    /// <summary>Shown after the value in a dimmer colour, e.g. "°C" or "min". Optional.</summary>
    public static readonly StyledProperty<string?> UnitProperty =
        AvaloniaProperty.Register<Stepper, string?>(nameof(Unit));

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Step
    {
        get => GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    public string? Unit
    {
        get => GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    private static double CoerceValue(AvaloniaObject o, double value)
    {
        var stepper = (Stepper)o;
        return Math.Clamp(value, stepper.Minimum, stepper.Maximum);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        if (e.NameScope.Find<Button>("PART_Decrease") is { } decrease)
        {
            decrease.Click += (_, _) => Value -= Step;
        }

        if (e.NameScope.Find<Button>("PART_Increase") is { } increase)
        {
            increase.Click += (_, _) => Value += Step;
        }
    }
}
