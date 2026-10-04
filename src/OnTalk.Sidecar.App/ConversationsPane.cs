using System.Windows;
using System.Windows.Controls;
using OnTalk.Sidecar.Core;

namespace OnTalk.Sidecar.App;

public sealed class ConversationsPane : UserControl
{
    private readonly TextBox _query = new() { Tag = "읽힌 방 이름·표시 참여자에서 찾기" };
    private readonly StackPanel _rows = new();
    private IReadOnlyList<ConversationItem> _items = [];
    private string _signature = "\0";
    public ConversationsPane()
    {
        var body = new StackPanel { Margin = new Thickness(14) };
        body.Children.Add(Label("대화방 찾기", "SectionTitle")); body.Children.Add(_query);
        body.Children.Add(Label("확인한 목록에서 글자를 찾습니다. 미리보기만으로 대화를 요약하지 않습니다. 온톡에서 대화방을 열면 원문 선택·흐름 정리·답변 초안으로 전환합니다.", "Small"));
        body.Children.Add(_rows); Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _query.TextChanged += (_, _) => Render();
    }
    private TextBlock Label(string text, string style) => new() { Text = text, Style = (Style)FindResource(style), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8) };
    public void Refresh(IReadOnlyList<ConversationItem> items)
    {
        var signature = string.Join("\n", items.Select(i => i.Label)); if (_signature == signature && _items.Count == items.Count) return;
        _items = items; _signature = signature; Render();
    }
    private void Render()
    {
        _rows.Children.Clear(); var query = _query.Text.Trim();
        foreach (var item in _items.Where(i => (i.Name + " " + i.Detail).Contains(query, StringComparison.OrdinalIgnoreCase))) _rows.Children.Add(Label(item.Label, "Body"));
        if (_rows.Children.Count == 0) _rows.Children.Add(Label("읽힌 목록에 일치하는 대화방이 없습니다.", "Small"));
        _rows.Children.Add(Label($"관찰한 목록 {_items.Count}건 · 전체 방/전체 참여자 여부는 확인하지 못했습니다.", "Caption"));
    }
}
