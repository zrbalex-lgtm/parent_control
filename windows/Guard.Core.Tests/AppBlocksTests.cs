using ParentControl.Core;

namespace ParentControl.Core.Tests;

public class AppBlocksTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 17, 0, 0, TimeSpan.Zero);
    private static readonly Guid Kid = Guid.NewGuid();

    private static TrayBlock Block(string group, params AppRule[] rules) => new(group, Now.AddHours(2), [.. rules]);

    [Fact]
    public void Contains_rule_is_case_insensitive_on_the_chosen_field()
    {
        var r = new AppRule("title", "contains", "youtube");
        Assert.True(AppBlocks.Matches(r, "chrome.exe", null, "Funny cats - YouTube - Google Chrome"));
        Assert.False(AppBlocks.Matches(r, "youtube.exe", null, "Something else"));
        Assert.False(AppBlocks.Matches(r, "chrome.exe", null, null));
    }

    [Fact]
    public void Regex_rule_understands_postgres_word_boundaries()
    {
        var r = new AppRule("exe", "regex", @"^(javaw|minecraft)\.exe$");
        Assert.True(AppBlocks.Matches(r, "javaw.exe", null, null));
        Assert.True(AppBlocks.Matches(r, "Minecraft.exe", null, null));
        Assert.False(AppBlocks.Matches(r, "notjavaw.exe", null, null));
        var word = new AppRule("title", "regex", @"\yroblox\y");
        Assert.True(AppBlocks.Matches(word, null, null, "Roblox"));
        Assert.False(AppBlocks.Matches(word, null, null, "robloxian"));
    }

    [Fact]
    public void Broken_regex_matches_nothing()
    {
        Assert.False(AppBlocks.Matches(new AppRule("title", "regex", "(unclosed"), null, null, "(unclosed"));
    }

    [Fact]
    public void Browser_sites_close_the_tab_other_apps_close_the_app()
    {
        Assert.Equal(BlockAction.CloseTab, AppBlocks.ActionFor("msedge.exe"));
        Assert.Equal(BlockAction.CloseTab, AppBlocks.ActionFor("Chrome.exe"));
        Assert.Equal(BlockAction.CloseApp, AppBlocks.ActionFor("RobloxPlayerBeta.exe"));
    }

    [Fact]
    public void Windows_shell_is_never_touched()
    {
        var blocks = new[] { Block("Всё", new AppRule("title", "contains", "")), Block("Проводник", new AppRule("exe", "contains", "explorer")) };
        Assert.Null(AppBlocks.Match(blocks, "explorer.exe", null, "Downloads", Now));
    }

    [Fact]
    public void Match_ignores_expired_blocks()
    {
        var expired = new TrayBlock("YouTube", Now.AddMinutes(-1), [new AppRule("title", "contains", "youtube")]);
        Assert.Null(AppBlocks.Match([expired], "chrome.exe", null, "YouTube", Now));
        Assert.Equal("YouTube", AppBlocks.Match([Block("YouTube", new AppRule("title", "contains", "youtube"))], "chrome.exe", null, "YouTube", Now)?.Group);
    }

    [Fact]
    public void Active_blocks_are_per_child_latest_per_group_and_need_rules()
    {
        var other = Guid.NewGuid();
        var rule = new AppRule("title", "contains", "youtube");
        var list = AppBlocks.ActiveFor([
            new AppBlockInfo(Kid, "YouTube", Now.AddHours(1), [rule]),
            new AppBlockInfo(Kid, "YouTube", Now.AddHours(3), [rule]),
            new AppBlockInfo(Kid, "Minecraft", Now.AddMinutes(-5), [rule]),
            new AppBlockInfo(Kid, "Пустая", Now.AddHours(1), []),
            new AppBlockInfo(other, "Roblox", Now.AddHours(1), [rule]),
        ], Kid, Now);
        var only = Assert.Single(list);
        Assert.Equal("YouTube", only.Group);
        Assert.Equal(Now.AddHours(3), only.Until);
    }

    [Fact]
    public void Sync_answer_with_blocks_parses()
    {
        const string json = """
            {"server_now":"2026-11-24T17:00:00Z","app_blocks":[{"child_id":"11111111-1111-1111-1111-111111111111",
             "group_name":"YouTube","until":"2026-11-24T19:00:00+00:00",
             "rules":[{"match_field":"title","match_type":"contains","pattern":"youtube"}]}]}
            """;
        var r = System.Text.Json.JsonSerializer.Deserialize<SyncResponse>(json, Json.Options)!;
        var b = Assert.Single(r.AppBlocks);
        Assert.Equal("YouTube", b.GroupName);
        Assert.Equal("youtube", Assert.Single(b.Rules).Pattern);
    }
}
