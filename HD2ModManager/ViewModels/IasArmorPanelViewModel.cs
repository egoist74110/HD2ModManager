using System.ComponentModel;
using HD2ModCore.Ias;

namespace HD2ModManager.ViewModels;

public sealed class IasArmorPanelViewModel(bool willSwapOccupants = true) : INotifyPropertyChanged
{
    private IReadOnlyList<IasRetargetChoice> _choices = [];
    private string _query = "";
    private string? _message = "正在检查两件套与身体护甲…";
    private IasArmorOption? _selected;

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<IasArmorGroup> Groups { get; private set; } = [];
    public string Query
    {
        get => _query;
        set
        {
            if (_query == value) return;
            _query = value;
            Changed(nameof(Query));
            Filter();
        }
    }

    public string? Message
    {
        get => _message;
        set
        {
            _message = value;
            Changed(nameof(Message));
        }
    }

    public IasArmorOption? Selected
    {
        get => _selected;
        private set
        {
            _selected = value;
            Changed(nameof(Selected));
            Changed(nameof(HasSelection));
        }
    }

    public bool HasSelection => Selected is not null;

    public void Show(IasRetargetAnalysis analysis)
    {
        _choices = analysis.Choices;
        Message = analysis.Reason;
        Filter();
    }

    private void Filter()
    {
        Selected = null;
        var query = Query.Trim();
        var options = _choices.Where(choice => choice.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            choice.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(choice => new IasArmorOption(choice, option => Selected = option, willSwapOccupants)).ToArray();
        Groups =
        [
            new("空闲，能换", options.Where(row => row.CanSelect && !row.Occupied).ToArray()),
            new("被占用", options.Where(row => row.CanSelect && row.Occupied).ToArray()),
            new("换不了", options.Where(row => !row.CanSelect).ToArray())
        ];
        Changed(nameof(Groups));
    }

    private void Changed(string property) => PropertyChanged?.Invoke(this, new(property));
}

public sealed record IasArmorGroup(string Name, IReadOnlyList<IasArmorOption> Options)
{
    public string Header => $"{Name}（{Options.Count}）";
}

public sealed class IasArmorOption
{
    private readonly bool _willSwapOccupants;

    public IasArmorOption(IasRetargetChoice choice, Action<IasArmorOption> select, bool willSwapOccupants = true)
    {
        Choice = choice;
        _willSwapOccupants = willSwapOccupants;
        SelectCommand = new(_ => select(this), _ => CanSelect);
    }

    public IasRetargetChoice Choice { get; }
    public RelayCommand SelectCommand { get; }
    public bool CanSelect => Choice.Reason is null;
    public bool Occupied => Choice.Occupants is { Length: > 0 };
    public string Name => Choice.Name;
    public bool IsOriginal => Choice.Original;
    public string Detail => Choice.Reason ?? (Occupied
        ? $"{string.Join("、", Choice.Occupants!.Select(name => $"『{name}』"))}占着" +
          (_willSwapOccupants ? "，选了会停用它" : "，启用时会停用它")
        : "没人占用");
}
