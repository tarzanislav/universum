using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.App.Pages;
using Universum.Core.Media;
using Universum.Core.Rules;
using Universum.Core.Templates;

namespace Universum.App.Templates;

/// <summary>One editable slot of a template.</summary>
public sealed class SlotEditRow : ObservableObject
{
    private TrackType _type;
    private Language? _language;
    private TrackRole _role;
    private bool _required;

    public SlotEditRow(TemplateSlot slot)
    {
        _type = slot.Type;
        _language = Languages.Find(slot.Language) ?? Languages.Czech;
        _role = slot.Role;
        _required = slot.Required;
    }

    public IReadOnlyList<Choice<TrackType>> TypeChoices => Texts.TrackTypes;

    public IReadOnlyList<Choice<TrackRole>> RoleChoices => Texts.Roles;

    public IReadOnlyList<Language> LanguageChoices => Languages.All;

    public TrackType Type { get => _type; set => SetProperty(ref _type, value); }

    public Language? Language { get => _language; set => SetProperty(ref _language, value); }

    public TrackRole Role { get => _role; set => SetProperty(ref _role, value); }

    public bool Required { get => _required; set => SetProperty(ref _required, value); }

    public TemplateSlot ToSlot() => new(Type, (Language ?? Languages.Czech).Ietf, Role, Required);
}

/// <summary>Editor of a batch template: name, ordered slots, what happens with unlisted tracks.</summary>
public sealed class TemplateEditorViewModel : ObservableObject
{
    private string _name;
    private SlotEditRow? _selected;
    private bool _removeUnlisted;

    public TemplateEditorViewModel(BatchTemplate template)
    {
        _name = template.Name;
        _removeUnlisted = template.Unlisted == UnlistedTracks.Remove;
        foreach (var slot in template.Slots)
        {
            Rows.Add(new SlotEditRow(slot));
        }

        AddCommand = new RelayCommand(() => Rows.Add(Selected = new SlotEditRow(new TemplateSlot(TrackType.Audio, "cs", TrackRole.Main, false))));
        RemoveCommand = new RelayCommand(() => Rows.Remove(Selected!), () => Selected is not null);
        UpCommand = new RelayCommand(() => Move(-1), () => Selected is not null && Rows.IndexOf(Selected) > 0);
        DownCommand = new RelayCommand(() => Move(1), () => Selected is not null && Rows.IndexOf(Selected) < Rows.Count - 1);
    }

    public ObservableCollection<SlotEditRow> Rows { get; } = [];

    public RelayCommand AddCommand { get; }

    public RelayCommand RemoveCommand { get; }

    public RelayCommand UpCommand { get; }

    public RelayCommand DownCommand { get; }

    public string Name { get => _name; set => SetProperty(ref _name, value); }

    public bool RemoveUnlisted { get => _removeUnlisted; set => SetProperty(ref _removeUnlisted, value); }

    public SlotEditRow? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                RemoveCommand.NotifyCanExecuteChanged();
                UpCommand.NotifyCanExecuteChanged();
                DownCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public BatchTemplate ToTemplate() => new()
    {
        Name = string.IsNullOrWhiteSpace(Name) ? "Šablóna" : Name.Trim(),
        Slots = Rows.Select(r => r.ToSlot()).Distinct().ToList(),
        Unlisted = RemoveUnlisted ? UnlistedTracks.Remove : UnlistedTracks.Keep,
    };

    private void Move(int delta)
    {
        if (Selected is null)
        {
            return;
        }

        var index = Rows.IndexOf(Selected);
        Rows.Move(index, index + delta);
        UpCommand.NotifyCanExecuteChanged();
        DownCommand.NotifyCanExecuteChanged();
    }
}
