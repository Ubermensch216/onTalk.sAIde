using OnTalk.Sidecar.Core;

namespace OnTalk.Sidecar.Tests;

public sealed class SentenceCandidatesTests
{
    private static readonly DateOnly Sent = new(2026, 10, 2);

    [Fact]
    public void 할_일로_보이는_문장을_원문_그대로_추린다()
    {
        const string body = "안녕하세요.\n감사위원회 직원은 연간 56시간 이상 감사교육 이수해야 합니다.\n결과는 10월 15일까지 제출 바랍니다.\n감사합니다.";

        var result = SentenceCandidates.Find(body, Sent);

        Assert.Equal("", result.Summary);
        Assert.Equal(2, result.Actions.Count);
        Assert.All(result.Actions, a => Assert.Contains(a.Evidence, body));
        // 기간("56시간")은 기한이 아니다.
        Assert.Null(result.Actions[0].DueText);
        Assert.Null(result.Actions[0].Due);
        Assert.Equal("10월 15일까지", result.Actions[1].DueText);
        Assert.Equal(new DateOnly(2026, 10, 15), result.Actions[1].Due);
    }

    [Fact]
    public void 기간으로_적힌_기한은_끝_날짜를_쓴다()
    {
        var action = Assert.Single(SentenceCandidates.Find("10. 5.~10. 16.(금)까지 신청 바랍니다.", Sent).Actions);

        Assert.Equal(new DateOnly(2026, 10, 16), action.Due);
    }

    [Fact]
    public void 할_일_문장이_없으면_후보도_없다()
    {
        Assert.Empty(SentenceCandidates.Find("안녕하세요.\n회의실이 3층으로 바뀌었습니다.\n평균 3.5점입니다.", Sent).Actions);
    }

    [Fact]
    public void 같은_문장은_한_번만_넣고_긴_문장은_제목만_줄인다()
    {
        var line = "확인 " + new string('가', 150);

        var action = Assert.Single(SentenceCandidates.Find($"{line}\n{line}", Sent).Actions);

        Assert.Equal(line, action.Evidence);
        Assert.Equal(101, action.Title.Length);
    }
}
