using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Universum.App.Localization;
using Universum.App.Templates;
using Universum.Core.Batch;
using Universum.Core.Execution;
using Universum.Core.IO;
using Universum.Core.Library;
using Universum.Core.Library.Tmdb;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Rules;
using Universum.Core.Speech;
using Universum.Core.Templates;
using Universum.Core.Text;
using Universum.Platform.Windows;

namespace Universum.App.Pages;

// BatchPageViewModel: the folder chosen and the templates
public sealed partial class BatchPageViewModel
{
    private void ChooseFolder()
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("Priečinok so súbormi MKV") };
        if ((Folder is { } last && Directory.Exists(last) ? last : _services.Settings.InputDirectory) is { } start && Directory.Exists(start))
        {
            dialog.InitialDirectory = start;
        }

        if (dialog.ShowDialog() == true)
        {
            // a folder chosen is analysed alone
            _services.Settings.BatchAllInputs = false;
            Folder = dialog.FolderName;
        }
    }

    private void EditTemplate()
    {
        var model = new TemplateEditorViewModel(Template.Slots.Count == 0 ? Template with { Name = Loc.T("Moja šablóna") } : Template);
        if (new TemplateEditorWindow(model) { Owner = System.Windows.Application.Current.MainWindow }.ShowDialog() == true)
        {
            Template = model.ToTemplate();
        }
    }

    /// <summary>
    /// From the Explorer ("Pridať do Hromadného spracovania" on a file): the file's folder is analysed and the template is
    /// made from the file's group, so files like it are processed like it.
    /// </summary>
    public async Task TemplateFromFileAsync(string path)
    {
        if (Busy)
        {
            return;
        }

        Folder = Path.GetDirectoryName(path);
        await AnalyzeAsync();
        if (Groups.FirstOrDefault(g => g.Group.Files.Any(f => Universum.Core.IO.Paths.Same(f.Path, path))) is { } group)
        {
            SelectedGroup = group;
            TemplateFromGroup();
            RunState = Loc.F("Šablóna je zo súboru {0} (skupina {1}) – súbory ako on sa spracujú rovnako.", Path.GetFileName(path), group.Label);
        }
    }

    private void TemplateFromGroup()
    {
        var item = _items.FirstOrDefault(i => i.Group == SelectedGroup?.Group);
        if (item is not null)
        {
            Template = BatchTemplate.FromTarget(Loc.F("Podľa skupiny {0}", item.Group.Label), item.Target);
        }
    }

    private void LoadTemplate()
    {
        Directory.CreateDirectory(AppSettings.TemplateDirectory);
        var dialog = new OpenFileDialog { Filter = Loc.T("Šablóny UNIVERSUM (*.json)|*.json"), InitialDirectory = AppSettings.TemplateDirectory };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                Template = BatchTemplate.FromJson(File.ReadAllText(dialog.FileName));
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                RunState = Loc.T("Šablónu sa nepodarilo načítať: ") + ex.Message;
            }
        }
    }

    private void SaveTemplate()
    {
        Directory.CreateDirectory(AppSettings.TemplateDirectory);
        var dialog = new SaveFileDialog
        {
            Filter = Loc.T("Šablóny UNIVERSUM (*.json)|*.json"),
            InitialDirectory = AppSettings.TemplateDirectory,
            FileName = string.Concat(Template.Name.Split(Path.GetInvalidFileNameChars())) + ".json",
        };
        if (dialog.ShowDialog() == true)
        {
            File.WriteAllText(dialog.FileName, Template.ToJson());
        }
    }

    private static void TrySave(CachingInspector inspector)
    {
        try
        {
            inspector.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the cache is only an optimisation
        }
    }
}
