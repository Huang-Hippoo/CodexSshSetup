using CodexSshSetup.App.Models;

namespace CodexSshSetup.App.ViewModels;

public sealed class StatusCardViewModel(string name) : BindableBase
{
    private ComponentState _state = ComponentState.Unknown;
    private string _summary = "尚未检测";
    public string Name { get; } = name;
    public ComponentState State { get => _state; private set => Set(ref _state, value); }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public string Glyph => State switch { ComponentState.Ready => "✓", ComponentState.Checking => "…", ComponentState.Warning => "!", ComponentState.Failed => "×", _ => "○" };
    public string Color => State switch { ComponentState.Ready => "#1E9E63", ComponentState.Checking => "#2878D0", ComponentState.Warning => "#D68A00", ComponentState.Failed => "#D04444", _ => "#7C8799" };

    public void Update(ComponentState state, string summary)
    {
        State = state;
        Summary = summary;
        Raise(nameof(Glyph));
        Raise(nameof(Color));
    }
}
