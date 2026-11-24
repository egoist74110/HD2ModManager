using System.Windows.Data;
using HD2ModManager.Services.Ias;

namespace HD2ModManager.ViewModels;

public sealed class IasTargetPickerViewModel : BaseViewModel
{
    private readonly Action<IasTargetChoice> _onSelected;
    private IasTargetChoice? _selected;
    private string _query = "";
    private bool _isOpen;
    private bool _needsSelection;
    private string? _originalName;

    public IasTargetPickerViewModel(string label, Action<IasTargetChoice> onSelected)
    {
        Label = label;
        _onSelected = onSelected;
        Choices = CreateView([]);
        SelectCommand = new(value =>
        {
            if (value is not IasTargetChoice choice) return;
            _selected = choice;
            NeedsSelection = choice.Primary is null && _originalName is null;
            IsOpen = false;
            OnPropertyChanged(nameof(Selected));
            OnPropertyChanged(nameof(SelectionText));
            OnPropertyChanged(nameof(SelectionIconPath));
            _onSelected(choice);
        });
    }

    public string Label { get; }
    public ListCollectionView Choices { get; private set; }
    public RelayCommand SelectCommand { get; }
    public IasTargetChoice? Selected => _selected;
    public string SelectionText => _selected?.Primary is not null ? _selected.Name
        : _originalName is not null ? $"原来那套：{_originalName}" : $"选择{Label}";
    public string? SelectionIconPath => _selected?.Primary is not null ? _selected.IconPath : null;
    public bool NeedsSelection
    {
        get => _needsSelection;
        private set { _needsSelection = value; OnPropertyChanged(); }
    }

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (_isOpen == value) return;
            _isOpen = value;
            if (value) Query = "";
            OnPropertyChanged();
        }
    }

    public string Query
    {
        get => _query;
        set { _query = value; Choices.Refresh(); OnPropertyChanged(); }
    }

    public void SetChoices(IReadOnlyList<IasTargetChoice> choices, IasTargetChoice? selected, bool needsSelection, string? originalName = null)
    {
        IsOpen = false;
        _selected = selected;
        _originalName = originalName;
        NeedsSelection = needsSelection;
        Choices = CreateView(choices);
        OnPropertyChanged(nameof(Choices));
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(SelectionIconPath));
    }

    private ListCollectionView CreateView(IReadOnlyList<IasTargetChoice> choices)
    {
        var view = new ListCollectionView(choices.ToArray())
        {
            Filter = item => item is IasTargetChoice choice &&
                (choice.Primary is null || choice.Name.Contains(Query.Trim(), StringComparison.OrdinalIgnoreCase) ||
                 choice.SearchText.Contains(Query.Trim(), StringComparison.OrdinalIgnoreCase))
        };
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(IasTargetChoice.Group)));
        return view;
    }
}

public sealed class IasConversionPanelViewModel : BaseViewModel
{
    private string? _message;
    private IasStandardTargetCatalog? _catalog;
    private bool _mergeHelmet;

    public IasConversionPanelViewModel()
    {
        Armor = new("护甲", _ => Message = null);
        Helmet = new("头盔", _ => Message = null);
    }

    public IasTargetPickerViewModel Armor { get; }
    public IasTargetPickerViewModel Helmet { get; }
    public bool HasArmor => _catalog?.Armor is not null;
    public bool HasHelmet => _catalog?.Helmet is not null;
    public bool CanMerge => HasArmor && HasHelmet;
    private bool IsLoaded => _catalog is not null;
    public bool MergeHelmet
    {
        get => _mergeHelmet;
        set { _mergeHelmet = value; OnPropertyChanged(); }
    }

    public string? Message
    {
        get => _message;
        set { _message = value; OnPropertyChanged(); }
    }

    public void ShowCatalog(IasStandardTargetCatalog catalog)
    {
        _catalog = catalog;
        SetDomain(Armor, catalog.Armor);
        SetDomain(Helmet, catalog.Helmet);
        Message = MissingCompatibleTarget();
        OnPropertyChanged(nameof(HasArmor));
        OnPropertyChanged(nameof(HasHelmet));
        OnPropertyChanged(nameof(CanMerge));
    }

    private static void SetDomain(IasTargetPickerViewModel picker, IasStandardTargetDomain? domain)
    {
        if (domain is null) return;
        var follow = new IasTargetChoice(domain.OriginalName is null ? "跟随原来那套（默认）" : $"原来那套：{domain.OriginalName}", "", []);
        picker.SetChoices([follow, .. domain.Choices], domain.OriginalId is null ? null : follow,
            domain.OriginalId is null, domain.OriginalName);
    }

    public bool TryGetTargets(out string? armor, out string? helmet)
    {
        armor = Format(Armor, _catalog?.Armor);
        helmet = Format(Helmet, _catalog?.Helmet);
        if (!IsLoaded) return false;
        if (!Armor.NeedsSelection && !Helmet.NeedsSelection) return true;
        Message = MissingCompatibleTarget() ?? (Armor.NeedsSelection ? "请先选择目标护甲。" : "请先选择目标头盔。");
        return false;

        static string? Format(IasTargetPickerViewModel picker, IasStandardTargetDomain? domain) =>
            (picker.Selected?.Primary?.ItemId ?? domain?.OriginalId) is { } id ? $"0x{id:x8}" : null;
    }
    private string? MissingCompatibleTarget()
    {
        if (_catalog?.Armor is { OriginalId: null, Choices.Count: 0 })
            return "找不到与包内原版格子兼容的目标护甲。";
        if (_catalog?.Helmet is { OriginalId: null, Choices.Count: 0 })
            return "找不到与包内原版格子兼容的目标头盔。";
        return null;
    }
}
