using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Universum.Core.Execution;
using Universum.Core.Mega;
using Universum.Core.Rules;

namespace Universum.App.Pages;

/// <summary>
/// Sending files of Single spracovanie and results of Zlučovanie to MEGA spracovanie (owner, 1. 11. 2026): into a new
/// batch or to complete a waiting one – the same choice as in Hromadné spracovanie. Their originals are always kept.
/// </summary>
public sealed class MegaSender : ObservableObject
{
    private readonly AppServices _services;
    private MegaTarget? _selected;

    public MegaSender(AppServices services)
    {
        _services = services;
        services.MegaChanged += () => Jobs.JobRow.OnUi(Refresh);
        Refresh();
    }

    /// <summary>"do novej dávky", "doplniť Dávka 2: …".</summary>
    public ObservableCollection<MegaTarget> Targets { get; } = [];

    public MegaTarget? Selected { get => _selected; set => SetProperty(ref _selected, value); }

    /// <summary>The batch sent to last (the self-test).</summary>
    public MegaBatch? Last { get; private set; }

    public void Refresh()
    {
        var chosen = _selected?.Batch;
        Targets.Clear();
        Targets.Add(new MegaTarget("do novej dávky", null));
        foreach (var batch in _services.Mega.Batches.Where(b => b.State is MegaBatchState.Waiting or MegaBatchState.Missed && !b.IsBeingEdited))
        {
            Targets.Add(new MegaTarget($"doplniť {batch.Label}: {batch.Name}", batch));
        }

        Selected = Targets.FirstOrDefault(t => t.Batch == chosen) ?? Targets[0];
    }

    /// <summary>Sends the files; what happened, in words.</summary>
    public string Send(IReadOnlyList<MegaFile> files, string name, string libraryRoot, RuleProfile profile)
    {
        try
        {
            string said;
            if (Selected?.Batch is { } batch)
            {
                if (batch.State is not (MegaBatchState.Waiting or MegaBatchState.Missed) || batch.IsBeingEdited)
                {
                    return $"{batch.Label} už nečaká (spúšťa sa alebo sa upravuje) – zvoľte inú dávku alebo novú.";
                }

                var (added, changed) = _services.Mega.Put(batch, files);
                if (added + changed == 0)
                {
                    return "Súbor už je v inej dávke MEGA spracovania.";
                }

                Last = batch;
                said = changed > 0 ? $"V {batch.Label} sa súbor nahradil novými rozhodnutiami." : $"Pridané do {batch.Label} MEGA spracovania.";
            }
            else if (_services.Mega.Add(name, libraryRoot, profile, OriginalsMode.Keep, files) is { } created)
            {
                Last = created;
                said = $"Odoslané do MEGA spracovania ako {created.Label}.";
            }
            else
            {
                return "Súbor už je v niektorej dávke MEGA spracovania.";
            }

            _services.NotifyMegaChanged();
            return said + " Zapíše sa, keď dávku spustíte alebo v nastavenom čase; originál ostane.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return "Do MEGA sa to nepodarilo uložiť: " + ex.Message;
        }
    }
}
