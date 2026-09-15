namespace VoiceWink.Models;

/// <summary>
/// Pre-filled App Mode config templates for common Windows applications.
/// Each template provides Name, ProcessPatterns, and a description.
/// </summary>
public static class AppModeTemplates
{
    public static AppModeTemplate[] All =>
    [
        Outlook, Teams, Word, Excel, PowerPoint,
        Chrome, Edge, Firefox, VSCode, VisualStudio,
        Slack, Discord, WhatsApp, WeChat,
        NotepadPlusPlus, Terminal
    ];

    /// <summary>Default configs for fresh installs (Office + chat apps).</summary>
    public static AppModeTemplate[] Defaults =>
    [
        Outlook, Teams, Word, Slack, Discord, WhatsApp, WeChat
    ];

    public static readonly AppModeTemplate Outlook = new()
    {
        Key = "outlook",
        Name = "Microsoft Outlook",
        ProcessPatterns = ["outlook", "olk"],
        Icon = "E715",
        Description = "Classic Outlook and new Outlook for Windows",
        DefaultEnhancementTitle = "E-mail",
        DefaultEnhancementKey = "email"
    };

    public static readonly AppModeTemplate Teams = new()
    {
        Key = "teams",
        Name = "Microsoft Teams",
        ProcessPatterns = ["ms-teams"],
        Icon = "E8BD",
        Description = "Microsoft Teams desktop app",
        DefaultEnhancementTitle = "Chat",
        DefaultEnhancementKey = "chat"
    };

    public static readonly AppModeTemplate Word = new()
    {
        Key = "word",
        Name = "Microsoft Word",
        ProcessPatterns = ["winword"],
        Icon = "E8A5",
        Description = "Microsoft Word document editor",
        DefaultEnhancementTitle = "Improve Transcription",
        DefaultEnhancementKey = "improve-accuracy"
    };

    public static readonly AppModeTemplate Excel = new()
    {
        Key = "excel",
        Name = "Microsoft Excel",
        ProcessPatterns = ["excel"],
        Icon = "E80A",
        Description = "Microsoft Excel spreadsheet editor",
        DefaultEnhancementTitle = "Improve Transcription",
        DefaultEnhancementKey = "improve-accuracy"
    };

    public static readonly AppModeTemplate PowerPoint = new()
    {
        Key = "powerpoint",
        Name = "Microsoft PowerPoint",
        ProcessPatterns = ["powerpnt"],
        Icon = "E8A0",
        Description = "Microsoft PowerPoint presentation editor",
        DefaultEnhancementTitle = "Improve Transcription",
        DefaultEnhancementKey = "improve-accuracy"
    };

    public static readonly AppModeTemplate Chrome = new()
    {
        Key = "chrome",
        Name = "Google Chrome",
        ProcessPatterns = ["chrome"],
        Icon = "E774",
        Description = "Google Chrome web browser"
    };

    public static readonly AppModeTemplate Edge = new()
    {
        Key = "edge",
        Name = "Microsoft Edge",
        ProcessPatterns = ["msedge"],
        Icon = "E774",
        Description = "Microsoft Edge web browser"
    };

    public static readonly AppModeTemplate Firefox = new()
    {
        Key = "firefox",
        Name = "Mozilla Firefox",
        ProcessPatterns = ["firefox"],
        Icon = "E774",
        Description = "Mozilla Firefox web browser"
    };

    public static readonly AppModeTemplate VSCode = new()
    {
        Key = "vscode",
        Name = "Visual Studio Code",
        ProcessPatterns = ["code"],
        Icon = "E943",
        Description = "Visual Studio Code editor"
    };

    public static readonly AppModeTemplate VisualStudio = new()
    {
        Key = "visualstudio",
        Name = "Visual Studio",
        ProcessPatterns = ["devenv"],
        Icon = "E943",
        Description = "Visual Studio IDE"
    };

    public static readonly AppModeTemplate Slack = new()
    {
        Key = "slack",
        Name = "Slack",
        ProcessPatterns = ["slack"],
        Icon = "E8BD",
        Description = "Slack messaging app",
        DefaultEnhancementTitle = "Chat",
        DefaultEnhancementKey = "chat"
    };

    public static readonly AppModeTemplate Discord = new()
    {
        Key = "discord",
        Name = "Discord",
        ProcessPatterns = ["discord"],
        Icon = "E8BD",
        Description = "Discord voice and text chat",
        DefaultEnhancementTitle = "Chat",
        DefaultEnhancementKey = "chat"
    };

    public static readonly AppModeTemplate WhatsApp = new()
    {
        Key = "whatsapp",
        Name = "WhatsApp",
        ProcessPatterns = ["whatsapp", "whatsapp.root"],
        Icon = "E8BD",
        Description = "WhatsApp desktop messenger",
        DefaultEnhancementTitle = "Chat",
        DefaultEnhancementKey = "chat"
    };

    public static readonly AppModeTemplate WeChat = new()
    {
        Key = "wechat",
        Name = "WeChat",
        ProcessPatterns = ["weixin", "wechatappex"],
        Icon = "E8BD",
        Description = "WeChat desktop messenger",
        DefaultEnhancementTitle = "Chat",
        DefaultEnhancementKey = "chat"
    };

    public static readonly AppModeTemplate NotepadPlusPlus = new()
    {
        Key = "notepadplusplus",
        Name = "Notepad++",
        ProcessPatterns = ["notepad++"],
        Icon = "E70F",
        Description = "Notepad++ text editor"
    };

    public static readonly AppModeTemplate Terminal = new()
    {
        Key = "terminal",
        Name = "Windows Terminal",
        ProcessPatterns = ["windowsterminal", "cmd", "powershell", "pwsh"],
        Icon = "E756",
        Description = "Windows Terminal, Command Prompt, and PowerShell"
    };
}

/// <summary>
/// An App Mode template that pre-fills Name and ProcessPatterns for a new config.
/// </summary>
public class AppModeTemplate
{
    /// <summary>Immutable stable identity of this template (UPD-4). Seeded default configs
    /// persist it as <see cref="AppModeConfig.SeedKey"/> so the re-seed matches them across
    /// versions. Never change a key.</summary>
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string[] ProcessPatterns { get; set; } = [];
    public string Icon { get; set; } = "E7AC";
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Title of the enhancement prompt to link by default (e.g. "Chat", "E-mail").
    /// Display/legacy only — link RESOLUTION now goes through <see cref="DefaultEnhancementKey"/>
    /// (UPD-4, Codex r2): a title can match duplicates or a renamed prompt, a key can't.
    /// </summary>
    public string? DefaultEnhancementTitle { get; set; }

    /// <summary>
    /// The <see cref="TemplatePrompt.Key"/> of the default enhancement prompt to link. The
    /// re-seed resolves this to the prompt's runtime Id via a unique SeedKey→Id index; a missing
    /// target yields a null link (never a title/duplicate fallback). Null = no default link.
    /// </summary>
    public string? DefaultEnhancementKey { get; set; }

    public AppModeConfig ToAppModeConfig(string? linkedEnhancementId = null) => new()
    {
        SeedKey = Key,          // UPD-4 provenance
        Name = Name,
        ProcessPatterns = (string[])ProcessPatterns.Clone(),
        LinkedEnhancementId = linkedEnhancementId,
        IsEnabled = false
    };
}
