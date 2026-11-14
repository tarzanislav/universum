using System.IO;
using System.Net.Http;
using System.Text.Json;
using Universum.Core.Execution;
using Universum.Core.Library;
using Universum.Core.Library.Histra;
using Universum.Core.Library.Tmdb;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Rules;
using Universum.Core.Processes;
using Universum.Core.Speech;
using Universum.Core.Subtitles;
using Universum.Core.Weather;
using Universum.Platform.Windows;
using Universum.App.Localization;

namespace Universum.App;

// AppServices: the agenda (Kalendár, Úlohy, Poznámky), the fonts of the reports, the voice
public sealed partial class AppServices
{
    /// <summary>The records of POZNÁMKY – Kalendár, Plánované úlohy, Poznámky (owner, 31. 10. 2026): one store in the folder of the settings.</summary>
    public Universum.Core.Agenda.AgendaStore Agenda => _agenda ??= new(Path.Combine(AppSettings.Directory, "planovac", "zaznamy.json"));

    private Universum.Core.Agenda.AgendaStore? _agenda;

    /// <summary>The font of printed reports and their PDF: the first face of Windows whose licence allows embedding; null: none.</summary>
    public Universum.Core.Reports.ReportFonts? ReportFonts => ReportFontsForSelfTest ?? (_reportFonts ??=
        Universum.Core.Reports.ReportFonts.In(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), Universum.Core.Reports.ReportFonts.WindowsFaces));

    public Universum.Core.Reports.ReportFonts? ReportFontsForSelfTest { get; set; }

    private Universum.Core.Reports.ReportFonts? _reportFonts;

    /// <summary>The voice of notes: recording and writing it down (POZNÁMKY).</summary>
    public Pages.VoiceRecorder Voice => _voice ??= new(this);

    private Pages.VoiceRecorder? _voice;
}
