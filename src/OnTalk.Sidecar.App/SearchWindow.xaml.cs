using System.Windows;
using System.Windows.Controls;
using OnTalk.Sidecar.AI;
using OnTalk.Sidecar.Core;
using OnTalk.Sidecar.Storage;

namespace OnTalk.Sidecar.App;

/// <summary>검색 결과 한 줄. 쪽지이거나 업무다.</summary>
public sealed record SearchRow(string Title, string Snippet, string Badge, StoredNote? Note, TaskItem? Task);

/// <summary>현재 계정이 보관한 쪽지와 업무를 찾는다. 계정이 바뀌면 패널이 이 창을 닫는다.</summary>
public partial class SearchWindow : Window
{
    // bge-m3에서 맞는 쪽지의 점수가 0.32~0.60, 무관한 쪽지가 0.53까지 나와 점수만으로는 가를 수 없었다
    // (합성 쪽지 8건, 질의 7개). 그래서 낮은 기준으로 넓게 받고 결과에는 "참고"라고 표시한다.
    private const float MinSimilarity = 0.30f;
    private const int SemanticCount = 5;

    private readonly ProfileStore _store;
    private readonly OllamaEmbedder _embedder;
    private CancellationTokenSource? _semanticCancel;

    public SearchWindow(ProfileStore store, OllamaEmbedder embedder)
    {
        InitializeComponent();
        _store = store;
        _embedder = embedder;
        Loaded += (_, _) =>
        {
            QueryBox.Focus();
            Search();
        };
        Closed += (_, _) => _semanticCancel?.Cancel();
    }

    private void OnSearch(object sender, RoutedEventArgs e) => Search();

    private void OnPeriodChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            Search();
        }
    }

    private DateTimeOffset? Since()
    {
        var now = DateTimeOffset.Now.ToOffset(KoreanNoteTime.SeoulOffset);
        return Period.SelectedIndex switch
        {
            1 => now.AddDays(-7),
            2 => now.AddDays(-30),
            3 => new DateTimeOffset(now.Year, 1, 1, 0, 0, 0, KoreanNoteTime.SeoulOffset),
            _ => null,
        };
    }

    private void Search()
    {
        _semanticCancel?.Cancel();
        var range = _store.GetCollectionRange();
        RangeText.Text = range.Since is { } since
            ? $"검색 범위: {since.ToLocalTime():yyyy-MM-dd HH:mm} 이후 이 PC에서 연 쪽지 {range.Count}건. 열어 보지 않은 쪽지는 검색되지 않습니다."
            : "아직 보관한 쪽지가 없습니다. 온톡에서 쪽지를 열면 검색 대상에 들어갑니다.";

        var terms = NoteQuery.SplitTerms(QueryBox.Text);
        var query = new NoteQuery(terms, Since(), AttachmentOnly.IsChecked == true);
        var notes = _store.SearchNotes(query);
        var rows = notes.Select(n => NoteRow(n, terms, terms.Count > 0 ? "글자 일치" : "")).ToList();
        var now = DateTimeOffset.Now;
        rows.AddRange(_store.SearchTasks(terms).Select(t => new SearchRow(
            $"업무 · {t.Title}", new TaskRow(t, now).DueLine, "글자 일치", null, t)));
        Results.ItemsSource = rows;
        StatusText.Text = terms.Count == 0 ? $"최근 쪽지 {rows.Count}건" : $"글자 일치 {rows.Count}건";

        if (terms.Count > 0)
        {
            SearchByMeaning(QueryBox.Text.Trim(), query, rows, notes.Select(n => n.Id).ToHashSet());
        }
    }

    private static SearchRow NoteRow(StoredNote stored, IReadOnlyList<string> terms, string badge)
    {
        var note = stored.Note;
        var when = note.SentAt is { } t ? t.Value.ToString("yyyy-MM-dd") : note.SentAtText;
        return new SearchRow($"{note.SenderName} · {when}", Snippet.Make(note.Body, terms), badge, stored, null);
    }

    private async void SearchByMeaning(string text, NoteQuery query, List<SearchRow> exact, HashSet<long> exactIds)
    {
        var cancel = _semanticCancel = new CancellationTokenSource();
        var baseStatus = StatusText.Text;
        StatusText.Text = $"{baseStatus} · 뜻이 비슷한 쪽지를 찾는 중";
        try
        {
            var vector = await _embedder.EmbedAsync(text, cancel.Token);
            var embeddings = _store.GetEmbeddings(_embedder.Model);
            var similar = Similarity.Top(vector, embeddings.Where(e => !exactIds.Contains(e.NoteId)), SemanticCount, MinSimilarity)
                .Select(hit => (Stored: _store.GetNote(hit.Id), hit.Score))
                .Where(x => x.Stored is not null && Matches(x.Stored, query))
                .Select(x => NoteRow(x.Stored!, [], $"뜻 유사(참고) {x.Score:0.00}"))
                .ToList();
            if (cancel.IsCancellationRequested)
            {
                return;
            }
            var selected = Results.SelectedItem;
            Results.ItemsSource = exact.Concat(similar).ToList();
            Results.SelectedItem = selected;
            var pending = _store.GetCollectionRange().Count - embeddings.Count;
            StatusText.Text = $"{baseStatus} · 뜻이 비슷할 수 있는 쪽지 {similar.Count}건(글자는 일치하지 않음)" +
                              (pending > 0 ? $" (쪽지 {pending}건은 아직 뜻 검색 준비가 안 됐습니다)" : "");
        }
        catch (OperationCanceledException)
        {
        }
        catch (AnalysisUnavailableException ex)
        {
            StatusText.Text = $"{baseStatus} · 뜻 검색은 쓸 수 없습니다: {ex.Message}";
        }
    }

    private static bool Matches(StoredNote stored, NoteQuery query) =>
        (!query.WithAttachmentOnly || stored.Note.AttachmentNames.Count > 0) &&
        (query.Since is not { } since || stored.Note.SentAt?.Value >= since);

    private void OnSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Results.SelectedItem is not SearchRow row)
        {
            return;
        }
        DetailEmpty.Visibility = Visibility.Collapsed;
        Detail.Visibility = Visibility.Visible;
        if (row.Note is { } stored)
        {
            var note = stored.Note;
            DetailTitle.Text = note.SenderName;
            DetailSub.Text = $"{note.SenderDetail}\n" + (note.SentAt switch
            {
                null => note.SentAtText,
                { YearInferred: true } t => $"{t.Value:yyyy-MM-dd HH:mm} (연도는 추정)",
                { } t => $"{t.Value:yyyy-MM-dd HH:mm}",
            });
            DetailNotice.Text = $"{stored.FirstSeenAt.ToLocalTime():yyyy-MM-dd HH:mm}에 보관한 사본입니다. 온톡의 원본은 이미 삭제되었을 수 있습니다.";
            DetailBody.Text = note.Body;
            DetailAttachments.Text = note.AttachmentNames.Count > 0
                ? $"첨부 파일 이름: {string.Join(", ", note.AttachmentNames)}"
                : "";
        }
        else if (row.Task is { } task)
        {
            DetailTitle.Text = task.Title;
            DetailSub.Text = new TaskRow(task, DateTimeOffset.Now).DueLine;
            DetailNotice.Text = task.NoteId is { } id && _store.DescribeNote(id) is { } origin
                ? $"출처 쪽지: {origin}"
                : "직접 추가한 업무입니다.";
            DetailBody.Text = task.Evidence is { } evidence ? $"근거 문장\n{evidence}" : "";
            DetailAttachments.Text = "";
        }
        DetailBody.ScrollToHome();
    }
}
