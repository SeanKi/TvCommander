namespace MPCommander.Views;

/// <summary>XAML 템플릿 안처럼 LocUi.Apply 가 닿지 않는 곳의 글자 ({x:Static local:UiText.이름} 으로 쓴다)</summary>
public static class UiText
{
    public static string BlockedFileTip => Loc.T("인터넷에서 받은 파일 (차단됨) — Ctrl+Shift+U 로 차단 해제");
}
