namespace SkeldForge.ViewModels.Infrastructure;

public sealed class ChoiceItem<T>(T value, string label)
{
    public T Value { get; } = value;
    public string Label { get; } = label;
    public override string ToString() => Label;
}
