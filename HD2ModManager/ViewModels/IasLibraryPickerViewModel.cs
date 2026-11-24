using System.Collections.ObjectModel;
using HD2ModManager.Services.Ias;

namespace HD2ModManager.ViewModels;

public enum IasLibraryFilter { All, Current, Standard, Vpk, Body }

/// <summary>卡片：候选加上「是否勾选」。不可转的卡片永远选不上。</summary>
public sealed class IasLibraryPickerItem(IasLibraryCandidate candidate) : BaseViewModel
{
    private bool _isSelected;

    public IasLibraryCandidate Candidate { get; } = candidate;
    public string Name => Candidate.Name;
    public string? ThumbnailPath => Candidate.ThumbnailPath;
    public string SourceText => Candidate.SourceText;
    public IasLibrarySource Source => Candidate.Source;
    public string MetaText => Candidate.MetaText;
    public bool HasMeta => Candidate.HasMeta;
    public string? Number => Candidate.Number;
    public bool HasNumber => !string.IsNullOrEmpty(Candidate.Number);
    public bool IsConvertible => Candidate.IsConvertible;
    public string? UnavailableReason => Candidate.UnavailableReason;
    public string ToolTipText => HasNumber ? $"{Name}\n编号 {Number}" : Name;

    public bool IsSelected
    {
        get => _isSelected;
        internal set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }
}

/// <summary>「从库里选 Mod 转 IAS」对话框的逻辑：筛选、搜索、分组、多选。不碰界面，便于单测。</summary>
public sealed class IasLibraryPickerViewModel : BaseViewModel
{
    public const int MaxAvatars = 4;

    private IReadOnlyList<IasLibraryPickerItem> _all = [];
    private readonly List<IasLibraryPickerItem> _selection = [];
    private string _searchText = "";
    private IasLibraryFilter _filter = IasLibraryFilter.All;
    private bool _isLoading = true;
    private string? _loadError;

    public IasLibraryPickerViewModel()
    {
        ToggleCommand = new(value =>
        {
            if (value is IasLibraryPickerItem item) Toggle(item);
        });
        SetFilterCommand = new(value =>
        {
            if (value is IasLibraryFilter filter) Filter = filter;
        });
    }

    public RelayCommand ToggleCommand { get; }
    public RelayCommand SetFilterCommand { get; }

    /// <summary>「当前配置里的」：在当前配置启用的 Mod。</summary>
    public ObservableCollection<IasLibraryPickerItem> CurrentItems { get; } = [];
    /// <summary>「库里其他的」。</summary>
    public ObservableCollection<IasLibraryPickerItem> OtherItems { get; } = [];
    /// <summary>底栏叠放的已选头像，最多 <see cref="MaxAvatars"/> 个。</summary>
    public ObservableCollection<IasLibraryPickerItem> Avatars { get; } = [];

    public bool HasCurrent => CurrentItems.Count != 0;
    public bool HasOther => OtherItems.Count != 0;
    public bool IsEmpty => !IsLoading && LoadError is null && !HasCurrent && !HasOther;
    public bool ShowList => !IsLoading && LoadError is null && (HasCurrent || HasOther);
    public int TotalCount => _all.Count;

    public bool IsLoading
    {
        get => _isLoading;
        private set { if (SetField(ref _isLoading, value)) NotifyView(); }
    }

    public string? LoadError
    {
        get => _loadError;
        private set { if (SetField(ref _loadError, value)) { OnPropertyChanged(nameof(HasLoadError)); NotifyView(); } }
    }

    public bool HasLoadError => LoadError is not null;

    public string SearchText
    {
        get => _searchText;
        set { if (SetField(ref _searchText, value ?? "")) Rebuild(); }
    }

    public IasLibraryFilter Filter
    {
        get => _filter;
        set { if (SetField(ref _filter, value)) Rebuild(); }
    }

    public int SelectedCount => _selection.Count;
    public bool CanConfirm => _selection.Count != 0;
    public IReadOnlyList<IasLibraryCandidate> SelectedCandidates => _selection.Select(item => item.Candidate).ToArray();

    public string SummaryText => _selection.Count == 0 ? "点卡片选择，可以多选" : $"已选 {_selection.Count} 个";
    public string ConfirmText => _selection.Count > 1 ? $"转为 IAS 并安装 ({_selection.Count})" : "转为 IAS 并安装";

    public void SetCandidates(IReadOnlyList<IasLibraryCandidate> candidates)
    {
        _all = candidates.Select(candidate => new IasLibraryPickerItem(candidate)).ToArray();
        _selection.Clear();
        LoadError = null;
        IsLoading = false;
        Rebuild();
        NotifySelection();
    }

    public void SetLoadFailed(string message)
    {
        _all = [];
        _selection.Clear();
        LoadError = message;
        IsLoading = false;
        Rebuild();
        NotifySelection();
    }

    public void Toggle(IasLibraryPickerItem item)
    {
        if (!item.IsConvertible) return;
        item.IsSelected = !item.IsSelected;
        if (item.IsSelected) _selection.Add(item);
        else _selection.Remove(item);
        NotifySelection();
    }

    private bool Visible(IasLibraryPickerItem item)
    {
        var candidate = item.Candidate;
        var inFilter = Filter switch
        {
            IasLibraryFilter.Current => candidate.InActiveProfile,
            IasLibraryFilter.Standard => candidate.Source == IasLibrarySource.Standard,
            IasLibraryFilter.Vpk => candidate.Source == IasLibrarySource.L4d2Vpk,
            IasLibraryFilter.Body => candidate.Source == IasLibrarySource.CustomModel,
            _ => true,
        };
        return inFilter && candidate.Matches(SearchText);
    }

    private void Rebuild()
    {
        var visible = _all.Where(Visible).ToArray();
        Fill(CurrentItems, visible.Where(item => item.Candidate.InActiveProfile));
        Fill(OtherItems, visible.Where(item => !item.Candidate.InActiveProfile));
        OnPropertyChanged(nameof(CurrentCountText));
        OnPropertyChanged(nameof(OtherCountText));
        NotifyView();
    }

    public string CurrentCountText => CurrentItems.Count.ToString();
    public string OtherCountText => OtherItems.Count.ToString();

    private static void Fill(ObservableCollection<IasLibraryPickerItem> target, IEnumerable<IasLibraryPickerItem> items)
    {
        var next = items.ToArray();
        if (target.SequenceEqual(next)) return;
        target.Clear();
        foreach (var item in next) target.Add(item);
    }

    private void NotifyView()
    {
        OnPropertyChanged(nameof(HasCurrent));
        OnPropertyChanged(nameof(HasOther));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ShowList));
        OnPropertyChanged(nameof(TotalCount));
    }

    private void NotifySelection()
    {
        Fill(Avatars, _selection.Take(MaxAvatars));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(ConfirmText));
    }
}
