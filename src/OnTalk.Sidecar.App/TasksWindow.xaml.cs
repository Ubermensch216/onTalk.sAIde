using System.Windows;
using System.Windows.Controls;
using OnTalk.Sidecar.Storage;

namespace OnTalk.Sidecar.App;

/// <summary>현재 계정의 모든 업무. 계정이 바뀌면 패널이 이 창을 닫는다.</summary>
public partial class TasksWindow : Window
{
    private readonly ProfileStore _store;
    private readonly Action _changed;
    private bool _loading;

    public TasksWindow(ProfileStore store, Action changed)
    {
        InitializeComponent();
        _store = store;
        _changed = changed;
        Reload();
    }

    public void Reload()
    {
        var now = DateTimeOffset.Now;
        var all = _store.GetTasks();
        var shown = all.Where(t => ShowDone.IsChecked == true || !t.Done)
            .Select(t => new TaskRow(t, now, t.NoteId is { } id ? _store.DescribeNote(id) : null))
            .ToList();
        // 목록을 다시 채울 때 날짜 선택기가 내는 변경 이벤트를 저장으로 처리하지 않는다.
        _loading = true;
        TaskList.ItemsSource = shown;
        _loading = false;
        EmptyBox.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = $"열린 업무 {all.Count(t => !t.Done)}건 · 완료 {all.Count(t => t.Done)}건";
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e) => Reload();

    private void OnToggle(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: long id } box)
        {
            _store.SetTaskDone(id, box.IsChecked == true, DateTimeOffset.Now);
            Changed();
        }
    }

    private void OnDueChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || sender is not DatePicker { Tag: long id, IsLoaded: true } picker)
        {
            return;
        }
        _store.SetTaskDue(id, picker.SelectedDate is { } date ? DateOnly.FromDateTime(date) : null);
        Changed();
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: long id } &&
            Dialogs.Show(this, "이 업무를 삭제할까요? 쪽지 원문은 그대로 남습니다.", "업무 삭제",
                MessageBoxButton.OKCancel) == MessageBoxResult.OK)
        {
            _store.DeleteTask(id);
            Changed();
        }
    }

    private void Changed()
    {
        // 이벤트 처리 도중에 목록을 갈아 끼우지 않도록 뒤로 미룬다.
        Dispatcher.BeginInvoke(() =>
        {
            Reload();
            _changed();
        });
    }
}
