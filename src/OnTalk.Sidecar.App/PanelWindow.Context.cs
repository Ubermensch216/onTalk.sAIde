using System.Windows;
using System.Windows.Controls;
using OnTalk.Sidecar.Automation;
using OnTalk.Sidecar.Core;

namespace OnTalk.Sidecar.App;

public partial class PanelWindow
{
    private PeoplePane? _peoplePane;
    private string? _peoplePaneKey;
    private ChatPane? _chatPane;
    private string? _chatPaneKey;
    private ConversationsPane? _conversationsPane;
    private void ApplyCurrentContext(WatchSnapshot snapshot)
    {
        if (_store is null || snapshot.AccountCandidate != _store.LoginId) return;
        var current = snapshot.ForTarget(_dockedTo);
        if (current is null)
        {
            _context = null;
            _contextViewKey = null;
            ShowEmpty("현재 온톡 창의 작업을 확인하는 중입니다. 다른 창의 쪽지는 표시하지 않습니다.");
            return;
        }
        var context = current.Context;
        if (context.Surface != OnTalkSurface.Main)
        {
            _manualMenu = MainMenu.Unknown;
            _manualMenuWindow = IntPtr.Zero;
            MenuPicker.SelectedIndex = 0;
        }
        _context = current;
        if (context.Surface == OnTalkSurface.Main && context.Menu != MainMenu.Unknown)
        {
            _manualMenu = MainMenu.Unknown;
            _manualMenuWindow = IntPtr.Zero;
            MenuPicker.SelectedIndex = 0;
        }
        if (context.Surface == OnTalkSurface.Main && context.Menu == MainMenu.Unknown && _manualMenuWindow == current.Hwnd)
            context = context with { Menu = _manualMenu };
        if (context.Menu == MainMenu.Excluded) context = context with { Surface = OnTalkSurface.Excluded };
        var key = $"{current.SessionId}:{context.Surface}:{context.Menu}:{context.ComposeKind}";
        var changed = key != _contextViewKey;
        if (changed)
        {
            ShowEmpty("");
            _contextViewKey = key;
        }
        var guide = SidecarPolicy.Describe(context);
        SetStoreButtons(context.Surface != OnTalkSurface.Login);
        ContextHeader.Visibility = Visibility.Visible;
        ContextTitle.Text = guide.Title;
        WindowSubtitle.Text = guide.Title;
        ComposeButton.ToolTip = context.Surface == OnTalkSurface.NoteCompose ? "확장 작성 도우미" : "새 초안";
        ContextHint.Text = current.Context.Menu == MainMenu.Unknown && context.Menu != MainMenu.Unknown
            ? "수동으로 지정한 메뉴 · 온톡 선택 대상은 변경하지 않습니다."
            : "현재 부착된 온톡 창의 작업";
        ManualMenuPanel.Visibility = current.Context is { Surface: OnTalkSurface.Main, Menu: MainMenu.Unknown }
            ? Visibility.Visible : Visibility.Collapsed;
        MenuPicker.IsEnabled = current.Context.Menu == MainMenu.Unknown;

        if (context.Surface == OnTalkSurface.NoteRead)
        {
            var note = snapshot.Notes.FirstOrDefault(n => n.Hwnd == current.Hwnd && n.SessionId == current.SessionId)?.Content.Note;
            if (note is not null) ShowNote(note, _saved.GetValueOrDefault(current.Hwnd));
            else
            {
                ShowEmpty("현재 쪽지의 내용을 읽지 못했습니다. 온톡 화면 구성을 확인하세요.");
                ContextHeader.Visibility = Visibility.Visible;
                ContextTitle.Text = guide.Title;
            }
            return;
        }
        if (context.Surface == OnTalkSurface.NoteCompose)
        {
            var state = snapshot.Notes.FirstOrDefault(n => n.Hwnd == current.Hwnd && n.SessionId == current.SessionId)?.Content.Compose;
            if (state is null) { ShowEmpty("작성창을 끝까지 읽지 못해 편집·반영을 제공하지 않습니다."); return; }
            var slot = ContextDrafts.Slot(current.SessionId, context.ComposeKind);
            if (!_composePanes.TryGetValue(slot, out var pane))
            {
                pane = new NoteComposePane(_store, _writer, current.Hwnd, current.SessionId, context.ComposeKind, state);
                _composePanes.Add(slot, pane);
            }
            if (!ReferenceEquals(_activePane, pane)) _activePane?.SetActive(false);
            _activePane = pane;
            pane.Refresh(state);
            pane.SetActive(_composeWindow?.BoundSession != current.SessionId);
            ComposeHost.Content = pane;
            EmptyState.Visibility = ContextView.Visibility = NoteView.Visibility = Visibility.Collapsed;
            ComposeHost.Visibility = Visibility.Visible;
            return;
        }

        _activePane?.SetActive(false);
        if (context.Surface == OnTalkSurface.Chat)
        {
            if (current.Chat is not { } chat) { ShowEmpty("현재 대화 텍스트를 읽지 못했습니다."); return; }
            if (_chatPane is null || _chatPaneKey != key)
            {
                _chatPane?.Dispose();
                _chatPane = new ChatPane(_store, _analyzer, _writer, current.SessionId, chat, () => { UpdateFooter(); _tasksWindow?.Reload(); });
                _chatPaneKey = key;
            }
            _chatPane.Refresh(chat); _chatPane.SetActive(true);
            ChatHost.Content = _chatPane; ChatHost.Visibility = Visibility.Visible;
            EmptyState.Visibility = NoteView.Visibility = ComposeHost.Visibility = PeopleHost.Visibility = ContextView.Visibility = Visibility.Collapsed;
            return;
        }
        _chatPane?.SetActive(false);
        if (context.Surface == OnTalkSurface.Main && context.Menu == MainMenu.Conversations)
        {
            _conversationsPane ??= new ConversationsPane();
            _conversationsPane.Refresh(current.Conversations);
            ChatHost.Content = _conversationsPane; ChatHost.Visibility = Visibility.Visible;
            EmptyState.Visibility = NoteView.Visibility = ComposeHost.Visibility = PeopleHost.Visibility = ContextView.Visibility = Visibility.Collapsed;
            return;
        }
        if (context.Surface == OnTalkSurface.Main && context.Menu is MainMenu.Friends or MainMenu.Organization)
        {
            if (_peoplePane is null || _peoplePaneKey != key)
            {
                _peoplePane?.Dispose();
                _peoplePane = new PeoplePane(_store, context.Menu,
                    preparation => OpenCompose(null, preparation: preparation), OpenContactCandidate, _writer);
                _peoplePaneKey = key;
            }
            _peoplePane.Refresh(current.People ?? new PeopleSnapshot(context.Menu, [], [], [], DateTimeOffset.Now));
            _peoplePane.SetActive(true);
            PeopleHost.Content = _peoplePane;
            EmptyState.Visibility = NoteView.Visibility = ComposeHost.Visibility = ContextView.Visibility = Visibility.Collapsed;
            PeopleHost.Visibility = Visibility.Visible;
            return;
        }
        PeopleHost.Visibility = Visibility.Collapsed;
        ChatHost.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = NoteView.Visibility = ComposeHost.Visibility = Visibility.Collapsed;
        ContextView.Visibility = Visibility.Visible;
        ContextPurpose.Text = guide.Purpose;
        ContextTools.Text = guide.Tools;
        ContextScope.Text = guide.Scope;
        LocalGroupsPanel.Visibility = context.Surface == OnTalkSurface.Main && context.Menu == MainMenu.Friends
            ? Visibility.Visible : Visibility.Collapsed;
        if (changed && LocalGroupsPanel.Visibility == Visibility.Visible)
        {
            ContextGroups.ItemsSource = _store.GetGroups().Select(g => new GroupItem(g)).ToList();
            ContextMembers.Text = "그룹을 고르면 이름·직급·소속을 확인할 수 있습니다.";
        }
        var canPrepare = context.Surface == OnTalkSurface.Main && context.Menu is MainMenu.Friends or MainMenu.Organization or MainMenu.Notes;
        ContextPrimary.Visibility = canPrepare ? Visibility.Visible : Visibility.Collapsed;
        ContextPrimary.Content = context.Menu == MainMenu.Organization ? "문의 쪽지 준비" : "새 쪽지 준비";
        ContextSearch.Visibility = context.Surface == OnTalkSurface.Main ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnManualMenuChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_context?.Context is not { Surface: OnTalkSurface.Main, Menu: MainMenu.Unknown } ||
            MenuPicker.SelectedItem is not ComboBoxItem { Tag: string tag } || !Enum.TryParse<MainMenu>(tag, out var menu)) return;
        _manualMenu = menu;
        _manualMenuWindow = _dockedTo;
        if (_latestSnapshot is { } latest) ApplyCurrentContext(latest);
    }

    private void OnContextGroupChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ContextGroups.SelectedItem is GroupItem item)
            ContextMembers.Text = string.Join("\n", item.Group.Members.Select(m => $"{m.Name} · {m.Title}\n{string.Join(" · ", m.Details)}"));
    }

    private void OnContextPrimary(object sender, RoutedEventArgs e) => OpenCompose(null);

    private void OpenContactCandidate(StoredNote note)
    {
        OnOpenSearch(this, new RoutedEventArgs());
        _searchWindow?.ShowContactCandidate(note);
    }
}
